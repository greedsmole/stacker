// Purpose: Converts git status porcelain output into the entries the UI, the stash guard and the commit dialog use.
using Stacker.Core;
namespace Stacker.Infrastructure;

/// <summary>Turns one NUL-delimited <c>git status --porcelain=v1 -z</c> snapshot into working-tree entries with the
/// flags the UI, the stash guard and the commit dialog derive their decisions from.</summary>
/// <remarks>The flags are computed from Git's two status characters rather than from a second status call, so one
/// snapshot cannot disagree with itself. The NUL delimiter is what makes paths with spaces, quotes and non-ASCII
/// characters safe, and it is also why a rename or copy occupies two fields: Git emits the destination path with the
/// entry and appends the original path as the next field, which this parser retains. Only these characters decide the flags,
/// so an unmerged code can never be mistaken for a staged change and
/// an untracked path can never block a rewrite.</remarks>
public static class WorkingTreeParser
{
    internal static (string? Head, string? Branch, IReadOnlyList<WorkingTreeEntry> Entries) ParseWithBranch(string porcelain)
    {
        string? head = null;
        string? branch = null;
        var entries = new List<WorkingTreeEntry>();
        var fields = porcelain.Split('\0');
        for (var i = 0; i < fields.Length && fields[i].Length > 0; i++)
        {
            var record = fields[i];
            switch (record[0])
            {
                case '#':
                    if (record.StartsWith("# branch.oid ", StringComparison.Ordinal) && record[13..] != "(initial)")
                        head = record[13..];
                    if (record.StartsWith("# branch.head ", StringComparison.Ordinal) && record[14..] != "(detached)")
                        branch = record[14..];
                    break;
                case '?':
                    entries.Add(new(record[2..], "??", false, false, true));
                    break;
                case '1':
                case '2':
                case 'u':
                    var tracked = record.Split(' ', record[0] == '1' ? 9 : record[0] == '2' ? 10 : 11);
                    var code = tracked[1].Replace('.', ' ');
                    if (code[1] == ' ' && tracked[2][0] == 'S' && tracked[2] != "S...")
                        code = code[0] + "M";
                    var conflicted = record[0] == 'u';
                    var originalPath = record[0] == '2' ? fields[++i] : null;
                    entries.Add(new(tracked[^1], code, code[0] != ' ' && !conflicted, conflicted, false)
                    {
                        OriginalPath = originalPath
                    });
                    break;
            }
        }
        return (head, branch, entries);
    }

    /// <summary>The porcelain v1 codes that mean the path is unmerged, which is the only state that must be resolved
    /// before a commit or a branch move.</summary>
    /// <remarks>Git's seven unmerged combinations are DD, AU, UD, UA, DU, AA and UU; every other two-character code
    /// describes a cleanly staged or unstaged change. The set is static because these values never change during a
    /// session, and a set lookup keeps the per-entry cost constant while a comparison is exact and case-sensitive, as
    /// the codes are ASCII. Membership is what stops a conflicted path from also counting as staged: the index column
    /// of an unmerged code is a letter, so without this check the commit dialog would offer a conflicted file as
    /// staged.</remarks>
    private static readonly HashSet<string> Unmerged = new(StringComparer.Ordinal) { "DD", "AU", "UD", "UA", "DU", "AA", "UU" };

    /// <summary>Parses NUL-delimited porcelain v1 output; a rename or copy consumes the original path that follows it.</summary>
    /// <remarks>Each field is <c>XY path</c>, where the first character describes the index and the second the working
    /// tree; a field shorter than three characters is skipped, which drops the empty field after the final delimiter
    /// and any truncated record. The rename and copy codes are the only ones that occupy a second field, so the index
    /// advances by one to read the original path. A path is staged when its
    /// index column is neither a space nor <c>?</c> and its code is not unmerged, conflicted when its code is in
    /// <see cref="Unmerged"/>, and untracked only for <c>??</c>.</remarks>
    /// <param name="porcelain">Output of <c>git status --porcelain=v1 -z</c>, already decoded as text.</param>
    /// <returns>One entry per reported path, in the order Git reported them; empty when nothing changed.</returns>
    public static IReadOnlyList<WorkingTreeEntry> Parse(string porcelain)
    {
        var fields = porcelain.Split('\0');
        var entries = new List<WorkingTreeEntry>();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Length < 3)
                continue;
            var staged = field[0];
            var worktree = field[1];
            var originalPath = staged is 'R' or 'C' || worktree is 'R' or 'C' ? fields.ElementAtOrDefault(++index) : null;
            var code = new string([staged, worktree]);
            var conflicted = Unmerged.Contains(code);
            var untracked = staged == '?' && worktree == '?';
            entries.Add(new(field[3..], code, staged is not (' ' or '?') && !conflicted, conflicted, untracked)
            {
                OriginalPath = string.IsNullOrEmpty(originalPath) ? null : originalPath
            });
        }
        return entries;
    }
}
