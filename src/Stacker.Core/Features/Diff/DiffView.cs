namespace Stacker.Core;

/// <summary>Pure presentation data shared by rendering, empty states and comment-range selection.</summary>
public sealed record DiffView(IReadOnlyList<DiffLine> Rows, string EmptyReason)
{
    public static DiffView From(FileDiff diff)
    {
        var rows = new List<DiffLine>();
        foreach (var hunk in diff.Hunks)
        {
            rows.Add(new(DiffLineKind.Header, null, null,
                $"Lines {hunk.OldStart}–{hunk.OldStart + Math.Max(0, hunk.OldCount - 1)} → {hunk.NewStart}–{hunk.NewStart + Math.Max(0, hunk.NewCount - 1)}  {hunk.Context}"));
            foreach (var line in hunk.Lines)
                if (line.Kind != DiffLineKind.Notice || line.Text.StartsWith("\\ No newline", StringComparison.Ordinal))
                    rows.Add(line.Kind == DiffLineKind.Notice ? line with { Text = "No newline at end of file" } : line);
        }
        var file = diff.File;
        var reason = file.IsBinary ? "Binary file — text preview unavailable."
            : file.IsSubmodule ? "Submodule commit changed."
            : file.ModeChanged && diff.Hunks.Count == 0 ? file.Detail
            : diff.Hunks.Count == 0 ? $"{file.Detail} — no textual changes in this comparison." : "";
        return new(rows, reason);
    }
}
