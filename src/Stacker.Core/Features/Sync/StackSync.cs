namespace Stacker.Core;

/// <summary>Short display name for a full Git ref.</summary>
/// <remarks>Both prefixes are removed with replacement rather than a prefix check, so a remote-tracking ref keeps its remote name ("refs/remotes/origin/A" becomes "origin/A"). The result is for labels and messages, not for an update-ref argument.</remarks>
public static class StackRefs
{
    /// <summary>Strips the local or remote-tracking prefix for display.</summary>
    /// <param name="reference">Full ref, or a name without a prefix.</param>
    /// <returns>The shortened name; a name without either prefix is returned unchanged.</returns>
    public static string Short(string reference) => reference.Replace("refs/heads/", "").Replace("refs/remotes/", "");
}

/// <summary>Read-only facts about one layer, gathered before a plan is built.</summary>
/// <remarks>Behind counts commits the parent has and this layer does not; a nonzero value means the parent is not an ancestor. OwnCommitCount is measured from the replay base, so it is what a rebase would replay rather than every commit on the branch. These values are a snapshot and can go stale before execution; the restack effect re-verifies the plan against the refs.</remarks>
/// <param name="Branch">Full ref of the layer as declared by the stack.</param>
/// <param name="Behind">Commits the parent has and this layer does not; nonzero means the layer must replay onto the parent.</param>
/// <param name="OwnCommitCount">Commits from the replay base to the layer head, i.e. what a rebase would replay; zero means the layer only points at its parent's history.</param>
/// <param name="HasMergeCommits">Whether those own commits include a merge, which a linear replay would flatten.</param>
/// <param name="ReplayBase">Fork point used as the rebase upstream so only own commits replay; null when it could not be established, in which case <c>Blocker</c> explains why.</param>
/// <param name="Blocker">Why this layer cannot be planned safely, already phrased for the user; null when the layer is plannable.</param>
public sealed record SyncLayerState(string Branch, int Behind, int OwnCommitCount, bool HasMergeCommits,
    string? ReplayBase = null, string? Blocker = null);

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

/// <summary>One layer's planned rewrite; every SHA is captured before anything moves.</summary>
/// <remarks>OldParentTip and BranchTip are read from the snapshot, so the preview, the journal and the push lease all describe the same commits. RemoteTip is the remote-tracking commit at plan time and becomes the force-with-lease value. A blocked step may have null tips. NeedsRebase is true when the parent moved or when any layer below is rewritten; such a step is never dropped, even with no commits of its own, because it must still follow its rewritten parent.</remarks>
/// <param name="Position">Zero-based position in the stack; user-facing labels use this plus one.</param>
/// <param name="Branch">Full ref of the layer.</param>
/// <param name="ParentRef">Full ref of its parent: the previous layer, or the stack base for the first.</param>
/// <param name="OldParentTip">Parent commit the plan was built against, and the default rebase base; null when that ref was missing.</param>
/// <param name="BranchTip">Layer commit at plan time, used as the journal's old value; null when the ref was missing.</param>
/// <param name="RemoteTip">Remote-tracking commit of the same branch, used as the push lease; null when the plan captured none, for example no remote-tracking ref or a blocked layer.</param>
/// <param name="CommitCount">Own commits the rebase would replay; zero means the layer only follows its parent.</param>
/// <param name="NeedsRebase">Whether the layer must move because its parent moved or a layer below is being rewritten.</param>
/// <param name="Blocked">Whether this layer cannot be rewritten because of its own state or a blocked layer below.</param>
/// <param name="Note">Optional sentence shown instead of the parent and tip detail; when several per-layer conditions apply, every blocker is kept but the last note assigned wins.</param>
public sealed record RestackStep(int Position, string Branch, string ParentRef, string? OldParentTip, string? BranchTip,
    string? RemoteTip, int CommitCount, bool NeedsRebase, bool Blocked, string? Note)
{
    /// <summary>Commit to use as the rebase upstream so only the layer's own commits replay.</summary>
    /// <remarks>The planner stores the fork point from preflight when it is known, otherwise the parent tip; the effect passes it as the base of <c>rebase --onto</c>. A blocked step is never executed, so an executable step always carries a value.</remarks>
    public string? ReplayBase { get; init; }
    /// <summary>Short layer name for labels and messages.</summary>
    public string BranchName => StackRefs.Short(Branch);
    /// <summary>Short parent name for labels and messages.</summary>
    public string ParentName => StackRefs.Short(ParentRef);
    /// <summary>Whether the layer is a local branch, the only ref namespace Stacker may rewrite; a remote-tracking layer is blocked instead.</summary>
    public bool IsLocalBranch => Branch.StartsWith("refs/heads/", StringComparison.Ordinal);
    /// <summary>Preview title: the one-based layer number and the short branch name.</summary>
    public string Title => $"Layer {Position + 1} · {BranchName}";
    /// <summary>What the preview promises before the user approves anything.</summary>
    /// <remarks>Blocked wins over Rebase, which wins over Up to date. A layer that only follows a rewritten parent shows "Rebase 0 commit(s)" because it must still be replayed; the count is a prediction, and the effect reports commits that were already present and dropped.</remarks>
    public string Status => Blocked ? "Blocked" : NeedsRebase ? $"Rebase {CommitCount} commit(s) onto {ParentName}" : "Up to date";
    /// <summary>Secondary preview line: the step's note when present, otherwise the parent name and the short tips.</summary>
    /// <remarks><see cref="RestackStep.Short"/> caps each id at seven characters, so full ids never widen the row; a null tip would render as "missing" instead of a blank.</remarks>
    public string Detail => Note ?? $"parent {ParentName} · {Short(OldParentTip)} → {Short(BranchTip)}";
    /// <summary>Formats a commit id for display: null becomes "missing", otherwise at most seven characters.</summary>
    /// <remarks>Uses <c>Math.Min(7, sha.Length)</c>, so an id shorter than seven characters is not padded and slicing cannot throw.</remarks>
    /// <param name="sha">Commit id, or null when the ref was missing.</param>
    /// <returns>The display form.</returns>
    private static string Short(string? sha) => sha is null ? "missing" : sha[..Math.Min(7, sha.Length)];
}

/// <summary>One branch that must be force-pushed after a successful restack, and what the plan believed the remote had.</summary>
/// <remarks>RemoteTip is captured before any branch moves and is used as the force-with-lease value; if origin moved after the preview the push fails instead of overwriting. A null value means the remote branch did not exist, and the lease then requires it to still be absent.</remarks>
/// <param name="Branch">Full local ref to publish.</param>
/// <param name="RemoteTip">Remote commit the lease requires, or null when the branch was absent.</param>
public sealed record PushStep(string Branch, string? RemoteTip);

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

/// <summary>How one restack attempt ended.</summary>
public enum RestackStatus
{
    /// <summary>Every planned layer was rewritten.</summary>
    /// <remarks>The original branch is checked out again before this status is returned.</remarks>
    Applied,
    /// <summary>No layer needed a rewrite.</summary>
    NothingToDo,
    /// <summary>The plan was refused before anything moved.</summary>
    /// <remarks>This holds for a refusal raised while planning. A refusal detected in the middle of a run leaves the journal entry describing the layers that already moved, so the entry, not this status, is the source of truth in that case.</remarks>
    Refused,
    /// <summary>An unexpected failure was undone automatically; the repository is as it was.</summary>
    RolledBack,
    /// <summary>A layer did not apply cleanly and the rebase was left in progress for the user to resolve.</summary>
    Conflicted
}

/// <summary>Result of one restack attempt; a rollback has already restored every ref it touched.</summary>
/// <remarks>The journal entry is the record of what happened: it drives undo, resume and recovery. The status decides what the entry can still do, from undoable after <see cref="RestackStatus.Applied"/> to needing the user after <see cref="RestackStatus.Conflicted"/> and already reversed after <see cref="RestackStatus.RolledBack"/>.</remarks>
/// <param name="Status">How the attempt ended.</param>
/// <param name="ConflictingBranch">Layer that stopped on a conflict, or null when the failure was not a per-layer conflict.</param>
/// <param name="ConflictingFiles">Paths Git reported as unmerged for that layer; empty otherwise.</param>
/// <param name="Message">User-facing account of the attempt, including any restoration that happened.</param>
/// <param name="Journal">Entry recording the attempt; null for a refusal that never opened one, and otherwise the plan and moved steps that undo and resume rely on.</param>
public sealed record RestackOutcome(RestackStatus Status, string? ConflictingBranch, IReadOnlyList<string> ConflictingFiles,
    string Message, Framework.JournalEntry? Journal)
{
    /// <summary>Whether every planned layer was rewritten.</summary>
    public bool Applied => Status == RestackStatus.Applied;
    /// <summary>Whether the run stopped on a conflict and left the rebase in progress.</summary>
    public bool Conflicted => Status == RestackStatus.Conflicted;
    /// <summary>Whether the run was reversed, so no branch should still point at a new commit.</summary>
    public bool RolledBack => Status == RestackStatus.RolledBack;
    /// <summary>The effects that actually moved, which an undo must reverse.</summary>
    /// <remarks>Read from the journal's moved steps, so a planned or failed step is never offered for undo; the value is empty when there is no journal.</remarks>
    public IReadOnlyList<Framework.JournalStep> Steps => Journal?.Moved ?? [];
}

/// <summary>Decides what a stack write would do, entirely from read-only inputs, so the decision is testable without Git.</summary>
/// <remarks>The planner is pure: it only reads <see cref="RepositorySnapshot"/> and <see cref="SyncPreflight"/> and returns a plan. Execution re-verifies the refs before moving them, so a plan that went stale is refused rather than half-applied.</remarks>
public static class StackSyncPlanner
{
    /// <summary>Marks every layer the parent has moved under, then propagates the rewrite upward.</summary>
    /// <remarks>Layers are visited bottom-up and the parent of the first is the stack base. Hazards take three forms: plan-wide blockers, individually blocked steps, and notes. A missing branch or parent ref, or a layer outside <c>refs/heads/</c>, marks that step Blocked and every step above it as blocked by a layer below, because a missing commit can be neither moved nor undone and a remote-tracking ref would move a view of history or detach HEAD instead of advancing a local branch. A ref checked out in another worktree, or a merge commit without <paramref name="preserveMerges"/>, adds a blocker and a note but leaves the step otherwise planned; the plan-level refusal stops execution, because moving another worktree's branch corrupts that worktree and a linear replay would flatten merge topology the user did not approve. A preflight <see cref="SyncLayerState.Blocker"/> (for example a missing fork point) is added verbatim so the layer cannot run with a guessed replay range.
    /// A layer needs a rebase when its behind count is greater than zero or when any layer below is being rewritten. A rebase changes a layer's SHA, so every layer above it must be replayed too even when its own behind count is zero; such a layer is never skipped even with no commits of its own.
    /// An in-progress operation, a detached HEAD or tracked uncommitted changes are plan-wide blockers: an operation already paused would collide with the rebase, a detached HEAD leaves no branch to return to and the restack would finish on the base branch instead, and uncommitted tracked work would be folded into replayed commits or stop the rebase.
    /// The remote tip is read from the origin-tracking ref before anything moves, so a later push can lease against the exact commit this plan saw. ReplayBase is the preflight fork point or the parent tip, which keeps only the layer's own commits in the replay. Blocked steps are still returned with their note so the preview can explain the whole stack, and blockers are deduplicated in first-seen order.</remarks>
    /// <param name="repository">Snapshot of refs; the only source of branch tips.</param>
    /// <param name="stack">Ordered layers and the base ref.</param>
    /// <param name="preflight">Repository and per-layer facts gathered before planning.</param>
    /// <param name="preserveMerges">Whether merge commits are kept by a merge-aware replay; false blocks a layer that contains merges.</param>
    /// <returns>The plan with every step, the captured remote tips and the blockers; callers must still check <see cref="RestackPlan.CanExecute"/>.</returns>
    public static RestackPlan Plan(RepositorySnapshot repository, StackDefinition stack, SyncPreflight preflight, bool preserveMerges = false)
    {
        var refs = repository.Refs.ToDictionary(r => r.Name, r => r.Sha, StringComparer.Ordinal);
        var elsewhere = preflight.BranchesCheckedOutElsewhere.ToHashSet(StringComparer.Ordinal);
        var blockers = new List<string>();
        if (preflight.InProgressOperation is { } operation)
            blockers.Add($"{operation} is already in progress in this repository. Finish or abort it before restacking.");
        if (preflight.CurrentBranch is null)
            blockers.Add("HEAD is detached. Switch to a branch before restacking.");
        if (preflight.HasUncommittedChanges)
            blockers.Add("The working tree has uncommitted changes to tracked files. Commit or stash them before restacking.");
        var steps = new List<RestackStep>();
        var cascade = false;
        var blockedBelow = false;
        for (var position = 0; position < stack.Branches.Count; position++)
        {
            var branch = stack.Branches[position];
            var parentRef = position == 0 ? stack.Base : stack.Branches[position - 1];
            refs.TryGetValue(branch, out var branchTip);
            refs.TryGetValue(parentRef, out var parentTip);
            if (branchTip is null || parentTip is null)
            {
                blockers.Add($"Missing ref: {(branchTip is null ? branch : parentRef)}.");
                steps.Add(new(position, branch, parentRef, null, null, null, 0, false, true, "Missing ref"));
                blockedBelow = true; cascade = true;
                continue;
            }
            if (blockedBelow)
            {
                steps.Add(new(position, branch, parentRef, parentTip, branchTip, null, 0, false, true, "Blocked by a layer below"));
                continue;
            }
            // Only local branches can be rewritten; a remote-tracking layer would detach HEAD instead of moving a branch.
            if (!branch.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                blockers.Add($"{StackRefs.Short(branch)} is not a local branch, so it cannot be rebased. Point this layer at a local branch.");
                steps.Add(new(position, branch, parentRef, parentTip, branchTip, null, 0, false, true, "Not a local branch"));
                blockedBelow = true; cascade = true;
                continue;
            }
            var state = preflight.Layers.FirstOrDefault(layer => string.Equals(layer.Branch, branch, StringComparison.Ordinal));
            var behind = state?.Behind ?? 0;
            var commits = state?.OwnCommitCount ?? 0;
            if (state?.Blocker is { } blocker) blockers.Add(blocker);
            refs.TryGetValue("refs/remotes/origin/" + StackRefs.Short(branch), out var remoteTip);
            string? note = null;
            if (elsewhere.Contains(branch))
            {
                blockers.Add($"{StackRefs.Short(branch)} is checked out in another worktree. Remove that worktree or check out another branch there.");
                note = "Checked out in another worktree";
            }
            if (state?.HasMergeCommits == true)
            {
                if (preserveMerges) note = "Merge commits will be preserved";
                else
                {
                    blockers.Add($"{StackRefs.Short(branch)} contains merge commits, which Stacker does not flatten. Turn on Preserve merge commits or restack this layer manually.");
                    note = "Contains merge commits";
                }
            }
            var needsRebase = cascade || behind > 0;
            // A layer that needs a rebase is never skipped, even with no commits of its own: it must follow its rewritten parent.
            steps.Add(new(position, branch, parentRef, parentTip, branchTip, remoteTip, commits, needsRebase, false,
                needsRebase ? note ?? (commits == 0 ? "No commits of its own; it follows its parent" : null) : note)
            { ReplayBase = state?.ReplayBase ?? parentTip });
            cascade |= needsRebase;
        }
        return new(repository.Root, stack.Id, stack.Name, preflight.CurrentBranch ?? StackRefs.Short(stack.Base), preflight.HasOrigin, preserveMerges, steps, blockers.Distinct().ToList());
    }
}
