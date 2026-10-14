// Purpose: Converts git status porcelain output into the entries the UI, the stash guard and the commit dialog use.
using Stacker.Core;
namespace Stacker.Infrastructure;

public static class WorkingTreeParser
{
    private static readonly HashSet<string> Unmerged = new(StringComparer.Ordinal) { "DD", "AU", "UD", "UA", "DU", "AA", "UU" };

    /// <summary>Parses NUL-delimited porcelain v1 output; a rename or copy consumes the original path that follows it.</summary>
    public static IReadOnlyList<WorkingTreeEntry> Parse(string porcelain)
    {
        var fields = porcelain.Split('\0');
        var entries = new List<WorkingTreeEntry>();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Length < 3) continue;
            var staged = field[0];
            var worktree = field[1];
            if (staged is 'R' or 'C' || worktree is 'R' or 'C') index++;
            var code = new string([staged, worktree]);
            var conflicted = Unmerged.Contains(code);
            var untracked = staged == '?' && worktree == '?';
            entries.Add(new(field[3..], code, staged is not (' ' or '?') && !conflicted, conflicted, untracked));
        }
        return entries;
    }
}
