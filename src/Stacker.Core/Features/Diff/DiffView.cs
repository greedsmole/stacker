namespace Stacker.Core;

/// <summary>Pure presentation data shared by rendering, empty states and comment-range selection.</summary>
/// <remarks>Rows are what the view binds to: one synthetic header row per hunk followed by that hunk's lines in patch order. Code rows keep their side-specific line numbers so review anchors can address both sides; header and notice rows carry none and are not commentable. EmptyReason is the message shown when no row can be rendered, and is empty whenever Rows has content. The whole patch is prepared off the UI thread before the row collection is replaced.</remarks>
/// <param name="Rows">Ordered rows to display; empty when the file has no textual diff.</param>
/// <param name="EmptyReason">Message explaining why there is nothing to display; empty when Rows is not.</param>
public sealed record DiffView(IReadOnlyList<DiffLine> Rows, string EmptyReason)
{
    public const int DisplayLineLimit = 50_000;

    /// <summary>Projects a parsed patch into display rows, one synthetic header per hunk, and derives the empty-state message when no text can be shown.</summary>
    /// <remarks>Header text is built here rather than taken from the raw patch: <c>Lines oldStart–oldEnd → newStart–newEnd  context</c>, where each end is the start plus count minus one clamped at zero. A zero-count side (a pure insertion or deletion) therefore shows one anchor line instead of an inverted range. Notice rows other than Git's <c>\ No newline at end of file</c> marker are dropped as patch plumbing; that marker is rewritten to a readable sentence. The empty reason has a fixed precedence: binary, submodule, a mode-only change showing <see cref="FileChange.Detail"/>, then a hunkless change with "no textual changes in this comparison". A patch above the reader's 50,000-line display limit never reaches this method; the read fails instead of yielding a truncated row list.</remarks>
    /// <param name="diff">Parsed patch together with its file metadata.</param>
    /// <returns>The ordered rows and, when no row can be produced, the reason to display.</returns>
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
