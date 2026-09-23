using System.Globalization;
using System.Text.RegularExpressions;
using Stacker.Core;

namespace Stacker.Infrastructure;

public static partial class PatchParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();
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
                newLine = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                current = [];
                hunks.Add(new(text, current));
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
