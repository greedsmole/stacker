namespace Stacker.Core;

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
                blockedBelow = true;
                cascade = true;
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
                blockedBelow = true;
                cascade = true;
                continue;
            }
            var state = preflight.Layers.FirstOrDefault(layer => string.Equals(layer.Branch, branch, StringComparison.Ordinal));
            var behind = state?.Behind ?? 0;
            var commits = state?.OwnCommitCount ?? 0;
            if (state?.Blocker is { } blocker)
                blockers.Add(blocker);
            refs.TryGetValue("refs/remotes/origin/" + StackRefs.Short(branch), out var remoteTip);
            string? note = null;
            if (elsewhere.Contains(branch))
            {
                blockers.Add($"{StackRefs.Short(branch)} is checked out in another worktree. Remove that worktree or check out another branch there.");
                note = "Checked out in another worktree";
            }
            if (state?.HasMergeCommits == true)
            {
                if (preserveMerges)
                    note = "Merge commits will be preserved";
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
            {
                ReplayBase = state?.ReplayBase ?? parentTip
            });
            cascade |= needsRebase;
        }
        return new(repository.Root, stack.Id, stack.Name, preflight.CurrentBranch ?? StackRefs.Short(stack.Base), preflight.HasOrigin, preserveMerges, steps, blockers.Distinct().ToList());
    }
}
