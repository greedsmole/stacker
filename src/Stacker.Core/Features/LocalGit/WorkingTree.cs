namespace Stacker.Core;

/// <summary>Unfinished operation the repository is in the middle of.</summary>
public enum WorkingOperation { None, Rebase, Am, Merge, CherryPick, Revert }

/// <summary>Names an unfinished operation in the form a sentence needs ("A rebase is in progress").</summary>
public static class WorkingOperations
{
    public static string Noun(WorkingOperation operation) => operation switch
    {
        WorkingOperation.Rebase => "A rebase",
        WorkingOperation.Am => "A patch application",
        WorkingOperation.Merge => "A merge",
        WorkingOperation.CherryPick => "A cherry-pick",
        WorkingOperation.Revert => "A revert",
        _ => "An operation"
    };
}

/// <summary>One working-tree entry from git status, with the flags the UI, the stash and the commit dialog need.</summary>
public sealed record WorkingTreeEntry(string Path, string Status, bool IsStaged, bool IsConflicted, bool IsUntracked)
{
    public string Display => Status + " " + Path;
}

/// <summary>What the working tree currently holds, including any operation that must be finished first.</summary>
/// <remarks>Untracked files never block a rewrite; only tracked changes do, and those can be stashed.</remarks>
public sealed record WorkingTreeState(string? Branch, WorkingOperation Operation, IReadOnlyList<WorkingTreeEntry> Entries)
{
    public static readonly WorkingTreeState Empty = new(null, WorkingOperation.None, []);
    public IReadOnlyList<string> ConflictedFiles => Entries.Where(entry => entry.IsConflicted).Select(entry => entry.Path).ToArray();
    public bool HasConflicts => Entries.Any(entry => entry.IsConflicted);
    public bool HasOperation => Operation != WorkingOperation.None;
    public IReadOnlyList<WorkingTreeEntry> TrackedChanges => Entries.Where(entry => !entry.IsUntracked).ToArray();
    public bool HasTrackedChanges => Entries.Any(entry => !entry.IsUntracked);
    public bool HasUntrackedOnly => Entries.Count > 0 && !HasTrackedChanges;
    /// <summary>Only a rebase can be resumed by the app; a merge or cherry-pick is finished by committing.</summary>
    public bool CanContinue => Operation == WorkingOperation.Rebase;
    public bool CanAbort => Operation != WorkingOperation.None;
    public bool NeedsAttention => HasOperation || HasConflicts;
    public string OperationLabel => Operation == WorkingOperation.None ? "" : WorkingOperations.Noun(Operation) + " is in progress";
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HasOperation) parts.Add(OperationLabel);
            if (HasConflicts) parts.Add($"{ConflictedFiles.Count} conflicted file(s)");
            if (!HasOperation && !HasConflicts)
                parts.Add(HasTrackedChanges ? $"{TrackedChanges.Count} uncommitted change(s)" : "no uncommitted changes");
            if (Branch is not null) parts.Add("on " + Branch);
            return string.Join(" · ", parts);
        }
    }
}
