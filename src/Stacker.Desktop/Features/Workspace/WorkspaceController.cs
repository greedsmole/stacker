using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceController(IWorkspaceActionsPresentation owner, WorkspaceRuntime runtime)
{
    public string GroupKey(StackGroupViewModel group) => (group.IsRemote ? "github:" + runtime._remoteSnapshot?.Context.Identity : "local") + ":" + group.Definition.Id;

    public string ViewKey() => owner.Mode + ":" + string.Join("|", runtime._activeGroup?.Layers.Where(l => runtime._selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch) ?? []);

    public void SetWindowActive(bool active)
    {
        runtime._windowActive = active;
        runtime._coordinator ??= new(PollAsync);
        runtime._coordinator.SetActive(active && owner.Settings.BackgroundRefresh && !owner.IsDemo && runtime._repository is not null);
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        if (runtime._repository is null || owner.IsDemo || !runtime._syncGate.Wait(0))
            return;
        try
        {
            await owner.RefreshGitHubCoreAsync(ct, background: true);
            if ((runtime._pendingRemote is null || runtime._pendingRemote.Context.Identity == runtime._remoteSnapshot?.Context.Identity) && owner.ActiveReview is { IsBusy: false } review)
                await review.RefreshDiscussionAsync(ct);
        }
        finally { runtime._syncGate.Release(); }
    }

    public async Task RefreshAllAsync()
    {
        if (!runtime._syncGate.Wait(0))
            return;
        try
        {
            runtime._coordinator?.Reset();
            await owner.RefreshAsync();
            await owner.RefreshGitHubCoreAsync(default, background: false);
            if (owner.ActiveReview is { IsBusy: false } review)
                await review.ReloadAsync();
        }
        catch (Exception ex) { owner.Error = ex.Message; }
        finally { runtime._syncGate.Release(); SetWindowActive(runtime._windowActive); }
    }

    public async Task UpdateChangesAsync()
    {
        if (runtime._reviews.Values.Any(r => r.IsBusy))
        {
            owner.Error = "Wait for the current review operation to finish. Your draft is safe.";
            return;
        }
        await SaveWorkspaceAsync();
        runtime._applyingChanges = true;
        try
        {
            if (runtime._pendingLocal)
            {
                runtime._pendingLocal = false;
                await owner.RefreshAsync();
                if (owner.HasError)
                    runtime._pendingLocal = true;
            }
            if (runtime._pendingRemote is { } snapshot)
            {
                var graph = runtime.Session.Pending?.Graph;
                if (runtime.HistoryGraph is not null)
                    graph = await runtime.HistoryGraph.ObserveRemoteAsync(snapshot, owner.GitHubPreferences.BoundaryBranches ?? [], default);
                runtime.Session.Offer(snapshot, graph);
                runtime.Session.ApplyPending();
                owner.BuildDiscoveredGroups();
                if (runtime._activeGroup is not null)
                    await owner.CompareAsync();
                if (owner.HasError)
                    runtime._pendingRemote = snapshot;
            }
            owner.HasPendingChanges = runtime._pendingLocal || runtime._pendingRemote is not null;
        }
        finally { runtime._applyingChanges = false; }
    }

    public void CaptureWorkspace()
    {
        foreach (var group in owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups))
        {
            var key = GroupKey(group);
            if (!runtime._workspace.Stacks.TryGetValue(key, out var state))
                runtime._workspace.Stacks[key] = state = new();
            if (state.Expanded != group.IsExpanded)
            {
                state.Expanded = group.IsExpanded;
                state.Revision++;
            }
        }
        if (runtime._activeGroup is null)
            return;
        var activeKey = GroupKey(runtime._activeGroup);
        var active = runtime._workspace.Stacks[activeKey];
        runtime._workspace.ActiveGroup = activeKey;
        active.Revision++;
        if (runtime._displayedStateKey is not null)
        {
            // Stored comparison views are a cache: keep the recent ones and the one on screen.
            var current = runtime._displayedStateKey;
            foreach (var stale in active.Comparisons.Keys.Where(key => !string.Equals(key, current, StringComparison.Ordinal)).Take(Math.Max(0, active.Comparisons.Count - (active.Comparisons.ContainsKey(current) ? 40 : 39))).ToArray())
                active.Comparisons.Remove(stale);
            active.Comparisons[current] = owner.Sections.Select(s => new SectionViewState(s.Result.Layer, s.SelectedFile?.NewPath, s.Filter, s.ScrollX, s.ScrollY, s.IsExpanded)).ToArray();
        }
    }

    public async Task SaveWorkspaceAsync()
    {
        if (runtime._repository is null)
            return;
        CaptureWorkspace();
        var root = runtime._repository.WorkspaceKey;
        var copy = runtime._workspacePersistence.Snapshot(root, runtime._workspace);
        var previous = runtime._workspaceSave;
        runtime._workspaceSave = SaveAsync();
        await runtime._workspaceSave;
        async Task SaveAsync()
        {
            await previous;
            try
            {
                (await runtime._workspacePersistence.SaveAsync(root, copy)).OrThrow();
            }
            catch (Exception ex) { owner.Error = "Cannot save workspace: " + ex.Message; }
        }
    }

    public void RestoreSections()
    {
        if (runtime._activeGroup is null)
            return;
        var key = GroupKey(runtime._activeGroup);
        if (!runtime._workspace.Stacks.TryGetValue(key, out var state))
            runtime._workspace.Stacks[key] = state = new();
        runtime._displayedStateKey = ViewKey();
        state.Mode = owner.Mode;
        state.Revision++;
        state.Branches = runtime._activeGroup.Layers.Where(l => runtime._selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch).ToArray();
        if (state.Comparisons.TryGetValue(runtime._displayedStateKey, out var saved))
            foreach (var section in owner.Sections)
                if (saved.FirstOrDefault(s => s.Layer == section.Result.Layer) is { } view)
                {
                    section.Filter = view.Filter;
                    section.SelectedFile = section.Files.FirstOrDefault(f => f.NewPath == view.File) ?? section.Files.FirstOrDefault();
                    section.ScrollX = view.X;
                    section.ScrollY = view.Y;
                    section.IsExpanded = view.Expanded;
                }
    }

    public async Task SelectOverviewAsync(StackGroupViewModel group)
    {
        await SaveWorkspaceAsync();
        owner.SetActiveGroup(group);
        runtime._selectedPositions = [];
        runtime._suppress = true;
        owner.Mode = DiffMode.FullStack;
        runtime._suppress = false;
        await owner.CompareAsync();
    }

    public async Task<IReadOnlyList<string>> ShutdownAsync()
    {
        var problems = new List<string>();
        if (runtime._commands is not null && !await runtime._commands.StopAsync(TimeSpan.FromSeconds(5)))
            problems.Add("An operation is still stopping; its journal will be checked on restart.");
        try
        {
            await SaveWorkspaceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { problems.Add("Workspace: " + ex.Message); }
        if (runtime._workspacePersistence.LastProblem is { } workspaceError)
            problems.Add("Workspace: " + workspaceError.Message);
        foreach (var review in runtime._reviews.Values.ToArray())
        {
            try
            {
                await review.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) { problems.Add("Draft: " + ex.Message); }
        }
        try
        {
            await owner.SaveSettingsAsync();
        }
        catch (Exception ex) { problems.Add("Settings: " + ex.Message); }
        owner.Dispose();
        return problems;
    }

}
