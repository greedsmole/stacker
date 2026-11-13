using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

/// <summary>Indexes the pinned head blob and identifies unchanged lines outside the parsed Git hunks.</summary>
internal sealed class DiffContext(string text, int[] lineStarts, IReadOnlyList<DiffContextGap> gaps)
{
    public IReadOnlyList<DiffContextGap> Gaps { get; } = gaps;
    public int HiddenLineCount { get; } = gaps.Sum(gap => gap.Count);

    public static bool CanRead(FileChange file) => !file.IsBinary && !file.IsSubmodule &&
        file.Kind is FileChangeKind.Modified or FileChangeKind.Renamed or FileChangeKind.Copied;

    public static DiffContext? Create(FileDiff diff, DiffView view, string text, CancellationToken ct)
    {
        if (text.Length == 0 || text.Contains('\0'))
            return null;
        var lineCount = text.AsSpan().Count('\n') + (text.EndsWith('\n') ? 0 : 1);
        var gaps = new List<DiffContextGap>();
        var hunkIndex = 0;
        var oldConsumed = 0;
        var newConsumed = 0;
        for (var row = 0; row < view.Rows.Count; row++)
        {
            if (view.Rows[row].Kind != DiffLineKind.Header)
                continue;
            ct.ThrowIfCancellationRequested();
            var hunk = diff.Hunks[hunkIndex++];
            // On an empty side, Git reports the preceding line rather than the first line of the hunk.
            var oldBefore = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
            var newBefore = hunk.NewCount == 0 ? hunk.NewStart : hunk.NewStart - 1;
            var count = newBefore - newConsumed;
            if (count < 0 || oldBefore - oldConsumed != count || newBefore + hunk.NewCount > lineCount ||
                hunk.Lines.Count(line => line.OldLineNumber.HasValue) != hunk.OldCount ||
                hunk.Lines.Count(line => line.NewLineNumber.HasValue) != hunk.NewCount)
                return null;
            if (count > 0)
                gaps.Add(new(row, oldConsumed + 1, newConsumed + 1, count, row > 0, true));
            oldConsumed = oldBefore + hunk.OldCount;
            newConsumed = newBefore + hunk.NewCount;
        }
        if (diff.Hunks.Count == 0 && (diff.File.Additions != 0 || diff.File.Deletions != 0))
            return null;
        if (lineCount > newConsumed)
            gaps.Add(new(view.Rows.Count, oldConsumed + 1, newConsumed + 1, lineCount - newConsumed, view.Rows.Count > 0, false));
        if (gaps.Count == 0)
            return null;

        var starts = new int[lineCount + 1];
        for (var line = 0; line < lineCount; line++)
        {
            if (line % 1024 == 0)
                ct.ThrowIfCancellationRequested();
            var newline = text.IndexOf('\n', starts[line]);
            starts[line + 1] = newline < 0 ? text.Length : newline + 1;
        }
        return new(text, starts, gaps);
    }

    public DiffLine Line(int oldNumber, int newNumber)
    {
        var start = lineStarts[newNumber - 1];
        var end = lineStarts[newNumber];
        if (text[end - 1] == '\n')
            end--;
        return new(DiffLineKind.Context, oldNumber, newNumber, string.Concat(" ", text.AsSpan(start, end - start)));
    }
}
