namespace Stacker.Core;

/// <summary>One working-tree entry from git status, with the flags the UI, the stash and the commit dialog need.</summary>
/// <remarks>Status is Git's two-character porcelain code in XY order, index column first, so <c>M </c> (staged) and <c> M</c> (unstaged) stay distinguishable; the flags are derived from those characters rather than from a second status call, which keeps one snapshot internally consistent. For a rename or copy, Path is the destination path because the parser consumes the source path that follows it in the NUL-delimited output.</remarks>
/// <param name="Path">Path relative to the repository root; for a rename or copy this is the destination.</param>
/// <param name="Status">Two-character porcelain v1 code, index column then worktree column.</param>
/// <param name="IsStaged">Whether the index holds a change that is not a conflict, which is what the commit dialog offers.</param>
/// <param name="IsConflicted">Whether the entry uses one of Git's unmerged codes; such a path must be resolved and is never treated as staged.</param>
/// <param name="IsUntracked">Whether the path is untracked (<c>??</c>), so it cannot block a rewrite and is not saved by a plain stash.</param>
public sealed record WorkingTreeEntry(string Path, string Status, bool IsStaged, bool IsConflicted, bool IsUntracked)
{
    /// <summary>Status code and path in one string for flat lists where the flags are not otherwise visible.</summary>
    /// <remarks>The code is kept verbatim so a staged and an unstaged modification do not read the same.</remarks>
    public string Display => Status + " " + Path;
}
