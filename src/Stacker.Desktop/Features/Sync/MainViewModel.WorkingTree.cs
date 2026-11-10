// Purpose: Local working-tree actions — conflicts, staging, continuing, stashing, committing and switching branches.
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Recorded stashes available for restoration.</summary>
    public IReadOnlyList<SavedStash> SavedStashes { get; set; } = [];
    /// <summary>Latest immutable working-tree snapshot.</summary>
    [ObservableProperty] internal WorkingTreeState? _workingTree;
    /// <summary>Whether Git has an unfinished operation requiring attention.</summary>

    /// <summary>Shown in the main window whenever an operation or a conflict needs the user.</summary>
    public bool HasOperation => WorkingTree is { NeedsAttention: true };
    public string OperationSummary => WorkingTree?.Summary ?? "";
    public bool CanContinueOperation => WorkingTree is { CanContinue: true };
    public bool CanAbortOperation => WorkingTree is { CanAbort: true };
    public bool HasConflicts => WorkingTree is { HasConflicts: true };
    public bool CanRestoreStash => _runtime._pendingStash is not null;
    /// <summary>An interrupted run from an earlier session that the user still has to finish or undo.</summary>
    public bool HasRecovery => _runtime._activeEntry is { NeedsAttention: true } && !HasOperation;
    public bool CanRestoreRecovery => _runtime._activeEntry is { Kind: "restack", NeedsAttention: true } && _runtime._activeEntry.Restorable.Count > 0;
    public string RecoverySummary => _runtime._activeEntry is null ? "" : string.Join(" · ", _runtime._activeEntry.Kind, _runtime._activeEntry.Phase.ToString(),
        _runtime._activeEntry.Moved.Count + (_runtime._activeEntry.Kind == "restack" ? " branch(es)" : " effect(s)"), _runtime._activeEntry.Message);
    internal void SetActiveEntry(JournalEntry? entry) => _workingTreeController.SetActiveEntry(entry);
    partial void OnWorkingTreeChanged(WorkingTreeState? value)
    {
        OnPropertyChanged(nameof(HasOperation)); OnPropertyChanged(nameof(OperationSummary));
        OnPropertyChanged(nameof(CanContinueOperation)); OnPropertyChanged(nameof(CanAbortOperation)); OnPropertyChanged(nameof(HasConflicts));
    }

    /// <summary>Reads the working tree and any stack write that still needs the user.</summary>
    public Task<(bool Ok, string Message)> RefreshWorkingTreeAsync(CancellationToken ct = default) => _workingTreeController.RefreshWorkingTreeAsync(ct);

    /// <summary>Opens the conflicted files in the editor the user chose.</summary>
    public Task<(bool Ok, string Message)> OpenConflictsInEditorAsync() => _workingTreeController.OpenConflictsInEditorAsync();

    /// <summary>Stages the conflicted files so the rebase can continue.</summary>
    public Task<(bool Ok, string Message)> StageConflictsAsync() => _workingTreeController.StageConflictsAsync();

    /// <summary>Resumes the interrupted rebase, using the configured editor for the commit message.</summary>
    public Task<(bool Ok, string Message)> ContinueOperationAsync() => _workingTreeController.ContinueOperationAsync();

    /// <summary>Drops the commit that conflicted and resumes the rebase.</summary>
    public Task<(bool Ok, string Message)> SkipCommitAsync() => _workingTreeController.SkipCommitAsync();

    /// <summary>Aborts the operation in progress and restores every branch the interrupted run moved.</summary>
    public Task<(bool Ok, string Message)> AbortOperationAsync() => _workingTreeController.AbortOperationAsync();

    internal Task<(bool Ok, string Message)> OperationAsync(Func<IGitBranchWriter, string, string?, Task<RestackOutcome>> action) => _workingTreeController.OperationAsync(action);

    /// <summary>Commits the working tree; the user's hooks and signing configuration apply.</summary>
    public Task<(bool Ok, string Message)> CommitAsync(string message, bool stageAll) => _workingTreeController.CommitAsync(message, stageAll);

    /// <summary>Checks out an existing local branch.</summary>
    public Task<(bool Ok, string Message)> SwitchToBranchAsync(string branch) => _workingTreeController.SwitchToBranchAsync(branch);

    /// <summary>Stashes the tracked changes, restacks the stack, and leaves the stash for the user to restore.</summary>
    public Task<(bool Ok, string Message)> StashAndRestackAsync(RestackPlan plan) => _workingTreeController.StashAndRestackAsync(plan);

    /// <summary>Pops the stash the last restack recorded, reporting a conflicting pop as conflicts to resolve.</summary>
    public Task<(bool Ok, string Message)> RestoreStashAsync() => _workingTreeController.RestoreStashAsync();

    /// <summary>Restores the specifically recorded stash identified by its SHA.</summary>
    public Task<(bool Ok, string Message)> RestoreStashAsync(string sha) => _workingTreeController.RestoreStashAsync(sha);

    /// <summary>Restores what an interrupted run moved, from the entry the banner is showing.</summary>
    public Task<(bool Ok, string Message)> RestoreRecoveryAsync() => _workingTreeController.RestoreRecoveryAsync();

    /// <summary>Closes the entry without touching the repository; the branches stay as they are.</summary>
    public Task<(bool Ok, string Message)> DismissRecoveryAsync() => _workingTreeController.DismissRecoveryAsync();

    /// <summary>What the banner offers for the entry it is showing.</summary>
    public string RecoveryAction => CanRestoreRecovery ? "Restore" : "Dismiss";

    /// <summary>The editor command to use, or null when none resolves and git should keep the prepared message.</summary>
    internal Task<string?> ResolveEditorAsync(string root) => _workingTreeController.ResolveEditorAsync(root);
}
