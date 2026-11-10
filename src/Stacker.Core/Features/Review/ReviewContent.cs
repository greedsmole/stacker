using System.Text;
using System.Text.RegularExpressions;

namespace Stacker.Core;

/// <summary>One text or suggested-replacement block in a review comment, in its original order.</summary>
/// <remarks>
/// A comment is parsed into blocks so the views can render prose as Markdown and each suggestion as a copyable
/// replacement, and so publishing code can tell whether a comment actually proposes a code change. The order is the
/// author's order: a comment may mix several suggestions with prose before, between and after them.
/// </remarks>
/// <param name="Text">Block content. For a suggestion this is the replacement code as written between the fences, with
/// LF line breaks and its indentation preserved; for a text block it is the original Markdown.</param>
/// <param name="IsSuggestion">True when the block came from a recognized suggestion fence rather than prose.</param>
/// <param name="LinesAbove">GitLab-style count of unchanged lines above the replacement; zero for a plain GitHub
/// suggestion, which replaces only the targeted lines.</param>
/// <param name="LinesBelow">GitLab-style count of unchanged lines below the replacement; zero for a plain suggestion.</param>
public sealed record ReviewContentBlock(string Text, bool IsSuggestion, int LinesAbove = 0, int LinesBelow = 0)
{
    /// <summary>True for a prose block, which the view renders as selectable text instead of a replacement panel.</summary>
    public bool IsText => !IsSuggestion;
    /// <summary>Caption shown for the block, including the offset counts when the author used the GitLab form.</summary>
    public string Label => LinesAbove == 0 && LinesBelow == 0 ? "Suggested change" : $"Suggested change · −{LinesAbove} / +{LinesBelow} lines";
    /// <summary>True for a suggestion whose replacement is empty, which proposes deleting the targeted lines.</summary>
    public bool IsDeletion => IsSuggestion && Text.Length == 0;
}

/// <summary>Reads suggestion fences without interpreting ordinary code fences or malformed Markdown as replacements.</summary>
/// <remarks>
/// Only GitHub's <c>```suggestion</c> fence (and its GitLab <c>```suggestion:-N+M</c> variant) is a replacement. Every
/// other fence stays prose, because treating a code example as a suggestion would silently propose replacing the
/// reviewer's selected lines with sample code. Unterminated fences, over-indented fences and unparseable offsets are
/// likewise returned as text: a damaged comment must still render instead of hiding the text the author wrote.
/// </remarks>
public static partial class ReviewContent
{
    /// <summary>Matches an opening fence: up to three leading spaces, at least three backticks or tildes, then an info string.</summary>
    /// <remarks>Three spaces is the CommonMark indent that still belongs to a paragraph; a fourth space makes the line an
    /// indented code block, so it can never open a suggestion and is left as text. The info string is checked separately,
    /// which lets one scan recognize ordinary code fences and suggestion fences with the same opening shape.</remarks>
    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>[^\r\n]*)$")]
    private static partial Regex OpeningFence();
    /// <summary>Matches a suggestion info string, with optional GitLab <c>:-above+below</c> line offsets.</summary>
    /// <remarks>Only the exact word <c>suggestion</c> qualifies. Anything else, including <c>suggestion python</c> or a
    /// malformed offset such as <c>suggestion:-x+1</c>, fails this match and the fence is preserved as ordinary code, so a
    /// typo in the marker cannot rewrite code.</remarks>
    [GeneratedRegex(@"^suggestion(?::-(?<above>\d+)\+(?<below>\d+))?$")]
    private static partial Regex SuggestionInfo();

    /// <summary>Splits a comment body into prose and suggestion blocks, preserving the author's order and text.</summary>
    /// <remarks>
    /// Flow: CRLF is normalized to LF, then each line is tested as an opening fence. A non-fence line is accumulated as
    /// prose. A fence is closed by the first line that is indented at most three spaces and consists only of the same fence
    /// character, at least as many times as the opener — the CommonMark rule that prevents an inner shorter fence from
    /// ending the block early. An unterminated fence leaves the rest of the body as prose instead of dropping it, and an
    /// ordinary (non-suggestion) fence is copied through verbatim, including any suggestion-looking fences nested inside
    /// it, so examples about suggestions are not executable replacements. Text before a suggestion becomes its own block,
    /// and trailing prose becomes a final text block. No input throws: malformed fences and unparseable offsets degrade to
    /// text, which keeps a stored draft loadable and displayable even after hand-editing.
    /// </remarks>
    /// <param name="body">Raw comment Markdown. Empty text is accepted and yields one empty text block, which the views
    /// filter out.</param>
    /// <returns>Blocks in source order; empty prose segments are not returned, and an empty suggestion stays a suggestion
    /// so a deletion keeps its meaning.</returns>
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
    /// <remarks>
    /// The fence is at least three backticks — the shortest legal fence — and otherwise one longer than the longest run
    /// inside the replacement, so suggested code that itself contains backticks (a Markdown example, for instance) cannot
    /// terminate the suggestion. An empty replacement produces an empty fenced block, which parses back as a suggestion
    /// with empty text, so "suggest deleting these lines" round-trips. A newline is appended only when there is code, so
    /// the empty case does not acquire a blank line that would change the replacement.
    /// </remarks>
    /// <param name="code">Replacement code to place between the fences; it is not modified or escaped.</param>
    /// <returns>Markdown whose parse yields exactly one suggestion block with the same text.</returns>
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
/// <remarks>
/// The rules exist because GitHub rejects or misfiles a review comment whose line is not on the requested side of the
/// current diff, and because a comment attached to the wrong line is worse than a refusal: it looks authoritative while
/// pointing at unrelated code. Every rejection below therefore returns a user-facing reason and no anchor.
/// </remarks>
public static class ReviewSelection
{
    /// <summary>Builds an anchor from selected diff lines, or explains why the selection cannot be commented on.</summary>
    /// <remarks>
    /// Side selection: an explicit side wins. Without one, a selection that contains removed lines and no added lines is
    /// inferred as LEFT and then required to contain only removed lines, which is how a deletion is recognized; every other
    /// selection is treated as RIGHT, where context and added lines are allowed and a removed line is refused, because
    /// GitHub accepts unchanged context on the right. A left-side selection must contain only removed lines and a right-side
    /// selection only added or context lines, which is what the screen shows; a range that mixes both sides, or includes a
    /// header line, is refused because the two sides are different coordinate spaces. Line numbers are taken on the chosen
    /// side, de-duplicated and sorted, so every selected line must have a number there, no line may repeat, and the range
    /// must be contiguous — a gap cannot be represented by one anchor and would place the comment across unrelated lines.
    /// The anchor's <c>Line</c> is the last selected line and <c>StartLine</c> is set only for a range, matching GitHub's
    /// end-line/start-line contract used by the writer.
    /// </remarks>
    /// <param name="pr">Pull request whose number and version SHAs the anchor records.</param>
    /// <param name="path">Repository-relative path of the file being reviewed.</param>
    /// <param name="lines">Selected diff lines, in the order shown; header and notice lines carry no side line number.</param>
    /// <param name="side">Explicit <c>LEFT</c> or <c>RIGHT</c> choice, or null to infer it from the selected line kinds.</param>
    /// <param name="anchor">The built anchor on success; null when the selection is refused.</param>
    /// <param name="error">A message naming the rule the selection broke, or null on success.</param>
    /// <returns>True when <paramref name="anchor"/> was built; false with <paramref name="error"/> set otherwise.</returns>
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
