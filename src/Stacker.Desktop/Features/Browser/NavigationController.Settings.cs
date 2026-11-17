using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class NavigationController
{
    public async Task<bool> SaveStackAsync(StackDefinition stack)
    {
        if (runtime._repository is null || runtime._document is null)
            return false;
        var updated = runtime._document.Stacks.Any(s => s.Id == stack.Id) ? runtime._document.Stacks.Select(s => s.Id == stack.Id ? stack : s).ToArray() : runtime._document.Stacks.Append(stack).ToArray();
        return await SaveStacksAsync(updated);
    }

    public async Task DeleteSelectedStackAsync()
    {
        if (runtime._document is not null && owner.SelectedStack is not null && runtime._activeGroup?.IsRemote != true)
            await SaveStacksAsync(runtime._document.Stacks.Where(s => s.Id != owner.SelectedStack.Id).ToArray());
    }

    public async Task<bool> SaveStacksAsync(IReadOnlyList<StackDefinition> stacks)
    {
        if (runtime._repository is null || runtime._document is null)
            return false;
        try
        {
            (await runtime._store.SaveAsync(runtime._repository.Root, stacks, runtime._document.Revision)).OrThrow();
            // The refresh reports its own problems; the save has already succeeded by here.
            runtime._applyingChanges = true;
            try
            {
                await RefreshAsync();
            }
            finally { runtime._applyingChanges = false; }
            return true;
        }
        catch (Exception ex) { owner.Error = ex.Message; return false; }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            runtime._executable.Override = owner.Settings.GitPath;
            if (runtime._commandLog is not null)
                runtime._commandLog.GitExecutablePath = runtime._executable.Resolve();
            if (runtime._ghExecutable is not null)
            {
                runtime._ghExecutable.Override = owner.Settings.GhPath;
                runtime._ghExecutable.UseSavedCredentials = owner.Settings.UseSavedGhCredentials;
            }
            (await runtime._settingsStore.SaveAsync(owner.Settings)).OrThrow();
        }
        catch (Exception ex) { owner.Error = "Cannot save settings: " + ex.Message; throw; }
    }

    public async Task SetWrapCodeAsync(bool enabled)
    {
        owner.Settings.WrapCode = enabled;
        foreach (var section in runtime._views.Values.SelectMany(views => views).Distinct())
            section.WrapCode = enabled;
        await SaveSettingsAsync();
    }

    public async Task SetGroupFilesByFolderAsync(bool enabled)
    {
        owner.Settings.GroupFilesByFolder = enabled;
        foreach (var section in owner.Sections.Concat(runtime._views.Values.SelectMany(views => views)).Distinct())
            section.GroupFilesByFolder = enabled;
        await SaveSettingsAsync();
    }

    public void DisposeViews()
    {
        foreach (var section in runtime._views.Values.SelectMany(v => v).Distinct())
            section.Dispose();
        runtime._views.Clear();
    }

    public void Dispose()
    {
        if (runtime._disposed)
            return;
        runtime._disposed = true;
        if (runtime._commandLog is not null)
            runtime._commandLog.Changed -= owner.LogChanged;
        if (runtime._commands is not null)
        {
            runtime._commands.Reported -= owner.ReportCommand;
            runtime._commands.Starting -= owner.CommandStarting;
        }
        runtime._searchLoad?.Cancel();
        runtime._refresh?.Cancel();
        runtime._comparison?.Cancel();
        runtime._githubLoad?.Cancel();
        runtime._coordinator?.Dispose();
        owner.DisposeReviewSessions();
        DisposeViews();
    }
}
