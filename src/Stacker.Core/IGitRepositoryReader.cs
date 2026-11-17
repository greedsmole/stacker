using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Reads repository history and file content without changing refs, the index, or the working tree.</summary>
/// <remarks>The read side of Git, kept separate from <see cref="IGitBranchWriter"/> so a caller can prove that a screen or a
/// comparison only reads. Every method takes an explicit root and explicit commit IDs, and the shipped adapter runs non-interactive
/// commands with paging, prompts and optional index writes disabled, so opening, refreshing or comparing a repository does not
/// leave a lock, a changed ref or a half-written index behind. It also validates commit IDs before building a command, so a stale
/// snapshot produces a problem rather than a command assembled from arbitrary text.</remarks>
public interface IGitRepositoryReader
{
    /// <summary>Opens a repository and captures its refs and working-tree change count.</summary>
    /// <remarks>Accepts a path inside a working tree and resolves the root from it. The snapshot keeps direct local and
    /// remote-tracking refs only - symbolic refs such as <c>origin/HEAD</c> are skipped - and counts working-tree status entries,
    /// including untracked files, with a rename counted once. A folder that does not exist or is not a working tree is a problem,
    /// never an empty snapshot.</remarks>
    /// <param name="path">A folder inside the working tree to open.</param>
    /// <param name="ct">Cancels the underlying reads.</param>
    /// <returns>The root, refs, working-tree summary and, when available, the registered repository identity.</returns>
    Task<Result<RepositorySnapshot>> OpenAsync(string path, CancellationToken ct = default);
    /// <summary>Returns every best common ancestor; callers reject zero or ambiguous results.</summary>
    /// <remarks>More than one entry means the histories meet at several equally good points, so any single choice would make a
    /// comparison arbitrary; an empty list means the two commits share no history. The caller decides whether ambiguity is
    /// acceptable, which is why this method reports all answers instead of picking one.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="left">A commit ID in the repository.</param>
    /// <param name="right">Another commit ID in the repository.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Every best common ancestor, in Git's order.</returns>
    Task<Result<IReadOnlyList<string>>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default);
    /// <summary>Counts commits unique to each side relative to the proposed parent/head pair.</summary>
    /// <remarks>Ahead counts commits reachable from the head but not the parent; behind counts parent commits missing from the head.
    /// The pair is a divergence measure, not a merge status: a nonzero behind value is exactly when a merge-base diff stops being a
    /// preview of the merge result.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="parent">The parent-side commit ID.</param>
    /// <param name="head">The layer-side commit ID.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The head's own commit count and the parent's unmerged commit count.</returns>
    Task<Result<(int Ahead, int Behind)>> CountsAsync(string root, string parent, string head, CancellationToken ct = default);
    /// <summary>Reads machine-parsed file metadata for a fixed pair of commit IDs.</summary>
    /// <remarks>One <see cref="FileChange"/> per changed path: kind, old and new path, line counts and file modes. The output is
    /// parsed from NUL-delimited metadata so spaces, Unicode and newlines in paths stay unambiguous, and renames are detected by
    /// content similarity - a rename Git cannot detect appears as a delete plus an add. No patch text is read here, making this the
    /// cheap call that fills a comparison's file list before any large content is loaded.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="base">The base-side commit ID, normally the merge base.</param>
    /// <param name="head">The head-side commit ID.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>File metadata in Git's report order.</returns>
    Task<Result<IReadOnlyList<FileChange>>> FilesAsync(string root, string @base, string head, CancellationToken ct = default);
    /// <summary>Loads the patch for one previously listed file, subject to the display-size limit.</summary>
    /// <remarks>The file must come from <see cref="FilesAsync"/> for the same commit pair: both paths are used to select the patch
    /// section, because a rename whose old path was reused by another file would otherwise return the wrong one. Output is bounded
    /// by the adapter's display limit; a file over it fails with <see cref="OutputLimitException"/> instead of being truncated, so
    /// the UI can offer a smaller comparison. Hunk headers, line coordinates and the flat line list are parsed here once and reused
    /// for rendering, search, review anchors and history analysis.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="base">The same base-side commit ID used for the file list.</param>
    /// <param name="head">The same head-side commit ID used for the file list.</param>
    /// <param name="file">One entry from <see cref="FilesAsync"/> for this commit pair.</param>
    /// <param name="ct">Cancels the read and kills the child process when the adapter runs one.</param>
    /// <returns>The raw patch, its hunks and a flat line stream.</returns>
    Task<Result<FileDiff>> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default);
}
