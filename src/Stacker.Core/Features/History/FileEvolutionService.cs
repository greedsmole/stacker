namespace Stacker.Core;

/// <summary>Reconstructs how one selected file travelled through a captured stack, layer by layer.</summary>
/// <remarks>The walk is deliberately limited to the captured path the user is looking at: ancestry is never guessed from commit
/// messages or similarity, and the walk stops where a file identity provably begins. Renames are followed backwards; an added
/// or copied file starts a new identity, because the older text still exists under the original name. Line attribution is only
/// attempted across boundaries that are proven continuous.</remarks>
public sealed class FileEvolutionService(IGitRepositoryReader git)
{
    /// <summary>Walks backwards along one selected path. Renames follow the old path; copies start a new identity.</summary>
    /// <remarks>Starting at the newest layer is what makes the query well defined: the caller supplies the path as it exists in
    /// that layer. Each layer is then asked for the file entry bearing that new-side path in its own merge-base-to-head diff, so
    /// <c>Added</c> and <c>Copied</c> are the entries that end the walk — the current identity begins there — while
    /// <c>Renamed</c> moves the tracked path to the old name for the next, older layer. A layer whose version has no merge base,
    /// or whose diff has zero or several entries for the path, produces a step without a file entry instead of a guess, and the
    /// walk still visits older layers. Continuity is the exact-SHA test <c>path[i - 1].Version.HeadSha == version.MergeBase</c>:
    /// it proves this layer was captured directly on top of the previous one, which is what later allows line coordinates to be
    /// translated. The oldest step is continuous by definition, since nothing older exists in the list. The collected steps are
    /// reversed at the end, so the caller receives ancestry order.</remarks>
    /// <param name="root">Working root of the explicit repository used as the Git working directory; no other checkout is touched.</param>
    /// <param name="path">Captured layers in ancestry order, oldest first, each with its node and version.</param>
    /// <param name="filePath">Path of the file as it exists in the newest layer.</param>
    /// <param name="ct">Cancellation for the per-layer reads.</param>
    /// <returns>Steps in ancestry order, the reverse of the order they were walked in.</returns>
    public async Task<FileEvolution> BuildAsync(string root, IReadOnlyList<(ChangeNode Node, ChangeVersion Version)> path,
        string filePath, CancellationToken ct = default)
    {
        var steps = new List<FileEvolutionStep>();
        var currentPath = filePath;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var (node, version) = path[i];
            if (version.MergeBase is null)
            {
                steps.Add(new(node, version, null, false));
                continue;
            }
            var files = (await git.FilesAsync(root, version.MergeBase, version.HeadSha, ct)).OrThrow();
            var candidates = files.Where(f => f.NewPath == currentPath).ToArray();
            var file = candidates.Length == 1 ? candidates[0] : null;
            var continuous = i == 0 || path[i - 1].Version.HeadSha == version.MergeBase;
            steps.Add(new(node, version, file, continuous));
            if (file?.Kind is FileChangeKind.Added or FileChangeKind.Copied)
                break;
            if (file?.Kind == FileChangeKind.Renamed)
                currentPath = file.OldPath;
        }
        steps.Reverse();
        return new(steps);
    }
    /// <summary>Attributes one line of the newest captured file to the layer that introduced or last rewrote it.</summary>
    /// <remarks>The walk runs newest to oldest and keeps translating the line number into older coordinates. When the reader also
    /// implements <see cref="IBlobReader"/> the range is validated first: the logical line count is the split length minus one
    /// when the text ends with a newline, because a trailing newline does not open another line, and a line outside that range —
    /// or a missing blob — returns the "outside the current file" message rather than a wrong attribution. Per step the patch
    /// row for the current new-side line decides: an added row means this layer introduced or rewrote the text, so it is
    /// reported; a row with an old-side number moves the line to that number; and when no row carries that new-side number, the
    /// nearest hunk ending entirely above the line supplies the offset <c>(OldStart + OldCount) − (NewStart + NewCount)</c> — the
    /// net number of lines added or removed before that point — and the line is shifted by it. A step that is not continuous
    /// ends the walk with an ancestry-gap message, because older coordinates are no longer comparable. Exhausting the steps means
    /// the line predates the captured stack. Every outcome, including "no attribution", is returned as a human-readable message
    /// the UI shows directly; only a Git read failure still surfaces as <see cref="StackerException"/>.</remarks>
    /// <param name="root">Working root of the explicit repository used for the patch and blob reads.</param>
    /// <param name="evolution">Evolution produced by <see cref="BuildAsync"/> for this file.</param>
    /// <param name="line">One-based line number in the newest captured file.</param>
    /// <param name="ct">Cancellation for the patch reads.</param>
    /// <returns>A message naming the layer that introduced the line, or explaining why it cannot be attributed.</returns>
    public async Task<string> TraceLineAsync(string root, FileEvolution evolution, int line, CancellationToken ct = default)
    {
        var latest = evolution.Steps.LastOrDefault();
        var latestPath = evolution.Steps.LastOrDefault(s => s.File is not null)?.File?.NewPath;
        if (line < 1 || latest is null || latestPath is null)
            return "No captured file line is available.";
        if (git is IBlobReader reader)
        {
            var blob = (await reader.ReadBlobAsync(root, latest.Version.HeadSha, latestPath, ct)).OrThrow();
            if (blob is null || line > blob.Text.Split('\n').Length - (blob.Text.EndsWith('\n') ? 1 : 0))
                return "Line is outside the current file or its contents are unavailable.";
        }
        foreach (var step in evolution.Steps.Reverse())
        {
            if (step.File is { } file)
            {
                var diff = (await git.PatchAsync(root, step.Version.MergeBase!, step.Version.HeadSha, file, ct)).OrThrow();
                var row = diff.Lines.FirstOrDefault(l => l.NewLineNumber == line);
                if (row?.Kind == DiffLineKind.Added)
                    return $"Line last introduced or textually rewritten in {step.Change.Title} ({step.Version.Label}).";
                if (row?.OldLineNumber is { } old)
                    line = old;
                else
                    foreach (var hunk in diff.Hunks.Where(h => h.NewStart + h.NewCount <= line).OrderByDescending(h => h.NewStart).Take(1))
                        line += hunk.OldStart + hunk.OldCount - hunk.NewStart - hunk.NewCount;
            }
            if (!step.Continuous)
                return "Line origin is unavailable across an ancestry gap.";
        }
        return "Line predates this captured stack; no layer is attributed.";
    }

}
