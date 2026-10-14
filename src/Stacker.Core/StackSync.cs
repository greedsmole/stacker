namespace Stacker.Core;

/// <summary>Short display name for a full Git ref.</summary>
public static class StackRefs
{
    public static string Short(string reference) => reference.Replace("refs/heads/", "").Replace("refs/remotes/", "");
}

/// <summary>Read-only facts about one layer, gathered before a plan is built.</summary>
/// <remarks>Behind counts commits the parent has and this layer does not; a nonzero value means the parent is not an ancestor.</remarks>
public sealed record SyncLayerState(string Branch, int Behind, int OwnCommitCount, bool HasMergeCommits);

/// <summary>Repository conditions that decide whether rewriting stack refs is safe to attempt.</summary>
public sealed record SyncPreflight(string Root, string? CurrentBranch, bool HasUncommittedChanges, string? InProgressOperation,
    IReadOnlyList<string> BranchesCheckedOutElsewhere, IReadOnlyList<SyncLayerState> Layers, bool HasOrigin);

/// <summary>One layer's planned rewrite; every SHA is captured before anything moves.</summary>
public sealed record RestackStep(int Position, string Branch, string ParentRef, string? OldParentTip, string? BranchTip,
    string? RemoteTip, int CommitCount, bool NeedsRebase, bool Blocked, string? Note)
{
    public string BranchName => StackRefs.Short(Branch);
    public string ParentName => StackRefs.Short(ParentRef);
    public bool IsLocalBranch => Branch.StartsWith("refs/heads/", StringComparison.Ordinal);
    public string Title => $"Layer {Position + 1} · {BranchName}";
    /// <summary>What the preview promises before the user approves anything.</summary>
    public string Status => Blocked ? "Blocked" : NeedsRebase ? $"Rebase {CommitCount} commit(s) onto {ParentName}" : "Up to date";
    public string Detail => Note ?? $"parent {ParentName} · {Short(OldParentTip)} → {Short(BranchTip)}";
    private static string Short(string? sha) => sha is null ? "missing" : sha[..Math.Min(7, sha.Length)];
}

/// <summary>One branch that must be force-pushed after a successful restack, and what the plan believed the remote had.</summary>
public sealed record PushStep(string Branch, string? RemoteTip);

/// <summary>What the user approves before anything is executed; an empty blocker list and one rebase make it executable.</summary>
public sealed record RestackPlan(string RepositoryRoot, string StackId, string StackName, string OriginalBranch,
    bool HasOrigin, bool PreserveMerges, IReadOnlyList<RestackStep> Steps, IReadOnlyList<string> Blockers, string? StashRef = null)
{
    public bool CanExecute => Blockers.Count == 0 && Steps.Any(s => s.NeedsRebase && !s.Blocked);
    public int RebaseCount => Steps.Count(s => s.NeedsRebase && !s.Blocked);
    /// <summary>The layers a restack rewrote; pushing them is what publishes the rewrite.</summary>
    public IReadOnlyList<PushStep> PushSteps => Steps.Where(s => s.NeedsRebase && !s.Blocked).Select(s => new PushStep(s.Branch, s.RemoteTip)).ToArray();
    /// <summary>Every layer the stack can push, rewritten or not.</summary>
    public IReadOnlyList<PushStep> StackPushSteps => Steps.Where(s => !s.Blocked && s.IsLocalBranch).Select(s => new PushStep(s.Branch, s.RemoteTip)).ToArray();
}

/// <summary>How one restack attempt ended.</summary>
public enum RestackStatus
{
    /// <summary>Every planned layer was rewritten.</summary>
    Applied,
    /// <summary>No layer needed a rewrite.</summary>
    NothingToDo,
    /// <summary>The plan was refused before anything moved.</summary>
    Refused,
    /// <summary>An unexpected failure was undone automatically; the repository is as it was.</summary>
    RolledBack,
    /// <summary>A layer did not apply cleanly and the rebase was left in progress for the user to resolve.</summary>
    Conflicted
}

/// <summary>Result of one restack attempt; a rollback has already restored every ref it touched.</summary>
/// <remarks>The journal entry is the record of what happened: it drives undo, resume and recovery.</remarks>
public sealed record RestackOutcome(RestackStatus Status, string? ConflictingBranch, IReadOnlyList<string> ConflictingFiles,
    string Message, Framework.JournalEntry? Journal)
{
    public bool Applied => Status == RestackStatus.Applied;
    public bool Conflicted => Status == RestackStatus.Conflicted;
    public bool RolledBack => Status == RestackStatus.RolledBack;
    /// <summary>The effects that actually moved, which an undo must reverse.</summary>
    public IReadOnlyList<Framework.JournalStep> Steps => Journal?.Moved ?? [];
}

/// <summary>Decides what a stack write would do, entirely from read-only inputs, so the decision is testable without Git.</summary>
public static class StackSyncPlanner
{
    /// <summary>Marks every layer the parent has moved under, then propagates the rewrite upward.</summary>
    /// <remarks>A rebase changes a layer's SHA, so every layer above it must be replayed too even when its own behind count is zero.</remarks>
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
                needsRebase ? note ?? (commits == 0 ? "No commits of its own; it follows its parent" : null) : note));
            cascade |= needsRebase;
        }
        return new(repository.Root, stack.Id, stack.Name, preflight.CurrentBranch ?? StackRefs.Short(stack.Base), preflight.HasOrigin, preserveMerges, steps, blockers.Distinct().ToList());
    }
}
