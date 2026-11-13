// Purpose: Converts unified patch text into line-numbered diff records used by rendering and review-anchor validation.
using System.Globalization;
using System.Text.RegularExpressions;
using Stacker.Core;

namespace Stacker.Infrastructure;

/// <summary>Turns the unified patch text Git produced into semantic rows with old and new coordinates, for rendering,
/// changed-line search and review-anchor validation.</summary>
/// <remarks>One pass assigns each row its kind and line number, so a review comment can be anchored to a file and line
/// without re-reading the patch. Rows keep their raw text including the leading marker; a hunk header row appears in
/// the flat row stream but not in the hunk's own body. The class is static and the header expression is
/// source-generated because a large comparison parses one patch per file, so per-line cost decides whether the diff
/// opens promptly. Text that is neither a header nor a marked row is reported as a notice rather than raising an
/// error, which keeps binary markers, mode lines and the no-newline marker visible without inventing a coordinate.</remarks>
public static partial class PatchParser
{
    // Generated once by the compiler so each patch line does not recompile or recreate the hunk-header expression.
    /// <summary>The compiled hunk-header expression, shared by every call to <see cref="Parse"/>.</summary>
    /// <remarks>Group 1 is the old start, 2 the old count, 3 the new start, 4 the new count and 5 the section heading
    /// Git appends after the closing <c>@@</c>, such as the enclosing function. A count is optional in unified format:
    /// Git omits it when it is exactly one, so a missing group is normalized to one by <see cref="Parse"/>. Rows that
    /// do not match the full pattern are classified by their first character instead, which is why the
    /// <c>---</c>/<c>+++</c> file preamble is never read as a removed or added line.</remarks>
    /// <returns>The generated expression; it is immutable and safe to use concurrently.</returns>
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$")]
    private static partial Regex HunkHeader();
    /// <summary>Parses unified patch lines into semantic kinds and old/new coordinates for display and review anchoring.</summary>
    /// <remarks>Rows before the first hunk header are notices, so the file preamble cannot be mistaken for a changed
    /// line. Inside a hunk the first character decides the kind: a leading <c>+</c> is added, <c>-</c> removed and a
    /// space context, and the row takes the next number on the matching side. Each hunk starts from the coordinates in
    /// its header, and a count Git omitted becomes one. The trailing empty field produced by the patch's final newline
    /// is dropped, so the last row is the last real row rather than a blank notice. A patch whose header counts
    /// disagree with its body still yields monotonic line numbers, because numbers are derived by walking the rows
    /// instead of being trusted from the header; nothing is rejected.</remarks>
    /// <param name="file">Metadata of the file this patch belongs to, copied onto the result unchanged.</param>
    /// <param name="patch">The raw unified patch text exactly as Git produced it.</param>
    /// <returns>The parsed patch: the original text, its hunks with parsed coordinates, and a flat stream over the same
    /// rows that a lookup by line number can scan once.</returns>
    public static FileDiff Parse(FileChange file, string patch)
    {
        var lines = new List<DiffLine>();
        var hunks = new List<DiffHunk>();
        List<DiffLine>? current = null;
        var oldLine = 0;
        var newLine = 0;
        var textLines = patch.Split('\n');
        for (var i = 0; i < textLines.Length; i++)
        {
            var text = textLines[i];
            if (i == textLines.Length - 1 && text.Length == 0)
                break;
            var match = HunkHeader().Match(text);
            DiffLine line;
            if (match.Success)
            {
                oldLine = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                newLine = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                current = [];
                hunks.Add(new(text, current, oldLine, match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1, newLine, match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 1, match.Groups[5].Value.Trim()));
                line = new(DiffLineKind.Header, null, null, text);
            }
            else if (current is not null && text.StartsWith('+'))
                line = new(DiffLineKind.Added, null, newLine++, text);
            else if (current is not null && text.StartsWith('-'))
                line = new(DiffLineKind.Removed, oldLine++, null, text);
            else if (current is not null && text.StartsWith(' '))
                line = new(DiffLineKind.Context, oldLine++, newLine++, text);
            else
                line = new(DiffLineKind.Notice, null, null, text);
            lines.Add(line);
            if (line.Kind != DiffLineKind.Header)
                current?.Add(line);
        }
        return new(file, patch, hunks, lines);
    }
}
