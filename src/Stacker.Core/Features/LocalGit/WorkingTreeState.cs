namespace Stacker.Core;

/// <summary>What the working tree currently holds, including any operation that must be finished first.</summary>
/// <remarks>Every decision is derived from one status snapshot, so the UI cannot see a branch name from one read and a conflict count from another. Untracked files never block a rewrite; only tracked changes do, and those can be stashed. A conflicted entry is tracked, so a conflicted tree also reports tracked changes. The branch name is null when HEAD is detached, which the restack planner treats as a blocker.</remarks>
/// <param name="Branch">Short branch name, or null when HEAD is detached.</param>
/// <param name="Operation">Unfinished operation, or <see cref="WorkingOperation.None"/>.</param>
/// <param name="Entries">Every status entry, tracked and untracked, in the order Git reported them.</param>
public sealed record WorkingTreeState(string? Branch, WorkingOperation Operation, IReadOnlyList<WorkingTreeEntry> Entries)
{
    /// <summary>Neutral state for a detached repository with no entries and no operation.</summary>
    /// <remarks>It is a shared instance, not a detection result; callers should test the derived flags rather than reference equality.</remarks>
    public static readonly WorkingTreeState Empty = new(null, WorkingOperation.None, []);
    /// <summary>Paths of the unmerged entries, in status order, for conflict panels and recovery messages.</summary>
    /// <remarks>A new array is built on each access; it is empty when there is no conflict.</remarks>
    public IReadOnlyList<string> ConflictedFiles => Entries.Where(entry => entry.IsConflicted).Select(entry => entry.Path).ToArray();
    /// <summary>Whether any entry is unmerged, which forces resolution before commit, restack or undo.</summary>
    /// <remarks>A conflicted entry is also tracked, so this also implies <see cref="HasTrackedChanges"/>; the separate flag exists for conflict-specific wording.</remarks>
    public bool HasConflicts => Entries.Any(entry => entry.IsConflicted);
    /// <summary>Whether Git has an operation in progress, so branch-moving commands must be refused until it is finished or aborted.</summary>
    public bool HasOperation => Operation != WorkingOperation.None;
    /// <summary>Entries Git tracks, including staged, unstaged and conflicted paths; untracked entries are excluded.</summary>
    /// <remarks>This is the set that blocks a rewrite, and the stash path uses the same split: a plain stash does not include untracked entries. The array is rebuilt on each access from Entries.</remarks>
    public IReadOnlyList<WorkingTreeEntry> TrackedChanges => Entries.Where(entry => !entry.IsUntracked).ToArray();
    /// <summary>Whether any tracked entry exists; an untracked-only tree passes this and can be restacked.</summary>
    public bool HasTrackedChanges => Entries.Any(entry => !entry.IsUntracked);
    /// <summary>Whether the tree has entries but none of them tracked, the one dirty state that does not block a rewrite.</summary>
    /// <remarks>False for an empty tree, so "untracked only" never describes a clean repository.</remarks>
    public bool HasUntrackedOnly => Entries.Count > 0 && !HasTrackedChanges;
    /// <summary>Only a rebase can be resumed by the app; a merge or cherry-pick is finished by committing.</summary>
    /// <remarks>The app drives <c>rebase --continue</c> and <c>rebase --skip</c>. Merge, cherry-pick and revert have no resume path here: their conflict is resolved and then committed, so the preview offers Abort but not Continue.</remarks>
    public bool CanContinue => Operation == WorkingOperation.Rebase;
    /// <summary>Whether an operation can be aborted; true for every operation other than <see cref="WorkingOperation.None"/>.</summary>
    /// <remarks>The abort action is offered from this flag and dispatches to the matching Git abort command for the detected operation.</remarks>
    public bool CanAbort => Operation != WorkingOperation.None;
    /// <summary>Whether the UI must surface the state before an action: an operation in progress or any conflict.</summary>
    /// <remarks>The sync preview uses it to disable Push and Restore and to show the operation or conflict panel; it does not by itself prevent a plain status read.</remarks>
    public bool NeedsAttention => HasOperation || HasConflicts;
    /// <summary>Phrase stating the operation in progress, or an empty string when there is none.</summary>
    /// <remarks>Built from <see cref="WorkingOperations.Noun"/> so the banner, the blockers and the labels all say the same thing.</remarks>
    public string OperationLabel => Operation == WorkingOperation.None ? "" : WorkingOperations.Noun(Operation) + " is in progress";
    /// <summary>One-line state for the sync banner: operation, conflict or change count, then the current branch.</summary>
    /// <remarks>The change count is shown only when there is no operation and no conflict; otherwise the operation or conflict text replaces it so a stale uncommitted count is not reported during a rewrite. Parts are joined with " · ". Counts use the literal "(s)" suffix and are recomputed from the entries, and the branch suffix is omitted when HEAD is detached.</remarks>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HasOperation)
                parts.Add(OperationLabel);
            if (HasConflicts)
                parts.Add($"{ConflictedFiles.Count} conflicted file(s)");
            if (!HasOperation && !HasConflicts)
                parts.Add(HasTrackedChanges ? $"{TrackedChanges.Count} uncommitted change(s)" : "no uncommitted changes");
            if (Branch is not null)
                parts.Add("on " + Branch);
            return string.Join(" · ", parts);
        }
    }
}
