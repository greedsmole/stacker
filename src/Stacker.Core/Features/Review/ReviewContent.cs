using System.Text;
using System.Text.RegularExpressions;

namespace Stacker.Core;

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
            if (!opening.Success)
            {
                text.Add(lines[i]);
                continue;
            }
            var fence = opening.Groups["fence"].Value;
            var end = i + 1;
            for (; end < lines.Length; end++)
            {
                var candidate = lines[end];
                var trimmed = candidate.TrimStart(' ');
                if (candidate.Length - trimmed.Length > 3)
                    continue;
                trimmed = trimmed.TrimEnd(' ', '\t');
                if (trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0]))
                    break;
            }
            if (end == lines.Length)
            {
                text.AddRange(lines[i..]);
                break;
            }
            var info = SuggestionInfo().Match(opening.Groups["info"].Value.Trim());
            if (!info.Success)
            {
                // Skip the entire ordinary fence, including nested examples of suggestion syntax.
                text.AddRange(lines[i..(end + 1)]);
                i = end;
                continue;
            }
            if (text.Count > 0)
            {
                blocks.Add(new(string.Join('\n', text), false));
                text.Clear();
            }
            var above = 0;
            var below = 0;
            if (info.Groups["above"].Success && (!int.TryParse(info.Groups["above"].Value, out above) || !int.TryParse(info.Groups["below"].Value, out below)))
            {
                text.AddRange(lines[i..(end + 1)]);
                i = end;
                continue;
            }
            blocks.Add(new(string.Join('\n', lines[(i + 1)..end]), true, above, below));
            i = end;
        }
        if (text.Count > 0)
            blocks.Add(new(string.Join('\n', text), false));
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
        var longest = 0;
        var run = 0;
        foreach (var c in code)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return new StringBuilder().Append(fence).Append("suggestion\n").Append(code)
            .Append(code.Length == 0 ? "" : "\n").Append(fence).ToString();
    }
}
