using System.Text;
using System.Text.RegularExpressions;

namespace Stacker.Core;

/// <summary>One text or suggested-replacement block in a review comment, in its original order.</summary>
public sealed record ReviewContentBlock(string Text, bool IsSuggestion, int LinesAbove = 0, int LinesBelow = 0)
{
    public bool IsText => !IsSuggestion;
    public string Label => LinesAbove == 0 && LinesBelow == 0 ? "Suggested change" : $"Suggested change · −{LinesAbove} / +{LinesBelow} lines";
    public bool IsDeletion => IsSuggestion && Text.Length == 0;
}

/// <summary>Reads suggestion fences without interpreting ordinary code fences or malformed Markdown as replacements.</summary>
public static partial class ReviewContent
{
    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>[^\r\n]*)$")]
    private static partial Regex OpeningFence();
    [GeneratedRegex(@"^suggestion(?::-(?<above>\d+)\+(?<below>\d+))?$")]
    private static partial Regex SuggestionInfo();

    public static IReadOnlyList<ReviewContentBlock> Parse(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<ReviewContentBlock>();
        var text = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var opening = OpeningFence().Match(lines[i]);
            if (!opening.Success) { text.Add(lines[i]); continue; }
            var fence = opening.Groups["fence"].Value;
            var end = i + 1;
            for (; end < lines.Length; end++)
            {
                var candidate = lines[end];
                var trimmed = candidate.TrimStart(' ');
                if (candidate.Length - trimmed.Length > 3) continue;
                trimmed = trimmed.TrimEnd(' ', '\t');
                if (trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0])) break;
            }
            if (end == lines.Length) { text.AddRange(lines[i..]); break; }
            var info = SuggestionInfo().Match(opening.Groups["info"].Value.Trim());
            if (!info.Success)
            {
                // Skip the entire ordinary fence, including nested examples of suggestion syntax.
                text.AddRange(lines[i..(end + 1)]); i = end; continue;
            }
            if (text.Count > 0) { blocks.Add(new(string.Join('\n', text), false)); text.Clear(); }
            var above = 0; var below = 0;
            if (info.Groups["above"].Success && (!int.TryParse(info.Groups["above"].Value, out above) || !int.TryParse(info.Groups["below"].Value, out below)))
            { text.AddRange(lines[i..(end + 1)]); i = end; continue; }
            blocks.Add(new(string.Join('\n', lines[(i + 1)..end]), true, above, below)); i = end;
        }
        if (text.Count > 0) blocks.Add(new(string.Join('\n', text), false));
        return blocks;
    }

    /// <summary>Uses a fence longer than any backtick run in the code so embedded Markdown cannot close it early.</summary>
    public static string Suggestion(string code)
    {
        var longest = 0; var run = 0;
        foreach (var c in code) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        var fence = new string('`', Math.Max(3, longest + 1));
        return new StringBuilder().Append(fence).Append("suggestion\n").Append(code)
            .Append(code.Length == 0 ? "" : "\n").Append(fence).ToString();
    }
}

/// <summary>GitHub line-anchor rules shared by the selection affordance and review composer.</summary>
public static class ReviewSelection
{
    public static bool TryCreateAnchor(PullRequest pr, string path, IReadOnlyList<DiffLine> lines, string? side,
        out ReviewAnchor? anchor, out string? error)
    {
        anchor = null; error = null;
        if (lines.Count == 0) { error = "Select code lines first."; return false; }
        var left = side == "LEFT" || side is null && lines.Any(l => l.Kind == DiffLineKind.Removed) && lines.All(l => l.Kind != DiffLineKind.Added);
        // GitHub accepts unchanged context on RIGHT; LEFT targets only deleted lines.
        if (side is not (null or "LEFT" or "RIGHT") || (left ? lines.Any(l => l.Kind != DiffLineKind.Removed) : lines.Any(l => l.Kind is not (DiffLineKind.Added or DiffLineKind.Context))))
        { error = "Select a contiguous range on one side of the diff. Context lines use the right side."; return false; }
        var numbers = lines.Select(l => left ? l.OldLineNumber : l.NewLineNumber).OfType<int>().Distinct().Order().ToArray();
        if (numbers.Length != lines.Count || numbers[0] <= 0 || numbers[^1] - numbers[0] + 1 != numbers.Length)
        { error = "Select a contiguous range of code lines."; return false; }
        anchor = new(pr.Number, pr.HeadSha, pr.BaseSha, path, left ? "LEFT" : "RIGHT", numbers[^1],
            numbers.Length > 1 ? numbers[0] : null, numbers.Length > 1 ? left ? "LEFT" : "RIGHT" : null);
        return true;
    }
}
