namespace Stacker.Core;

public sealed class FileEvolutionService(IGitRepositoryReader git)
{
    /// <summary>Walks backwards along one selected path. Renames follow the old path; copies start a new identity.</summary>
    public async Task<FileEvolution> BuildAsync(string root, IReadOnlyList<(ChangeNode Node, ChangeVersion Version)> path,
        string filePath, CancellationToken ct = default)
    {
        var steps = new List<FileEvolutionStep>(); var currentPath = filePath;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var (node, version) = path[i];
            if (version.MergeBase is null) { steps.Add(new(node, version, null, false)); continue; }
            var files = (await git.FilesAsync(root, version.MergeBase, version.HeadSha, ct)).OrThrow();
            var candidates = files.Where(f => f.NewPath == currentPath).ToArray();
            var file = candidates.Length == 1 ? candidates[0] : null;
            var continuous = i == 0 || path[i - 1].Version.HeadSha == version.MergeBase;
            steps.Add(new(node, version, file, continuous));
            if (file?.Kind is FileChangeKind.Added or FileChangeKind.Copied) break;
            if (file?.Kind == FileChangeKind.Renamed) currentPath = file.OldPath;
        }
        steps.Reverse(); return new(steps);
    }
    public async Task<string> TraceLineAsync(string root, FileEvolution evolution, int line, CancellationToken ct = default)
    {
        var latest = evolution.Steps.LastOrDefault();
        var latestPath = evolution.Steps.LastOrDefault(s => s.File is not null)?.File?.NewPath;
        if (line < 1 || latest is null || latestPath is null) return "No captured file line is available.";
        if (git is IBlobReader reader)
        {
            var blob = (await reader.ReadBlobAsync(root, latest.Version.HeadSha, latestPath, ct)).OrThrow();
            if (blob is null || line > blob.Text.Split('\n').Length - (blob.Text.EndsWith('\n') ? 1 : 0)) return "Line is outside the current file or its contents are unavailable.";
        }
        foreach (var step in evolution.Steps.Reverse())
        {
            if (step.File is { } file)
            {
                var diff = (await git.PatchAsync(root, step.Version.MergeBase!, step.Version.HeadSha, file, ct)).OrThrow();
                var row = diff.Lines.FirstOrDefault(l => l.NewLineNumber == line);
                if (row?.Kind == DiffLineKind.Added) return $"Line last introduced or textually rewritten in {step.Change.Title} ({step.Version.Label}).";
                if (row?.OldLineNumber is { } old) line = old;
                else foreach (var hunk in diff.Hunks.Where(h => h.NewStart + h.NewCount <= line).OrderByDescending(h => h.NewStart).Take(1))
                    line += hunk.OldStart + hunk.OldCount - hunk.NewStart - hunk.NewCount;
            }
            if (!step.Continuous) return "Line origin is unavailable across an ancestry gap.";
        }
        return "Line predates this captured stack; no layer is attributed.";
    }

}

public static class AnchorMapping
{
    /// <summary>Requires an exact, unique selected block with up to two context lines on both sides.</summary>
    public static AnchorProposal? Propose(ReviewAnchor anchor, PullRequest target, string newPath, string oldText, string newText)
    {
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n'); var newLines = newText.Replace("\r\n", "\n").Split('\n');
        var start = (anchor.StartLine ?? anchor.Line) - 1; var end = anchor.Line - 1;
        if (start < 0 || end >= oldLines.Length || end < start) return null;
        var contextStart = Math.Max(0, start - 2); var contextEnd = Math.Min(oldLines.Length - 1, end + 2);
        var block = oldLines[contextStart..(contextEnd + 1)]; var matches = new List<int>();
        for (var i = 0; i + block.Length <= newLines.Length; i++)
        {
            if (block.SequenceEqual(newLines[i..(i + block.Length)])) matches.Add(i);
            if (matches.Count > 1) return null;
        }
        if (matches.Count != 1) return null;
        var mappedStart = matches[0] + start - contextStart + 1; var mappedEnd = mappedStart + end - start;
        var proposed = anchor with { HeadSha = target.HeadSha, BaseSha = target.BaseSha, Path = newPath, Line = mappedEnd,
            StartLine = anchor.StartLine is null ? null : mappedStart };
        return new(anchor, proposed, string.Join("\n", block), string.Join("\n", newLines[matches[0]..(matches[0] + block.Length)]));
    }
}
