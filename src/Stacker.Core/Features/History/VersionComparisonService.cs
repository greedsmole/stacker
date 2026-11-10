using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Compares two versions of one change as text, normalized onto the current base.</summary>
/// <remarks>Normalization is what makes the result attributable: it separates what landed underneath the layer from what the
/// author changed, so a rebase cannot be mistaken for a new edit. A comparison needs the same change on both sides and
/// recorded merge-base evidence, because without an ancestor the base an old patch belongs to is simply unknown. Expected
/// failures — missing evidence or a replay that does not apply — are returned as an unreliable comparison instead of being
/// thrown, so the caller can explain itself; cancellation and genuinely unexpected failures still escape. The call reads Git
/// objects and never checks out, stages or resets.</remarks>
public sealed class VersionComparisonService(IGitRepositoryReader git, IHistoryGit objects)
{
    /// <summary>Builds the base and code sides of the difference between two versions of the same change.</summary>
    /// <remarks>Order of work: require both merge bases, take a content lease over the scope's pins, then diff the two merge
    /// bases to obtain <see cref="VersionComparison.BaseChanges"/>. The expected head is the previous head itself when both
    /// versions share a merge base, otherwise the old patch replayed onto the current base; a replay that conflicts makes the
    /// comparison unreliable with a message telling the user to compare the originals manually. Finally the expected head is
    /// diffed against the current head for <see cref="VersionComparison.CodeChanges"/>. A <see cref="StackerException"/> from
    /// any step becomes <see cref="VersionComparison.Unavailable"/>; any other exception still fails loudly.</remarks>
    /// <param name="repository">Local repository whose object database holds both versions; passed explicitly so no
    /// substitute clone or worktree can be used.</param>
    /// <param name="scope">History scope whose pinned objects are leased for the duration of the comparison.</param>
    /// <param name="previous">Older version, normally the one the reviewer checked.</param>
    /// <param name="current">Version being reviewed now.</param>
    /// <param name="ct">Cancellation for the lease and the Git reads.</param>
    /// <returns>A reliable comparison, or one carrying the reason it could not be produced.</returns>
    /// <exception cref="StackerException">Thrown when the versions belong to different changes; a diff across two changes has
    /// no meaning, so it is refused before any object is read.</exception>
    public async Task<VersionComparison> CompareAsync(RepositoryIdentity repository, string scope, ChangeVersion previous, ChangeVersion current, CancellationToken ct = default)
    {
        if (previous.ChangeId != current.ChangeId) throw new StackerException("Versions belong to different changes.");
        try
        {
            if (previous.MergeBase is null || current.MergeBase is null) throw new StackerException("Version contents or merge-base are unavailable. Open both versions first.");
            using var lease = (await objects.AcquireAsync(repository, scope, ct)).OrThrow();
            var root = lease.Root;
            var basis = (await git.FilesAsync(root, previous.MergeBase, current.MergeBase, ct)).OrThrow();
            var expected = previous.MergeBase == current.MergeBase ? previous.HeadSha
                : (await objects.TransplantAsync(repository, previous.MergeBase, previous.HeadSha, current.MergeBase, ct)).OrThrow();
            if (expected is null) throw new StackerException("Old patch conflicts with the new base. Compare the original versions manually.");
            var changes = (await git.FilesAsync(root, expected, current.HeadSha, ct)).OrThrow();
            return new(previous, current, expected, changes, basis, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (StackerException ex) { return new(previous, current, null, [], [], ex.Message); }
    }
}

/// <summary>Classifies each file of the current layer against the reviewer's checkpoints.</summary>
/// <remarks>Progress is a comparison, not a list of touched files: a file stays <see cref="FileReviewState.Reviewed"/>
/// until something the reviewer did not see changed. The checks run from "no answer" through "the code changed" to "only the
/// context moved", so the strongest applicable evidence decides, and blockers are attached afterwards, independently of the
/// state. Comparisons are cached per checked version, so a layer whose files share one checkpoint does not replay the same
/// patch once per file.</remarks>
public sealed class ReviewProgressService(IHistoryStore store, VersionComparisonService comparisons)
{
    /// <summary>Returns one verdict per supplied file, in the same order.</summary>
    /// <remarks>Per file, the first matching rule wins. No checkpoint for the path — or a checkpoint whose version is no
    /// longer among the stored versions — is unreviewed, because there is nothing left to compare from. A checkpoint taken on
    /// the current version is reviewed without comparing anything. Otherwise the checked version is compared with the current
    /// one: an unreliable comparison is unavailable; a code-change entry whose new path is this file's new path, or whose old
    /// path is this file's old path, is metadata-changed when that entry is a rename or a mode-only change and code-changed
    /// otherwise; a different base or merge base without such an entry is base-changed; and nothing at all is reviewed. Blocking
    /// questions are then attached by new path, so a blocker is reported even on a reviewed file.</remarks>
    /// <param name="repository">Local repository that holds the compared objects.</param>
    /// <param name="scope">History scope used for the content lease inside each comparison.</param>
    /// <param name="current">Version currently on screen; checkpoints are compared against it.</param>
    /// <param name="files">Files of the current layer, in display order.</param>
    /// <param name="ct">Cancellation for the store reads and the comparisons.</param>
    /// <returns>One status per input file, in input order, with blocker text where one exists.</returns>
    public async Task<IReadOnlyList<FileReviewStatus>> StatusAsync(RepositoryIdentity repository, string scope, ChangeVersion current, IReadOnlyList<FileChange> files, CancellationToken ct = default)
    {
        var checkpoints = (await store.CheckpointsAsync(current.ChangeId, ct)).OrThrow().ToDictionary(c => c.Path);
        var versions = (await store.VersionsAsync(current.ChangeId, ct)).OrThrow().ToDictionary(v => v.Id);
        var results = new List<FileReviewStatus>(); var cache = new Dictionary<string, VersionComparison>();
        foreach (var file in files)
        {
            if (!checkpoints.TryGetValue(file.NewPath, out var checkpoint) || !versions.TryGetValue(checkpoint.VersionId, out var old))
            { results.Add(new(file, FileReviewState.Unreviewed, null)); continue; }
            if (old.Id == current.Id) { results.Add(new(file, FileReviewState.Reviewed, old)); continue; }
            if (!cache.TryGetValue(old.Id, out var comparison)) cache[old.Id] = comparison = await comparisons.CompareAsync(repository, scope, old, current, ct);
            var state = !comparison.IsReliable ? FileReviewState.Unavailable
                : comparison.CodeChanges.Any(f => f.NewPath == file.NewPath || f.OldPath == file.OldPath) ? (comparison.CodeChanges.Any(f => (f.NewPath == file.NewPath || f.OldPath == file.OldPath) && (f.ModeChanged || f.Kind == FileChangeKind.Renamed)) ? FileReviewState.MetadataChanged : FileReviewState.CodeChanged)
                : old.BaseSha != current.BaseSha || old.MergeBase != current.MergeBase ? FileReviewState.BaseChanged
                : FileReviewState.Reviewed;
            results.Add(new(file, state, old));
        }
        var concerns = (await store.ConcernsAsync(current.ChangeId, ct)).OrThrow().ToDictionary(c => c.Path);
        return results.Select(status => status with { Concern = concerns.GetValueOrDefault(status.Change.NewPath)?.Text }).ToArray();
    }
}
