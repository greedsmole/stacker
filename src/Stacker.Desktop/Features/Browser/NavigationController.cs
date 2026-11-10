using Stacker.Core.Framework;
using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

internal sealed class NavigationController(INavigationPresentation owner, WorkspaceRuntime runtime)
{
    public async Task InitializeAsync()
    {
        try
        {
            owner.Settings = (await runtime._settingsStore.LoadAsync()).OrThrow(); runtime._executable.Override = owner.Settings.GitPath;
            if (runtime._ghExecutable is not null) { runtime._ghExecutable.Override = owner.Settings.GhPath; runtime._ghExecutable.UseSavedCredentials = owner.Settings.UseSavedGhCredentials; }
            foreach (var path in owner.Settings.RecentRepositories.Take(10)) owner.RecentRepositories.Add(path);
            if (runtime._settingsStore is ISettingsDiagnostics { LastProblem: { } settingsProblem }) owner.Error = settingsProblem;
            if (runtime._applicationStore.LastProblem is { } stateProblem) owner.Error = stateProblem;
        }
        catch (Exception ex) { owner.Error = "Cannot load settings: " + ex.Message; }
    }

    public Task OpenAsync() => OpenRepositoryAsync(owner.RepositoryPath);

    public Task RefreshAsync() => ReadRepositoryAsync(runtime._repository?.Root ?? owner.RepositoryPath, false);

    public Task OpenRepositoryAsync(string path) => ReadRepositoryAsync(path, true);

    public async Task ReadRepositoryAsync(string path, bool opening)
    {
        if (string.IsNullOrWhiteSpace(path) || runtime._disposed) return;
        if (opening && (owner.IsSyncing || runtime._commands?.IsBusy == true || runtime._reviews.Values.Any(review => review.IsBusy)))
        { owner.Error = "Wait for the current operation before switching repositories."; return; }
        runtime._refresh?.Cancel(); var refresh = new CancellationTokenSource(); runtime._refresh = refresh;
        owner.IsBusy = true; owner.Error = "";
        try
        {
            if (opening) await owner.SaveWorkspaceAsync();
            var repo = (await runtime._git.OpenAsync(path, refresh.Token)).OrThrow();
            var document = (await runtime._store.LoadAsync(repo.Root, refresh.Token)).OrThrow();
            if (runtime.HistoryGraph is not null) await runtime.HistoryGraph.ObserveLocalAsync(repo, document.Stacks, refresh.Token);
            var sameRepo = runtime._repository?.Root == repo.Root;
            var workspace = runtime._workspace;
            string? stateNotice = null;
            if (!sameRepo)
            {
                var loaded = await runtime._workspacePersistence.LoadAsync(repo.WorkspaceKey, refresh.Token, repo.Root);
                if (loaded.IsFailed && loaded.Problem!.Code == ProblemCodes.StoreCorrupt)
                { workspace = new(); stateNotice = loaded.Problem.Message; }
                else workspace = loaded.OrThrow() ?? new();
            }
            if (!sameRepo)
            {
                runtime._comparison?.Cancel(); runtime._githubLoad?.Cancel();
                await owner.ClearReviewSessionsAsync();
            }
            refresh.Token.ThrowIfCancellationRequested();
            var unchanged = sameRepo && runtime._document?.Revision == document.Revision && runtime._repository!.Refs.SequenceEqual(repo.Refs);
            if (opening) { runtime._pendingLocal = false; runtime._pendingRemote = null; owner.HasPendingChanges = false; }
            if (sameRepo && !opening && !unchanged && owner.Sections.Count > 0 && !runtime._applyingChanges)
            { runtime._pendingLocal = true; owner.HasPendingChanges = true; owner.Status = "New local changes available"; return; }
            var groups = new List<StackGroupViewModel>();
            if (!unchanged)
                foreach (var stack in document.Stacks) groups.Add(new(stack, await runtime._stacks.SnapshotAsync(repo, stack, refresh.Token)) { IsExpanded = !sameRepo || owner.LocalGroups.FirstOrDefault(g => g.Definition.Id == stack.Id)?.IsExpanded != false });
            refresh.Token.ThrowIfCancellationRequested();
            var previousGroup = runtime._activeGroup; var selectedBranches = previousGroup?.Layers.Where(l => runtime._selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch).ToHashSet() ?? []; var mode = owner.Mode;
            if (!sameRepo) { runtime._comparison?.Cancel(); runtime._githubLoad?.Cancel(); DisposeViews(); owner.Sections.Clear(); owner.RemoteGroups.Clear(); owner.OtherGroups.Clear(); runtime._remoteSnapshot = null; runtime._historyRemoteGraph = null; runtime._activeGroup = null; owner.GitHubStatus = "Not connected"; }
            runtime._repository = repo; runtime._document = document; owner.RepositoryPath = repo.Root;
            if (!sameRepo) { runtime._workspace = workspace; runtime._displayedStateKey = null; runtime._pendingRemote = null; runtime._pendingLocal = false; owner.HasPendingChanges = false; owner.ActiveReview = null; owner.IsReviewOpen = false; }
            owner.Settings.LastRepository = repo.Root;
            owner.IsDemo = repo.IsDemo;
            owner.RepositoryTitle = Path.GetFileName(repo.Root) + (owner.IsDemo ? " · Demo / offline" : "");
            owner.Status = $"Working tree: {repo.ChangedFiles} changed entries · Updated {DateTime.Now:HH:mm:ss}"; owner.CanEdit = true;
            if (!unchanged)
            {
                owner.Stacks.Clear(); owner.LocalGroups.Clear();
                foreach (var group in groups)
                {
                    for (var i = 0; i < group.Layers.Count; i++)
                        if (owner.FindLocalPr(group.Layers[i].Snapshot.Branch) is { } pr) group.Layers[i] = new(group.Layers[i].Snapshot, pr) { Group = group };
                    owner.Stacks.Add(group.Definition); owner.LocalGroups.Add(group);
                }
                await owner.RefreshSyncAvailabilityAsync(refresh.Token);
                var selected = sameRepo && previousGroup?.IsRemote == true ? previousGroup : groups.FirstOrDefault(g => owner.GroupKey(g) == runtime._workspace.ActiveGroup) ?? groups.FirstOrDefault(g => g.Definition.Id == previousGroup?.Definition.Id) ?? groups.FirstOrDefault();
                if (selected is not null)
                {
                    SetActiveGroup(selected);
                    runtime._suppress = true; owner.Mode = sameRepo && selected.Definition.Id == previousGroup?.Definition.Id ? mode : DiffMode.FullStack;
                    runtime._selectedPositions = sameRepo ? selected.Layers.Where(l => selectedBranches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray() : [];
                    if (!sameRepo && runtime._workspace.Stacks.TryGetValue(owner.GroupKey(selected), out var saved))
                    { owner.Mode = saved.Mode; runtime._selectedPositions = selected.Layers.Where(l => saved.Branches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray(); }
                    if (owner.Mode != DiffMode.FullStack && runtime._selectedPositions.Length == 0) { owner.Mode = DiffMode.FullStack; owner.Error = "The selected layer disappeared. Showing the entire stack."; }
                    runtime._suppress = false; await CompareAsync();
                }
                else { runtime._activeGroup = null; runtime._suppress = true; owner.SelectedStack = null; runtime._suppress = false; owner.Layers.Clear(); owner.Sections.Clear(); owner.EmptyMessage = "Create a stack, or connect GitHub to discover PRs."; }
            }
            // The working tree can change without any ref changing, so its state is re-read on every load.
            await owner.RefreshWorkingTreeAsync(refresh.Token);
            if (opening)
            {
                owner.Settings.RecentRepositories.Remove(repo.Root); owner.Settings.RecentRepositories.Insert(0, repo.Root); owner.Settings.RecentRepositories = owner.Settings.RecentRepositories.Take(10).ToList();
                owner.RecentRepositories.Clear(); foreach (var recent in owner.Settings.RecentRepositories) owner.RecentRepositories.Add(recent);
                await SaveSettingsAsync();
                if (opening)
                {
                    var restore = runtime._workspace.ActiveGroup;
                    await owner.RefreshGitHubAsync();
                    foreach (var group in owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups))
                        if (runtime._workspace.Stacks.TryGetValue(owner.GroupKey(group), out var saved)) group.IsExpanded = saved.Expanded;
                    var target = owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups).FirstOrDefault(g => owner.GroupKey(g) == restore);
                    if (target is not null) await SelectGroupAsync(target, saveCurrent: false);
                }
                owner.SetWindowActive(runtime._windowActive);
            }
            if (stateNotice is not null) owner.Error = stateNotice;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!refresh.IsCancellationRequested) owner.Error = ex.Message + (runtime._repository is null ? "" : " Previous data retained."); }
        finally { if (ReferenceEquals(runtime._refresh, refresh)) { runtime._refresh = null; owner.IsBusy = false; } refresh.Dispose(); }
    }

    public void SetActiveGroup(StackGroupViewModel group)
    {
        runtime.Session.Select(new(group.Definition.Id, null, null));
        runtime._comparison?.Cancel();
        foreach (var g in owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups)) { g.IsSelected = ReferenceEquals(g, group); foreach (var l in g.Layers) { l.IsSelected = false; l.IsChecked = false; } }
        runtime._activeGroup = group; runtime._suppress = true; owner.SelectedStack = group.Definition; runtime._suppress = false;
        owner.Layers.Clear(); foreach (var layer in group.Layers) owner.Layers.Add(layer);
        owner.Notify(nameof(owner.BaseLabel)); owner.Notify(nameof(owner.IsLocalSelected)); owner.Notify(nameof(owner.CanRestackActiveStack));
    }

    public async Task SelectGroupAsync(StackGroupViewModel group, bool saveCurrent = true)
    {
        if (saveCurrent) await owner.SaveWorkspaceAsync();
        if (group.IsSinglePr) { SetActiveGroup(group); runtime._selectedPositions = [0]; runtime._suppress = true; owner.Mode = DiffMode.Layer; runtime._suppress = false; await CompareAsync(); return; }
        SetActiveGroup(group); runtime._selectedPositions = []; runtime._suppress = true; owner.Mode = DiffMode.FullStack;
        if (runtime._workspace.Stacks.TryGetValue(owner.GroupKey(group), out var saved))
        {
            runtime._selectedPositions = group.Layers.Where(l => saved.Branches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray();
            if (saved.Branches.Length == runtime._selectedPositions.Length) owner.Mode = saved.Mode;
            else { runtime._selectedPositions = []; owner.EmptyMessage = "The saved layer is no longer available. Showing Overview."; }
        }
        runtime._suppress = false; await CompareAsync();
    }

    public async Task SelectLayerAsync(LayerViewModel layer, bool toggle)
    {
        await owner.SaveWorkspaceAsync();
        var group = layer.Group!;
        if (!ReferenceEquals(runtime._activeGroup, group)) { SetActiveGroup(group); runtime._selectedPositions = []; }
        runtime._selectedPositions = toggle ? (runtime._selectedPositions.Contains(layer.Snapshot.Position) ? runtime._selectedPositions.Where(p => p != layer.Snapshot.Position) : runtime._selectedPositions.Append(layer.Snapshot.Position)).Order().ToArray() : [layer.Snapshot.Position];
        runtime._suppress = true; owner.Mode = runtime._selectedPositions.Length == 0 ? DiffMode.FullStack : runtime._selectedPositions.Length == 1 ? DiffMode.Layer : DiffMode.MultiLayer; runtime._suppress = false;
        await CompareAsync();
    }

    public void SelectLayers(IEnumerable<LayerViewModel> layers)
    {
        runtime._selectedPositions = layers.Select(l => l.Snapshot.Position).Order().ToArray();
        runtime._suppress = true; owner.Mode = runtime._selectedPositions.Length > 1 ? DiffMode.MultiLayer : DiffMode.Layer; runtime._suppress = false; _ = CompareAsync();
    }

    public async Task CompareAsync()
    {
        owner.ResetSearch();
        runtime._comparison?.Cancel(); var compare = new CancellationTokenSource(); runtime._comparison = compare;
        var group = runtime._activeGroup; var repo = runtime._repository; var mode = owner.Mode; var positions = runtime._selectedPositions.ToArray();
        if (repo is null || group is null || runtime._disposed) { compare.Dispose(); if (ReferenceEquals(runtime._comparison, compare)) runtime._comparison = null; return; }
        runtime.Session.Select(new(group.Definition.Id, mode + ":" + string.Join(",", positions), null));
        var ticket = runtime.Session.Ticket;
        owner.Notify(nameof(owner.HasPrDiscussion)); owner.Notify(nameof(owner.CanReview));
        owner.IsBusy = true; owner.Error = "";
        try
        {
            var comparisonRepository = repo; var stack = group.Definition;
            if (group.IsRemote)
            {
                if (runtime._remoteSnapshot is null) throw new StackerException("Refresh to load this stack.");
                comparisonRepository = await owner.PrepareRemoteAsync(group, mode, positions, compare.Token);
                stack = new(group.Definition.Id, group.Name, RemoteRefs.BaseRef(group.PullRequests[0]), group.PullRequests.Select(RemoteRefs.HeadRef).ToArray());
                // For a single PR's review use its declared server base, even if the discovered parent has moved.
                if (mode == DiffMode.Layer && positions.Length == 1)
                {
                    var pr = group.PullRequests[positions[0]];
                    stack = new(stack.Id, stack.Name, RemoteRefs.BaseRef(pr), [RemoteRefs.HeadRef(pr)]); positions = [0];
                }
            }
            var key = WorkspaceRuntime.ComparisonKey(comparisonRepository, stack, mode, positions) + "|" + owner.Settings.Theme + (group.IsRemote ? "|" + runtime._remoteSnapshot?.Context.Identity : "");
            if (!runtime._views.TryGetValue(key, out var sections))
            {
                var results = await runtime._diff.CompareAsync(new(comparisonRepository, stack, mode, positions), compare.Token);
                compare.Token.ThrowIfCancellationRequested();
                sections = results.Select(result =>
                {
                    var pr = group.IsRemote ? group.PullRequests.FirstOrDefault(p => RemoteRefs.HeadRef(p) == result.Layer) : owner.FindLocalPr(result.Layer);
                    return new DiffSectionViewModel(runtime._git, comparisonRepository.Root, result, runtime._highlighter, owner.Settings.Theme)
                    { AssociatedPr = pr, GitHubContext = runtime._remoteSnapshot?.Context, IsPrSnapshot = group.IsRemote && mode == DiffMode.Layer, IsDemo = owner.IsDemo, WrapCode = owner.Settings.WrapCode };
                }).ToList();
                if (group.IsRemote && runtime._prObjects is not null && !owner.IsDemo)
                    foreach (var section in sections) section.ContentLease = (await runtime._prObjects.AcquireAsync(repo.Identity!, compare.Token)).OrThrow();
                // Carry file/filter/scroll state across a changed snapshot of the same comparison.
                foreach (var section in sections)
                {
                    var old = owner.Sections.FirstOrDefault(s => s.Result.Layer == section.Result.Layer);
                    if (old is not null) section.RestoreView(old);
                }
                runtime._views[key] = sections;
                while (runtime._views.Count > 24) { var victim = runtime._views.First(kv => kv.Key != key && !kv.Value.Any(owner.Sections.Contains)); foreach (var s in victim.Value) s.Dispose(); runtime._views.Remove(victim.Key); }
            }
            compare.Token.ThrowIfCancellationRequested();
            if (!runtime.Session.IsCurrent(ticket)) return;
            if (!owner.Sections.SequenceEqual(sections)) { owner.Sections.Clear(); foreach (var section in sections) owner.Sections.Add(section); }
            foreach (var layer in group.Layers) { layer.IsChecked = runtime._selectedPositions.Contains(layer.Snapshot.Position); layer.IsSelected = layer.IsChecked; }
            owner.ComparisonTitle = mode switch
            {
                DiffMode.FullStack => $"Entire stack · {group.Name}",
                DiffMode.Cumulative => $"Through layer {(runtime._selectedPositions.FirstOrDefault() + 1)} · {group.Name}",
                DiffMode.MultiLayer => $"Selected layers · {string.Join(", ", runtime._selectedPositions.Select(p => p + 1))}",
                _ => group.IsSinglePr ? $"Pull request · {group.PullRequests[0].Label}" : $"Layer {(runtime._selectedPositions.FirstOrDefault() + 1)} · {group.Layers.ElementAtOrDefault(runtime._selectedPositions.FirstOrDefault())?.Title}"
            };
            owner.Notify(nameof(owner.ComparisonDetail));
            owner.RestoreSections();
            foreach (var section in sections) section.ShowSectionHeader = sections.Count > 1;
            if (mode == DiffMode.Layer) foreach (var section in sections) await owner.CaptureHistorySectionAsync(section, compare.Token);
            var prSection = sections.FirstOrDefault(s => s.IsPrSnapshot);
            if (prSection is not null) { var review = await owner.EnsureReviewAsync(prSection, compare.Token); compare.Token.ThrowIfCancellationRequested(); owner.ActiveReview = review; }
            else { owner.ActiveReview = null; owner.IsReviewOpen = false; }
            compare.Token.ThrowIfCancellationRequested();
            owner.Notify(nameof(owner.HasSelectedLayer));
            owner.EmptyMessage = sections.Count == 0 ? "This stack has no layers. Edit it to add branches." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!compare.IsCancellationRequested) owner.Error = ex.Message; }
        finally { if (ReferenceEquals(runtime._comparison, compare)) { runtime._comparison = null; owner.IsBusy = false; } compare.Dispose(); }
    }

    public async Task<bool> SaveStackAsync(StackDefinition stack)
    {
        if (runtime._repository is null || runtime._document is null) return false;
        var updated = runtime._document.Stacks.Any(s => s.Id == stack.Id) ? runtime._document.Stacks.Select(s => s.Id == stack.Id ? stack : s).ToArray() : runtime._document.Stacks.Append(stack).ToArray();
        return await SaveStacksAsync(updated);
    }

    public async Task DeleteSelectedStackAsync()
    {
        if (runtime._document is not null && owner.SelectedStack is not null && runtime._activeGroup?.IsRemote != true) await SaveStacksAsync(runtime._document.Stacks.Where(s => s.Id != owner.SelectedStack.Id).ToArray());
    }

    public async Task<bool> SaveStacksAsync(IReadOnlyList<StackDefinition> stacks)
    {
        if (runtime._repository is null || runtime._document is null) return false;
        try
        {
            (await runtime._store.SaveAsync(runtime._repository.Root, stacks, runtime._document.Revision)).OrThrow();
            // The refresh reports its own problems; the save has already succeeded by here.
            runtime._applyingChanges = true;
            try { await RefreshAsync(); } finally { runtime._applyingChanges = false; }
            return true;
        }
        catch (Exception ex) { owner.Error = ex.Message; return false; }
    }

    public async Task SaveSettingsAsync()
    {
        try { runtime._executable.Override = owner.Settings.GitPath; if (runtime._commandLog is not null) runtime._commandLog.GitExecutablePath = runtime._executable.Resolve(); if (runtime._ghExecutable is not null) { runtime._ghExecutable.Override = owner.Settings.GhPath; runtime._ghExecutable.UseSavedCredentials = owner.Settings.UseSavedGhCredentials; } (await runtime._settingsStore.SaveAsync(owner.Settings)).OrThrow(); }
        catch (Exception ex) { owner.Error = "Cannot save settings: " + ex.Message; throw; }
    }

    public async Task SetWrapCodeAsync(bool enabled)
    {
        owner.Settings.WrapCode = enabled;
        foreach (var section in runtime._views.Values.SelectMany(views => views).Distinct()) section.WrapCode = enabled;
        await SaveSettingsAsync();
    }

    public void DisposeViews() { foreach (var section in runtime._views.Values.SelectMany(v => v).Distinct()) section.Dispose(); runtime._views.Clear(); }

    public void Dispose() { if (runtime._disposed) return; runtime._disposed = true; if (runtime._commandLog is not null) runtime._commandLog.Changed -= owner.LogChanged; if (runtime._commands is not null) { runtime._commands.Reported -= owner.ReportCommand; runtime._commands.Starting -= owner.CommandStarting; } runtime._searchLoad?.Cancel(); runtime._refresh?.Cancel(); runtime._comparison?.Cancel(); runtime._githubLoad?.Cancel(); runtime._coordinator?.Dispose(); owner.DisposeReviewSessions(); DisposeViews(); }

}
