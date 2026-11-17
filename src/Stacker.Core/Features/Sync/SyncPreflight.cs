namespace Stacker.Core;

/// <summary>Repository conditions that decide whether rewriting stack refs is safe to attempt.</summary>
/// <remarks>Gathered from one status read, per-layer ancestry probes and one worktree listing, then passed to the pure planner. The values describe the repository at preflight time; execution re-verifies branch tips so a snapshot that went stale is refused rather than half-applied.</remarks>
/// <param name="Root">Repository root the plan will operate on.</param>
/// <param name="CurrentBranch">Short current branch name, or null when HEAD is detached.</param>
/// <param name="HasUncommittedChanges">Whether tracked files have uncommitted changes; untracked files alone do not count.</param>
/// <param name="InProgressOperation">Operation name already phrased for a sentence (for example "A rebase"), or null when none is in progress.</param>
/// <param name="BranchesCheckedOutElsewhere">Full refs checked out in other worktrees, compared by exact ref; such a branch cannot be moved without disturbing that worktree.</param>
/// <param name="Layers">One state per stack layer, in stack order; the planner matches by branch ref and treats a missing entry as zero divergence.</param>
/// <param name="HasOrigin">Whether origin has a URL; without it a restack stays local and push is unavailable.</param>
public sealed record SyncPreflight(string Root, string? CurrentBranch, bool HasUncommittedChanges, string? InProgressOperation,
    IReadOnlyList<string> BranchesCheckedOutElsewhere, IReadOnlyList<SyncLayerState> Layers, bool HasOrigin);
