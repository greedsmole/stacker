namespace Stacker.Core;

/// <summary>Immutable view of the repository refs and working-tree summary used by a comparison.</summary>
/// <param name="Root">Absolute working-tree root Git reports; every path the app resolves for this repository starts here.</param>
/// <param name="Refs">Direct local and remote-tracking refs from the moment of the snapshot, or the synthetic PR refs when the snapshot
/// was projected onto pull-request SHAs for a review comparison.</param>
/// <param name="ChangedFiles">Number of working-tree status entries, untracked files included and a rename counted once. It summarizes
/// the tree, not the difference between two commits.</param>
/// <remarks>The record is a value: taking a new snapshot produces a new instance, and nothing in Core mutates an existing one. A
/// comparison therefore keeps describing the state it was requested for even if refs move while it runs.</remarks>
public sealed record RepositorySnapshot(string Root, IReadOnlyList<BranchRef> Refs, int ChangedFiles)
{
    /// <summary>True when the repository carries the offline demo marker, which disables publication and background polling.</summary>
    public bool IsDemo
    {
        get; init;
    }
    /// <summary>The registered repository and worktree identity, or null when the snapshot came from a path without one.</summary>
    /// <remarks>Identity is what keeps history and workspace state attached to a repository when its folder moves; a snapshot without it
    /// falls back to path keys, which is the shape older records used.</remarks>
    public RepositoryIdentity? Identity
    {
        get; init;
    }
    /// <summary>Stable history scope for this repository: the identity scope when known, otherwise a path-based fallback.</summary>
    /// <remarks>PR bindings and history queries key on this value, so it survives moving or renaming the folder when an identity exists.
    /// The fallback reproduces the key older versions wrote from the root path, which is why it is computed instead of dropped.</remarks>
    public string Scope => Identity?.Scope ?? "local:" + Root;
    /// <summary>Key for the saved workspace view of this working tree, so two worktrees of one repository keep separate views.</summary>
    /// <remarks>Uses worktree identity when available and the root path otherwise; the path form is also what older workspace documents used,
    /// so they can be imported on first load instead of being ignored.</remarks>
    public string WorkspaceKey => Identity is { } identity ? "worktree:" + identity.Worktree.Value : Root;
}
