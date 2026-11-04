using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed class WorkingTreeController(IWorkingTreePresentation owner, WorkspaceRuntime runtime)
{
    public void SetActiveEntry(JournalEntry? entry)
    {
        if (entry == runtime._activeEntry) return;
        runtime._activeEntry = entry;
        owner.Notify(nameof(owner.HasRecovery)); owner.Notify(nameof(owner.RecoverySummary)); owner.Notify(nameof(owner.RecoveryAction)); owner.Notify(nameof(owner.CanRestoreRecovery));
    }

    public async Task<(bool Ok, string Message)> RefreshWorkingTreeAsync(CancellationToken ct = default)
    {
        if (runtime._branchWriter is null || runtime._repository is null) return (false, "Open a repository first.");
        try
        {
            owner.WorkingTree = (await runtime._branchWriter.StateAsync(runtime._repository.Root, ct)).OrThrow();
            var entry = (await runtime._branchWriter.ActiveJournalAsync(runtime._repository.Root, ct)).OrThrow();
            owner.SavedStashes = (await runtime._branchWriter.SavedStashesAsync(runtime._repository.Root, ct)).OrThrow();
            runtime._pendingStash = owner.SavedStashes.FirstOrDefault()?.Sha ?? entry?.StashRef;
            owner.Notify(nameof(owner.SavedStashes));
            SetActiveEntry(entry);
            owner.Notify(nameof(owner.CanRestoreStash));
            owner.Notify(nameof(owner.HasRecovery)); owner.Notify(nameof(owner.RecoverySummary)); owner.Notify(nameof(owner.RecoveryAction)); owner.Notify(nameof(owner.CanRestoreRecovery));
            return (true, owner.WorkingTree.Summary);
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
    }

    public async Task<(bool Ok, string Message)> OpenConflictsInEditorAsync()
    {
        if (runtime._editorLauncher is null || runtime._repository is null) return (false, "Open a repository first.");
        var files = owner.WorkingTree?.ConflictedFiles ?? [];
        if (files.Count == 0) return (false, "There are no conflicted files to open.");
        try
        {
            var command = (await runtime._editorLauncher.OpenAsync(runtime._repository.Root, files, owner.Settings.EditorCommand)).OrThrow();
            return command is null
                ? (false, "No editor is configured. Set one in Settings → Editor command.")
                : (true, $"{files.Count} conflicted file(s) opened in {command}. Stage them when the resolution is complete.");
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
    }

    public async Task<(bool Ok, string Message)> StageConflictsAsync()
    {
        if (runtime._branchWriter is null || runtime._repository is null) return (false, "Open a repository first.");
        var files = owner.WorkingTree?.ConflictedFiles ?? [];
        if (files.Count == 0) return (false, "There are no conflicted files to stage.");
        try
        {
            (await runtime._branchWriter.StageAsync(runtime._repository.Root, files)).OrThrow();
            await RefreshWorkingTreeAsync();
            return (true, $"{files.Count} file(s) staged. Continue when the resolution is complete.");
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
    }

    public async Task<(bool Ok, string Message)> ContinueOperationAsync() =>
        await OperationAsync(async (writer, root, editor) => (await writer.ContinueAsync(root, editor)).OrThrow());

    public async Task<(bool Ok, string Message)> SkipCommitAsync() =>
        await OperationAsync(async (writer, root, _) => (await writer.SkipAsync(root)).OrThrow());

    public async Task<(bool Ok, string Message)> AbortOperationAsync() =>
        await OperationAsync(async (writer, root, _) => (await writer.AbortAsync(root)).OrThrow());

    public async Task<(bool Ok, string Message)> OperationAsync(Func<IGitBranchWriter, string, string?, Task<RestackOutcome>> action)
    {
        if (runtime._branchWriter is null || runtime._repository is null) return (false, "Open a repository first.");
        if (owner.IsSyncing) return (false, "A stack operation is already running.");
        owner.IsSyncing = true; owner.Error = "";
        try
        {
            var editor = await ResolveEditorAsync(runtime._repository.Root);
            var outcome = await action(runtime._branchWriter, runtime._repository.Root, editor);
            owner.Status = outcome.Message;
            if (outcome.Applied || outcome.Conflicted || outcome.RolledBack) await owner.ReloadAsync();
            return outcome.Conflicted ? (false, outcome.Message) : (outcome.Applied || outcome.RolledBack, outcome.Message);
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
        finally { owner.IsSyncing = false; }
    }

    public async Task<(bool Ok, string Message)> CommitAsync(string message, bool stageAll)
    {
        if (runtime._branchWriter is null || runtime._repository is null) return (false, "Open a repository first.");
        if (owner.IsSyncing) return (false, "A stack operation is already running.");
        owner.IsSyncing = true; owner.Error = "";
        try
        {
            var sha = (await runtime._branchWriter.CommitAsync(runtime._repository.Root, message, stageAll)).OrThrow();
            await owner.ReloadAsync();
            return (true, $"Committed {sha[..Math.Min(7, sha.Length)]}.");
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
        finally { owner.IsSyncing = false; }
    }

    public async Task<(bool Ok, string Message)> SwitchToBranchAsync(string branch)
    {
        if (runtime._branchWriter is null || runtime._repository is null) return (false, "Open a repository first.");
        if (owner.IsSyncing) return (false, "A stack operation is already running.");
        owner.IsSyncing = true; owner.Error = "";
        try
        {
            (await runtime._branchWriter.SwitchAsync(runtime._repository.Root, branch)).OrThrow();
            await owner.ReloadAsync();
            return (true, $"Checked out {StackRefs.Short(branch)}.");
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
        finally { owner.IsSyncing = false; }
    }

    public async Task<(bool Ok, string Message)> StashAndRestackAsync(RestackPlan plan)
    {
        if (runtime._branchWriter is null || runtime._repository is null) return (false, "Open a repository first.");
        if (owner.IsSyncing) return (false, "A stack operation is already running.");
        owner.IsSyncing = true; owner.Error = "";
        string? stash;
        try { stash = (await runtime._branchWriter.StashAsync(runtime._repository.Root, $"Stacker: {plan.StackName} before restack")).OrThrow(); }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
        finally { owner.IsSyncing = false; }
        if (stash is null) return (false, "There was nothing to stash.");
        var fresh = await owner.BuildRestackPlanAsync(plan.PreserveMerges);
        if (fresh is null) return (false, "The stack could not be planned after stashing. Your changes are safe in the stash.");
        var outcome = await owner.RestackAsync(fresh with { StashRef = stash });
        await RefreshWorkingTreeAsync();
        var tail = " Your changes are stashed; restore them once the stack is settled.";
        return outcome is { Conflicted: true } ? (false, (outcome.Message + tail)) : (outcome?.Applied == true, (outcome?.Message ?? "The restack did not run.") + tail);
    }

    public async Task<(bool Ok, string Message)> RestoreStashAsync(string? sha = null)
    {
        if (runtime._branchWriter is null || runtime._repository is null) return (false, "Open a repository first.");
        if (owner.IsSyncing) return (false, "A stack operation is already running.");
        owner.IsSyncing = true; owner.Error = "";
        try
        {
            var result = sha is null ? await runtime._branchWriter.RestoreStashAsync(runtime._repository.Root)
                : await runtime._branchWriter.RestoreRecordedStashAsync(runtime._repository.Root, sha);
            var state = result.OrThrow();
            runtime._pendingStash = null; owner.Notify(nameof(owner.CanRestoreStash));
            await owner.ReloadAsync();
            return state.HasConflicts
                ? (false, "Your changes are back, but they conflict with the rewritten branches. Resolve and stage the files, then commit.")
                : (true, "Your stashed changes were restored.");
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
        finally { owner.IsSyncing = false; }
    }

    public async Task<(bool Ok, string Message)> RestoreRecoveryAsync() => owner.CanRestoreRecovery
        ? await AbortOperationAsync() : (false, "Only an interrupted restack can restore recorded branches. Inspect uncertain effects before dismissing their record.");

    public async Task<(bool Ok, string Message)> DismissRecoveryAsync()
    {
        if (runtime._branchWriter is null || runtime._repository is null) return (false, "Open a repository first.");
        try
        {
            (await runtime._branchWriter.DismissAsync(runtime._repository.Root)).OrThrow();
            await RefreshWorkingTreeAsync();
            return (true, "The interrupted run was dismissed. The branches were left exactly as they are.");
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
    }

    public async Task<string?> ResolveEditorAsync(string root)
    {
        if (runtime._editorLauncher is null) return null;
        try { return (await runtime._editorLauncher.ResolveAsync(root, owner.Settings.EditorCommand)).OrThrow(); }
        catch (Exception ex) { owner.SyncProgress = ex.Message; return null; }
    }

}
