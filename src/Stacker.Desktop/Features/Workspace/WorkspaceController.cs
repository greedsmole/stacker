using Stacker.Core.Framework;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

internal sealed class WorkspaceController(MainViewModel owner)
{
    public string GroupKey(StackGroupViewModel group) => (group.IsRemote ? "github:" + owner._remoteSnapshot?.Context.Identity : "local") + ":" + group.Definition.Id;

    public string ViewKey() => owner.Mode + ":" + string.Join("|", owner._activeGroup?.Layers.Where(l => owner._selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch) ?? []);

    public void SetWindowActive(bool active)
    {
        owner._windowActive = active;
        owner._coordinator ??= new(PollAsync);
        owner._coordinator.SetActive(active && owner.Settings.BackgroundRefresh && !owner.IsDemo && owner._repository is not null);
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        if (owner._repository is null || owner.IsDemo || !owner._syncGate.Wait(0)) return;
        try
        {
            await owner.RefreshGitHubCoreAsync(ct, background: true);
            if ((owner._pendingRemote is null || owner._pendingRemote.Context.Identity == owner._remoteSnapshot?.Context.Identity) && owner.ActiveReview is { IsBusy: false } review) await review.RefreshDiscussionAsync(ct);
        }
        finally { owner._syncGate.Release(); }
    }

    public async Task RefreshAllAsync()
    {
        if (!owner._syncGate.Wait(0)) return;
        try
        {
            owner._coordinator?.Reset();
            await owner.RefreshAsync(); await owner.RefreshGitHubCoreAsync(default, background: false);
            if (owner.ActiveReview is { IsBusy: false } review) await review.ReloadAsync();
        }
        catch (Exception ex) { owner.Error = ex.Message; }
        finally { owner._syncGate.Release(); SetWindowActive(owner._windowActive); }
    }

    public async Task UpdateChangesAsync()
    {
        if (owner._reviews.Values.Any(r => r.IsBusy)) { owner.Error = "Wait for the current review operation to finish. Your draft is safe."; return; }
        await SaveWorkspaceAsync();
        owner._applyingChanges = true;
        try
        {
            if (owner._pendingLocal) { owner._pendingLocal = false; await owner.RefreshAsync(); if (owner.HasError) owner._pendingLocal = true; }
            if (owner._pendingRemote is { } snapshot)
            {
                owner._pendingRemote = null; owner._remoteSnapshot = snapshot;
                if (owner.HistoryGraph is not null) owner._historyRemoteGraph = await owner.HistoryGraph.ObserveRemoteAsync(snapshot, owner.GitHubPreferences.BoundaryBranches ?? [], default);
                owner.BuildDiscoveredGroups();
                if (owner._activeGroup is not null) await owner.CompareAsync();
                if (owner.HasError) owner._pendingRemote = snapshot;
            }
            owner.HasPendingChanges = owner._pendingLocal || owner._pendingRemote is not null;
        }
        finally { owner._applyingChanges = false; }
    }

    public void CaptureWorkspace()
    {
        foreach (var group in owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups))
        {
            var key = GroupKey(group);
            if (!owner._workspace.Stacks.TryGetValue(key, out var state)) owner._workspace.Stacks[key] = state = new();
            if (state.Expanded != group.IsExpanded) { state.Expanded = group.IsExpanded; state.Revision++; }
        }
        if (owner._activeGroup is null) return;
        var activeKey = GroupKey(owner._activeGroup);
        var active = owner._workspace.Stacks[activeKey];
        owner._workspace.ActiveGroup = activeKey; active.Revision++;
        if (owner._displayedStateKey is not null)
        {
            // Stored comparison views are a cache: keep the recent ones and the one on screen.
            var current = owner._displayedStateKey;
            foreach (var stale in active.Comparisons.Keys.Where(key => !string.Equals(key, current, StringComparison.Ordinal)).Take(Math.Max(0, active.Comparisons.Count - (active.Comparisons.ContainsKey(current) ? 40 : 39))).ToArray())
                active.Comparisons.Remove(stale);
            active.Comparisons[current] = owner.Sections.Select(s => new SectionViewState(s.Result.Layer, s.SelectedFile?.NewPath, s.Filter, s.ScrollX, s.ScrollY, s.IsExpanded)).ToArray();
        }
    }

    public async Task SaveWorkspaceAsync()
    {
        if (owner._repository is null) return;
        CaptureWorkspace();
        var root = owner._repository.Root;
        var copy = owner._workspacePersistence.Snapshot(root, owner._workspace);
        var previous = owner._workspaceSave;
        owner._workspaceSave = SaveAsync();
        await owner._workspaceSave;
        async Task SaveAsync()
        {
            await previous;
            try { (await owner._workspacePersistence.SaveAsync(root, copy)).OrThrow(); }
            catch (Exception ex) { owner.Error = "Cannot save workspace: " + ex.Message; }
        }
    }

    public void RestoreSections()
    {
        if (owner._activeGroup is null) return;
        var key = GroupKey(owner._activeGroup);
        if (!owner._workspace.Stacks.TryGetValue(key, out var state)) owner._workspace.Stacks[key] = state = new();
        owner._displayedStateKey = ViewKey(); state.Mode = owner.Mode; state.Revision++;
        state.Branches = owner._activeGroup.Layers.Where(l => owner._selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch).ToArray();
        if (state.Comparisons.TryGetValue(owner._displayedStateKey, out var saved))
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
        await SaveWorkspaceAsync(); owner.SetActiveGroup(group); owner._selectedPositions = [];
        owner._suppress = true; owner.Mode = DiffMode.FullStack; owner._suppress = false; await owner.CompareAsync();
    }

    public async Task<ReviewViewModel?> EnsureReviewAsync(DiffSectionViewModel section, CancellationToken ct = default)
    {
        if (section.AssociatedPr is not { } pr || section.GitHubContext is not { } context || owner._github is null || owner._writer is null) return null;
        await owner._reviewGate.WaitAsync(ct);
        try
        {
            var key = context.Identity + "/" + pr.Number;
            if (!owner._reviews.TryGetValue(key, out var session))
            {
                if (owner._reviews.Count >= 12)
                {
                    var staleKey = owner._reviewOrder.FirstOrDefault(candidate => !owner._reviews[candidate].IsBusy
                        && !ReferenceEquals(owner._reviews[candidate], owner.ActiveReview));
                    if (staleKey is null) throw new StackerException("Review sessions are busy. Finish the current operation before opening another.");
                    var stale = owner._reviews[staleKey];
                    await stale.FlushAsync();
                    stale.PropertyChanged -= ReviewChanged; stale.Dispose();
                    owner._reviews.Remove(staleKey); owner._reviewOrder.Remove(staleKey);
                }
                session = new(owner._github, owner._writer, owner._applicationStore, owner._git, context, pr, section, owner.IsDemo) { HistoryObjects = owner.HistoryObjects };
                try
                {
                    await session.InitializeAsync(ct: ct); ct.ThrowIfCancellationRequested();
                    if (owner._disposed) throw new OperationCanceledException("The workspace was closed.");
                }
                catch { session.Dispose(); throw; }
                owner._reviews[key] = session;
                session.PropertyChanged += ReviewChanged;
            }
            owner._reviewOrder.Remove(key); owner._reviewOrder.AddLast(key);
            session.Attach(section); return session;
        }
        finally { owner._reviewGate.Release(); }
    }

    private void ReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (!owner._disposed && e.PropertyName == nameof(ReviewViewModel.ReviewLabel)) owner.Notify(nameof(owner.ReviewLabel)); }

    public async Task ClearReviewsAsync()
    {
        await owner._reviewGate.WaitAsync();
        try
        {
            foreach (var session in owner._reviews.Values) await session.FlushAsync();
            DisposeReviews();
        }
        finally { owner._reviewGate.Release(); }
    }
    public void DisposeReviews()
    {
        foreach (var session in owner._reviews.Values) { session.PropertyChanged -= ReviewChanged; session.Dispose(); }
        owner._reviews.Clear(); owner._reviewOrder.Clear();
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

    public async Task FlushReviewsAsync() { foreach (var review in owner._reviews.Values) await review.FlushAsync(); }

    public async Task<IReadOnlyList<string>> ShutdownAsync()
    {
        var problems = new List<string>();
        if (owner._commands is not null && !await owner._commands.StopAsync(TimeSpan.FromSeconds(5)))
            problems.Add("An operation is still stopping; its journal will be checked on restart.");
        try { await SaveWorkspaceAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception ex) { problems.Add("Workspace: " + ex.Message); }
        if (owner._workspacePersistence.LastProblem is { } workspaceError) problems.Add("Workspace: " + workspaceError.Message);
        foreach (var review in owner._reviews.Values.ToArray())
        {
            try { await review.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { problems.Add("Draft: " + ex.Message); }
        }
        try { await owner.SaveSettingsAsync(); } catch (Exception ex) { problems.Add("Settings: " + ex.Message); }
        owner.Dispose();
        return problems;
    }

}
