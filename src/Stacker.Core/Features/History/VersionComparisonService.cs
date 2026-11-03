using Stacker.Core.Framework;
namespace Stacker.Core;

public sealed class VersionComparisonService(IGitRepositoryReader git, IHistoryGit objects)
{
    public async Task<VersionComparison> CompareAsync(string scope, ChangeVersion previous, ChangeVersion current, CancellationToken ct = default)
    {
        if (previous.ChangeId != current.ChangeId) throw new StackerException("Versions belong to different changes.");
        try
        {
            if (previous.MergeBase is null || current.MergeBase is null) throw new StackerException("Version contents or merge-base are unavailable. Open both versions first.");
            var root = (await objects.RootAsync(scope, ct)).OrThrow();
            var basis = (await git.FilesAsync(root, previous.MergeBase, current.MergeBase, ct)).OrThrow();
            var expected = previous.MergeBase == current.MergeBase ? previous.HeadSha
                : (await objects.TransplantAsync(root, previous.MergeBase, previous.HeadSha, current.MergeBase, ct)).OrThrow();
            if (expected is null) throw new StackerException("Old patch conflicts with the new base. Compare the original versions manually.");
            var changes = (await git.FilesAsync(root, expected, current.HeadSha, ct)).OrThrow();
            return new(previous, current, expected, changes, basis, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (StackerException ex) { return new(previous, current, null, [], [], ex.Message); }
    }
}

public sealed class ReviewProgressService(IHistoryStore store, VersionComparisonService comparisons)
{
    public async Task<IReadOnlyList<FileReviewStatus>> StatusAsync(string scope, ChangeVersion current, IReadOnlyList<FileChange> files, CancellationToken ct = default)
    {
        var checkpoints = (await store.CheckpointsAsync(current.ChangeId, ct)).OrThrow().ToDictionary(c => c.Path);
        var versions = (await store.VersionsAsync(current.ChangeId, ct)).OrThrow().ToDictionary(v => v.Id);
        var results = new List<FileReviewStatus>(); var cache = new Dictionary<string, VersionComparison>();
        foreach (var file in files)
        {
            if (!checkpoints.TryGetValue(file.NewPath, out var checkpoint) || !versions.TryGetValue(checkpoint.VersionId, out var old))
            { results.Add(new(file, FileReviewState.Unreviewed, null)); continue; }
            if (old.Id == current.Id) { results.Add(new(file, FileReviewState.Reviewed, old)); continue; }
            if (!cache.TryGetValue(old.Id, out var comparison)) cache[old.Id] = comparison = await comparisons.CompareAsync(scope, old, current, ct);
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
