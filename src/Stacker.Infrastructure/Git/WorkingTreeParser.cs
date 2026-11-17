// Purpose: Converts git status porcelain output into the entries the UI, the stash guard and the commit dialog use.
using Stacker.Core;
namespace Stacker.Infrastructure;

/// <summary>Turns one NUL-delimited <c>git status --porcelain=v1 -z</c> snapshot into working-tree entries with the
/// flags the UI, the stash guard and the commit dialog derive their decisions from.</summary>
/// <remarks>The flags are computed from Git's two status characters rather than from a second status call, so one
/// snapshot cannot disagree with itself. The NUL delimiter is what makes paths with spaces, quotes and non-ASCII
/// characters safe, and it is also why a rename or copy occupies two fields: Git emits the destination path with the
/// entry and appends the original path as the next field, which this parser consumes so the following entry is not
/// misread. Only these characters decide the flags, so an unmerged code can never be mistaken for a staged change and
/// an untracked path can never block a rewrite.</remarks>
public static class WorkingTreeParser
{
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
    /// advances by one to discard the original path, and the entry keeps the destination. A path is staged when its
    /// index column is neither a space nor <c>?</c> and its code is not unmerged, conflicted when its code is in
    /// <see cref="Unmerged"/>, and untracked only for <c>??</c>. The original text is not retained, so callers cannot
    /// recover a rename's source path from the result.</remarks>
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
            if (staged is 'R' or 'C' || worktree is 'R' or 'C')
                index++;
            var code = new string([staged, worktree]);
            var conflicted = Unmerged.Contains(code);
            var untracked = staged == '?' && worktree == '?';
            entries.Add(new(field[3..], code, staged is not (' ' or '?') && !conflicted, conflicted, untracked));
        }
        return entries;
    }
}
