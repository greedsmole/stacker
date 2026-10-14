// Purpose: Local working-tree actions — conflicts, staging, continuing, stashing, committing and switching branches.
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IEditorLauncher? _editorLauncher;
    private string? _pendingStash;
    private JournalEntry? _activeEntry;
    [ObservableProperty] private WorkingTreeState? _workingTree;

    /// <summary>Shown in the main window whenever an operation or a conflict needs the user.</summary>
    public bool HasOperation => WorkingTree is { NeedsAttention: true };
    public string OperationSummary => WorkingTree?.Summary ?? "";
    public bool CanContinueOperation => WorkingTree is { CanContinue: true };
    public bool CanAbortOperation => WorkingTree is { CanAbort: true };
    public bool HasConflicts => WorkingTree is { HasConflicts: true };
    public bool CanRestoreStash => _pendingStash is not null;
    /// <summary>An interrupted run from an earlier session that the user still has to finish or undo.</summary>
    public bool HasRecovery => _activeEntry is { NeedsAttention: true } && !HasOperation;
    public string RecoverySummary => _activeEntry is null ? "" : string.Join(" · ", _activeEntry.Kind, _activeEntry.Phase.ToString(), _activeEntry.Moved.Count + " branch(es)", _activeEntry.Message);
    private void SetActiveEntry(JournalEntry? entry)
    {
        if (entry is null && _activeEntry is null) return;
        if (entry is not null && _activeEntry is not null && entry.Id == _activeEntry.Id && entry.Phase == _activeEntry.Phase && entry.Steps.Count == _activeEntry.Steps.Count) return;
        _activeEntry = entry;
        OnPropertyChanged(nameof(HasRecovery)); OnPropertyChanged(nameof(RecoverySummary)); OnPropertyChanged(nameof(RecoveryAction));
    }
    partial void OnWorkingTreeChanged(WorkingTreeState? value)
    {
        OnPropertyChanged(nameof(HasOperation)); OnPropertyChanged(nameof(OperationSummary));
        OnPropertyChanged(nameof(CanContinueOperation)); OnPropertyChanged(nameof(CanAbortOperation)); OnPropertyChanged(nameof(HasConflicts));
    }

    /// <summary>Reads the working tree and any stack write that still needs the user.</summary>
    public async Task<(bool Ok, string Message)> RefreshWorkingTreeAsync(CancellationToken ct = default)
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        try
        {
            WorkingTree = await _branchWriter.StateAsync(_repository.Root, ct);
            var entry = await _branchWriter.ActiveJournalAsync(_repository.Root, ct);
            _pendingStash = entry?.StashRef;
            SetActiveEntry(entry);
            OnPropertyChanged(nameof(CanRestoreStash));
            OnPropertyChanged(nameof(HasRecovery)); OnPropertyChanged(nameof(RecoverySummary)); OnPropertyChanged(nameof(RecoveryAction));
            return (true, WorkingTree.Summary);
        }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
    }

    /// <summary>Opens the conflicted files in the editor the user chose.</summary>
    public async Task<(bool Ok, string Message)> OpenConflictsInEditorAsync()
    {
        if (_editorLauncher is null || _repository is null) return (false, "Open a repository first.");
        var files = WorkingTree?.ConflictedFiles ?? [];
        if (files.Count == 0) return (false, "There are no conflicted files to open.");
        try
        {
            var command = await _editorLauncher.OpenAsync(_repository.Root, files, Settings.EditorCommand);
            return command is null
                ? (false, "No editor is configured. Set one in Settings → Editor command.")
                : (true, $"{files.Count} conflicted file(s) opened in {command}. Stage them when the resolution is complete.");
        }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
    }

    /// <summary>Stages the conflicted files so the rebase can continue.</summary>
    public async Task<(bool Ok, string Message)> StageConflictsAsync()
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        var files = WorkingTree?.ConflictedFiles ?? [];
        if (files.Count == 0) return (false, "There are no conflicted files to stage.");
        try
        {
            await _branchWriter.StageAsync(_repository.Root, files);
            await RefreshWorkingTreeAsync();
            return (true, $"{files.Count} file(s) staged. Continue when the resolution is complete.");
        }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
    }

    /// <summary>Resumes the interrupted rebase, using the configured editor for the commit message.</summary>
    public async Task<(bool Ok, string Message)> ContinueOperationAsync() =>
        await OperationAsync(async (writer, root, editor) => await writer.ContinueAsync(root, editor));

    /// <summary>Drops the commit that conflicted and resumes the rebase.</summary>
    public async Task<(bool Ok, string Message)> SkipCommitAsync() =>
        await OperationAsync(async (writer, root, _) => await writer.SkipAsync(root));

    /// <summary>Aborts the operation in progress and restores every branch the interrupted run moved.</summary>
    public async Task<(bool Ok, string Message)> AbortOperationAsync() =>
        await OperationAsync(async (writer, root, _) => await writer.AbortAsync(root));

    private async Task<(bool Ok, string Message)> OperationAsync(Func<IGitBranchWriter, string, string?, Task<RestackOutcome>> action)
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        if (IsSyncing) return (false, "A stack operation is already running.");
        IsSyncing = true; Error = "";
        try
        {
            var editor = await ResolveEditorAsync(_repository.Root);
            var outcome = await action(_branchWriter, _repository.Root, editor);
            Status = outcome.Message;
            if (outcome.Applied || outcome.Conflicted || outcome.RolledBack) await ReloadAsync();
            return outcome.Conflicted ? (false, outcome.Message) : (outcome.Applied || outcome.RolledBack, outcome.Message);
        }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
        finally { IsSyncing = false; }
    }

    /// <summary>Commits the working tree; the user's hooks and signing configuration apply.</summary>
    public async Task<(bool Ok, string Message)> CommitAsync(string message, bool stageAll)
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        if (IsSyncing) return (false, "A stack operation is already running.");
        IsSyncing = true; Error = "";
        try
        {
            var sha = await _branchWriter.CommitAsync(_repository.Root, message, stageAll);
            await ReloadAsync();
            return (true, $"Committed {sha[..Math.Min(7, sha.Length)]}.");
        }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
        finally { IsSyncing = false; }
    }

    /// <summary>Checks out an existing local branch.</summary>
    public async Task<(bool Ok, string Message)> SwitchToBranchAsync(string branch)
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        if (IsSyncing) return (false, "A stack operation is already running.");
        IsSyncing = true; Error = "";
        try
        {
            await _branchWriter.SwitchAsync(_repository.Root, branch);
            await ReloadAsync();
            return (true, $"Checked out {StackRefs.Short(branch)}.");
        }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
        finally { IsSyncing = false; }
    }

    /// <summary>Stashes the tracked changes, restacks the stack, and leaves the stash for the user to restore.</summary>
    public async Task<(bool Ok, string Message)> StashAndRestackAsync(RestackPlan plan)
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        if (IsSyncing) return (false, "A stack operation is already running.");
        IsSyncing = true; Error = "";
        string? stash;
        try { stash = await _branchWriter.StashAsync(_repository.Root, $"Stacker: {plan.StackName} before restack"); }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
        finally { IsSyncing = false; }
        if (stash is null) return (false, "There was nothing to stash.");
        var fresh = await BuildRestackPlanAsync(plan.PreserveMerges);
        if (fresh is null) return (false, "The stack could not be planned after stashing. Your changes are safe in the stash.");
        var outcome = await RestackAsync(fresh with { StashRef = stash });
        await RefreshWorkingTreeAsync();
        var tail = " Your changes are stashed; restore them once the stack is settled.";
        return outcome is { Conflicted: true } ? (false, (outcome.Message + tail)) : (outcome?.Applied == true, (outcome?.Message ?? "The restack did not run.") + tail);
    }

    /// <summary>Pops the stash the last restack recorded, reporting a conflicting pop as conflicts to resolve.</summary>
    public async Task<(bool Ok, string Message)> RestoreStashAsync()
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        if (IsSyncing) return (false, "A stack operation is already running.");
        IsSyncing = true; Error = "";
        try
        {
            var state = await _branchWriter.RestoreStashAsync(_repository.Root);
            _pendingStash = null; OnPropertyChanged(nameof(CanRestoreStash));
            await ReloadAsync();
            return state.HasConflicts
                ? (false, "Your changes are back, but they conflict with the rewritten branches. Resolve and stage the files, then commit.")
                : (true, "Your stashed changes were restored.");
        }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
        finally { IsSyncing = false; }
    }

    /// <summary>Restores what an interrupted run moved, from the entry the banner is showing.</summary>
    public async Task<(bool Ok, string Message)> RestoreRecoveryAsync() => await AbortOperationAsync();

    /// <summary>Closes the entry without touching the repository; the branches stay as they are.</summary>
    public async Task<(bool Ok, string Message)> DismissRecoveryAsync()
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        try
        {
            await _branchWriter.DismissAsync(_repository.Root);
            await RefreshWorkingTreeAsync();
            return (true, "The interrupted run was dismissed. The branches were left exactly as they are.");
        }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
    }

    /// <summary>What the banner offers for the entry it is showing.</summary>
    public string RecoveryAction => _activeEntry is { CanUndo: true } && _activeEntry.Moved.Count > 0 ? "Restore" : "Dismiss";

    /// <summary>The editor command to use, or null when none resolves and git should keep the prepared message.</summary>
    private async Task<string?> ResolveEditorAsync(string root)
    {
        if (_editorLauncher is null) return null;
        try { return await _editorLauncher.ResolveAsync(root, Settings.EditorCommand); }
        catch (Exception ex) { SyncProgress = ex.Message; return null; }
    }
}
