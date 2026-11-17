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
        if (previous.ChangeId != current.ChangeId)
            throw new StackerException("Versions belong to different changes.");
        try
        {
            if (previous.MergeBase is null || current.MergeBase is null)
                throw new StackerException("Version contents or merge-base are unavailable. Open both versions first.");
            using var lease = (await objects.AcquireAsync(repository, scope, ct)).OrThrow();
            var root = lease.Root;
            var basis = (await git.FilesAsync(root, previous.MergeBase, current.MergeBase, ct)).OrThrow();
            var expected = previous.MergeBase == current.MergeBase ? previous.HeadSha
                : (await objects.TransplantAsync(repository, previous.MergeBase, previous.HeadSha, current.MergeBase, ct)).OrThrow();
            if (expected is null)
                throw new StackerException("Old patch conflicts with the new base. Compare the original versions manually.");
            var changes = (await git.FilesAsync(root, expected, current.HeadSha, ct)).OrThrow();
            return new(previous, current, expected, changes, basis, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (StackerException ex) { return new(previous, current, null, [], [], ex.Message); }
    }
}
