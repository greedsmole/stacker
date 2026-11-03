// Purpose: Converts unified patch text into line-numbered diff records used by rendering and review-anchor validation.
using System.Globalization;
using System.Text.RegularExpressions;
using Stacker.Core;

namespace Stacker.Infrastructure;

public static partial class PatchParser
{
    // Generated once by the compiler so each patch line does not recompile or recreate the hunk-header expression.
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$")]
    private static partial Regex HunkHeader();
    /// <summary>Parses unified patch lines into semantic kinds and old/new coordinates for display and review anchoring.</summary>
    public static FileDiff Parse(FileChange file, string patch)
    {
        var lines = new List<DiffLine>();
        var hunks = new List<DiffHunk>();
        List<DiffLine>? current = null;
        var oldLine = 0; var newLine = 0;
        var textLines = patch.Split('\n');
        for (var i = 0; i < textLines.Length; i++)
        {
            var text = textLines[i];
            if (i == textLines.Length - 1 && text.Length == 0) break;
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
            else if (current is not null && text.StartsWith('+')) line = new(DiffLineKind.Added, null, newLine++, text);
            else if (current is not null && text.StartsWith('-')) line = new(DiffLineKind.Removed, oldLine++, null, text);
            else if (current is not null && text.StartsWith(' ')) line = new(DiffLineKind.Context, oldLine++, newLine++, text);
            else line = new(DiffLineKind.Notice, null, null, text);
            lines.Add(line);
            if (line.Kind != DiffLineKind.Header) current?.Add(line);
        }
        return new(file, patch, hunks, lines);
    }
}
