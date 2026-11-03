using Stacker.Core.Framework;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

internal sealed class WorkspaceController(IWorkspaceActionsPresentation owner, WorkspaceRuntime runtime)
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
        if (runtime._repository is null || owner.IsDemo || !runtime._syncGate.Wait(0)) return;
        try
        {
            await owner.RefreshGitHubCoreAsync(ct, background: true);
            if ((runtime._pendingRemote is null || runtime._pendingRemote.Context.Identity == runtime._remoteSnapshot?.Context.Identity) && owner.ActiveReview is { IsBusy: false } review) await review.RefreshDiscussionAsync(ct);
        }
        finally { runtime._syncGate.Release(); }
    }

    public async Task RefreshAllAsync()
    {
        if (!runtime._syncGate.Wait(0)) return;
        try
        {
            runtime._coordinator?.Reset();
            await owner.RefreshAsync(); await owner.RefreshGitHubCoreAsync(default, background: false);
            if (owner.ActiveReview is { IsBusy: false } review) await review.ReloadAsync();
        }
        catch (Exception ex) { owner.Error = ex.Message; }
        finally { runtime._syncGate.Release(); SetWindowActive(runtime._windowActive); }
    }

    public async Task UpdateChangesAsync()
    {
        if (runtime._reviews.Values.Any(r => r.IsBusy)) { owner.Error = "Wait for the current review operation to finish. Your draft is safe."; return; }
        await SaveWorkspaceAsync();
        runtime._applyingChanges = true;
        try
        {
            if (runtime._pendingLocal) { runtime._pendingLocal = false; await owner.RefreshAsync(); if (owner.HasError) runtime._pendingLocal = true; }
            if (runtime._pendingRemote is { } snapshot)
            {
                var graph=runtime.Session.Pending?.Graph;
                if (runtime.HistoryGraph is not null) graph = await runtime.HistoryGraph.ObserveRemoteAsync(snapshot, owner.GitHubPreferences.BoundaryBranches ?? [], default);
                runtime.Session.Offer(snapshot,graph);runtime.Session.ApplyPending();
                owner.BuildDiscoveredGroups();
                if (runtime._activeGroup is not null) await owner.CompareAsync();
                if (owner.HasError) runtime._pendingRemote = snapshot;
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
            if (!runtime._workspace.Stacks.TryGetValue(key, out var state)) runtime._workspace.Stacks[key] = state = new();
            if (state.Expanded != group.IsExpanded) { state.Expanded = group.IsExpanded; state.Revision++; }
        }
        if (runtime._activeGroup is null) return;
        var activeKey = GroupKey(runtime._activeGroup);
        var active = runtime._workspace.Stacks[activeKey];
        runtime._workspace.ActiveGroup = activeKey; active.Revision++;
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
        if (runtime._repository is null) return;
        CaptureWorkspace();
        var root = runtime._repository.WorkspaceKey;
        var copy = runtime._workspacePersistence.Snapshot(root, runtime._workspace);
        var previous = runtime._workspaceSave;
        runtime._workspaceSave = SaveAsync();
        await runtime._workspaceSave;
        async Task SaveAsync()
        {
            await previous;
            try { (await runtime._workspacePersistence.SaveAsync(root, copy)).OrThrow(); }
            catch (Exception ex) { owner.Error = "Cannot save workspace: " + ex.Message; }
        }
    }

    public void RestoreSections()
    {
        if (runtime._activeGroup is null) return;
        var key = GroupKey(runtime._activeGroup);
        if (!runtime._workspace.Stacks.TryGetValue(key, out var state)) runtime._workspace.Stacks[key] = state = new();
        runtime._displayedStateKey = ViewKey(); state.Mode = owner.Mode; state.Revision++;
        state.Branches = runtime._activeGroup.Layers.Where(l => runtime._selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch).ToArray();
        if (state.Comparisons.TryGetValue(runtime._displayedStateKey, out var saved))
            foreach (var section in owner.Sections)
                if (saved.FirstOrDefault(s => s.Layer == section.Result.Layer) is { } view)
                {
                    section.Filter = view.Filter;
                    section.SelectedFile = section.Files.FirstOrDefault(f => f.NewPath == view.File) ?? section.Files.FirstOrDefault();
                    section.ScrollX = view.X; section.ScrollY = view.Y; section.IsExpanded = view.Expanded;
                }
    }

    public async Task SelectOverviewAsync(StackGroupViewModel group)
    {
        await SaveWorkspaceAsync(); owner.SetActiveGroup(group); runtime._selectedPositions = [];
        runtime._suppress = true; owner.Mode = DiffMode.FullStack; runtime._suppress = false; await owner.CompareAsync();
    }

    public async Task<ReviewViewModel?> EnsureReviewAsync(DiffSectionViewModel section, CancellationToken ct = default)
    {
        if (section.AssociatedPr is not { } pr || section.GitHubContext is not { } context || runtime._github is null || runtime._writer is null) return null;
        await runtime._reviewGate.WaitAsync(ct);
        try
        {
            var key = context.Identity + "/" + pr.Number;
            if (!runtime._reviews.TryGetValue(key, out var session))
            {
                if (runtime._reviews.Count >= 12)
                {
                    var staleKey = runtime._reviewOrder.FirstOrDefault(candidate => !runtime._reviews[candidate].IsBusy
                        && !ReferenceEquals(runtime._reviews[candidate], owner.ActiveReview));
                    if (staleKey is null) throw new StackerException("Review sessions are busy. Finish the current operation before opening another.");
                    var stale = runtime._reviews[staleKey];
                    await stale.FlushAsync();
                    stale.PropertyChanged -= ReviewChanged; stale.Dispose();
                    runtime._reviews.Remove(staleKey); runtime._reviewOrder.Remove(staleKey);
                }
                session = new(runtime._github, runtime._writer, runtime._applicationStore, runtime._git, context, pr, section, owner.IsDemo) { HistoryObjects = runtime.HistoryObjects };
                try
                {
                    await session.InitializeAsync(ct: ct); ct.ThrowIfCancellationRequested();
                    if (runtime._disposed) throw new OperationCanceledException("The workspace was closed.");
                }
                catch { session.Dispose(); throw; }
                runtime._reviews[key] = session;
                session.PropertyChanged += ReviewChanged;
            }
            runtime._reviewOrder.Remove(key); runtime._reviewOrder.AddLast(key);
            session.Attach(section); return session;
        }
        finally { runtime._reviewGate.Release(); }
    }

    private void ReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (!runtime._disposed && e.PropertyName == nameof(ReviewViewModel.ReviewLabel)) owner.Notify(nameof(owner.ReviewLabel)); }

    public async Task ClearReviewsAsync()
    {
        await runtime._reviewGate.WaitAsync();
        try
        {
            foreach (var session in runtime._reviews.Values) await session.FlushAsync();
            DisposeReviews();
        }
        finally { runtime._reviewGate.Release(); }
    }
    public void DisposeReviews()
    {
        foreach (var session in runtime._reviews.Values) { session.PropertyChanged -= ReviewChanged; session.Dispose(); }
        runtime._reviews.Clear(); runtime._reviewOrder.Clear();
    }

    public async Task ShowReviewAsync(DiffSectionViewModel? section = null, int tab = 3)
    {
        section ??= owner.Sections.FirstOrDefault(s => s.IsPrSnapshot) ?? owner.CreateDiscussionContext();
        if (section is null) return;
        try { owner.ActiveReview = await EnsureReviewAsync(section); }
        catch (Exception ex) { owner.Error = ex.Message; return; }
        if (owner.ActiveReview is not null) { owner.ActiveReview.PanelTab = tab; if (tab == 0) owner.ActiveReview.BeginPrComment(); owner.IsReviewOpen = true; }
    }

    public void CloseReview() => owner.IsReviewOpen = false;

    public async Task FlushReviewsAsync() { foreach (var review in runtime._reviews.Values) await review.FlushAsync(); }

    public async Task<IReadOnlyList<string>> ShutdownAsync()
    {
        var problems = new List<string>();
        if (runtime._commands is not null && !await runtime._commands.StopAsync(TimeSpan.FromSeconds(5)))
            problems.Add("An operation is still stopping; its journal will be checked on restart.");
        try { await SaveWorkspaceAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception ex) { problems.Add("Workspace: " + ex.Message); }
        if (runtime._workspacePersistence.LastProblem is { } workspaceError) problems.Add("Workspace: " + workspaceError.Message);
        foreach (var review in runtime._reviews.Values.ToArray())
        {
            try { await review.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { problems.Add("Draft: " + ex.Message); }
        }
        try { await owner.SaveSettingsAsync(); } catch (Exception ex) { problems.Add("Settings: " + ex.Message); }
        owner.Dispose();
        return problems;
    }

}
