namespace Stacker.Core;

/// <summary>What the user approves before anything is executed; an empty blocker list and one rebase make it executable.</summary>
/// <remarks>The plan is the approval surface: it lists every layer, why it is blocked, and what a push would publish. It is built from a snapshot and can go stale; the effect re-verifies branch tips before moving anything.</remarks>
/// <param name="RepositoryRoot">Root the plan operates on.</param>
/// <param name="StackId">Identity of the stack definition the plan was built from.</param>
/// <param name="StackName">Display name of that stack.</param>
/// <param name="OriginalBranch">Branch to return to after the restack and for recovery to switch back to; falls back to the short base ref when HEAD was detached, although detached HEAD is itself a blocker.</param>
/// <param name="HasOrigin">Whether the repository has an origin, which decides whether a push can follow.</param>
/// <param name="PreserveMerges">Whether merge commits are kept with a merge-aware replay; when false, a layer with merge commits is blocked instead of flattened.</param>
/// <param name="Steps">One entry per stack layer, in stack order, including blocked layers.</param>
/// <param name="Blockers">User-facing reasons the plan cannot execute, deduplicated; an empty list is necessary but not sufficient for execution.</param>
/// <param name="StashRef">Stash created to clean the tree before this restack, if any, so undo and restore can find it.</param>
public sealed record RestackPlan(string RepositoryRoot, string StackId, string StackName, string OriginalBranch,
    bool HasOrigin, bool PreserveMerges, IReadOnlyList<RestackStep> Steps, IReadOnlyList<string> Blockers, string? StashRef = null)
{
    /// <summary>Whether execution may start: an empty blocker list and at least one layer to rebase.</summary>
    /// <remarks>One blocker blocks the whole plan, even when the step it names is not the one the user cares about, because a partial rewrite would leave the stack half-based. A plan with no blockers and nothing to rebase is not executable either.</remarks>
    public bool CanExecute => Blockers.Count == 0 && Steps.Any(s => s.NeedsRebase && !s.Blocked);
    /// <summary>Number of layers the plan would rewrite, i.e. steps that need a rebase and are not blocked.</summary>
    public int RebaseCount => Steps.Count(s => s.NeedsRebase && !s.Blocked);
    /// <summary>The layers a restack rewrote; pushing them is what publishes the rewrite.</summary>
    /// <remarks>A normal push publishes only these layers; an up-to-date layer keeps its existing remote commit. Each step carries the remote tip the plan captured before the rewrite.</remarks>
    public IReadOnlyList<PushStep> PushSteps => Steps.Where(s => s.NeedsRebase && !s.Blocked).Select(s => new PushStep(s.Branch, s.RemoteTip)).ToArray();
    /// <summary>Every layer the stack can push, rewritten or not.</summary>
    /// <remarks>Whole-stack push also publishes up-to-date layers, so a remote branch that is behind the current local commit is updated too; blocked layers are skipped because the plan could not establish them as safely publishable (missing ref, non-local ref, or a failure below).</remarks>
    public IReadOnlyList<PushStep> StackPushSteps => Steps.Where(s => !s.Blocked && s.IsLocalBranch).Select(s => new PushStep(s.Branch, s.RemoteTip)).ToArray();
}
