using Stacker.Core.Framework;
using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

internal sealed class NavigationController(MainViewModel owner)
{
    public async Task InitializeAsync()
    {
        try
        {
            owner.Settings = (await owner._settingsStore.LoadAsync()).OrThrow(); owner._executable.Override = owner.Settings.GitPath;
            if (owner._ghExecutable is not null) { owner._ghExecutable.Override = owner.Settings.GhPath; owner._ghExecutable.UseSavedCredentials = owner.Settings.UseSavedGhCredentials; }
            foreach (var path in owner.Settings.RecentRepositories.Take(10)) owner.RecentRepositories.Add(path);
            if (owner._settingsStore is ISettingsDiagnostics { LastProblem: { } settingsProblem }) owner.Error = settingsProblem;
            if (owner._applicationStore.LastProblem is { } stateProblem) owner.Error = stateProblem;
        }
        catch (Exception ex) { owner.Error = "Cannot load settings: " + ex.Message; }
    }

    public Task OpenAsync() => OpenRepositoryAsync(owner.RepositoryPath);

    public Task RefreshAsync() => ReadRepositoryAsync(owner._repository?.Root ?? owner.RepositoryPath, false);

    public Task OpenRepositoryAsync(string path) => ReadRepositoryAsync(path, true);

    public async Task ReadRepositoryAsync(string path, bool opening)
    {
        if (string.IsNullOrWhiteSpace(path) || owner._disposed) return;
        if (opening && (owner.IsSyncing || owner._commands?.IsBusy == true || owner._reviews.Values.Any(review => review.IsBusy)))
        { owner.Error = "Wait for the current operation before switching repositories."; return; }
        owner._refresh?.Cancel(); var refresh = new CancellationTokenSource(); owner._refresh = refresh;
        owner.IsBusy = true; owner.Error = "";
        try
        {
            if (opening) await owner.SaveWorkspaceAsync();
            var repo = (await owner._git.OpenAsync(path, refresh.Token)).OrThrow();
            var document = (await owner._store.LoadAsync(repo.Root, refresh.Token)).OrThrow();
            if (owner.HistoryGraph is not null) await owner.HistoryGraph.ObserveLocalAsync(repo, document.Stacks, refresh.Token);
            var sameRepo = owner._repository?.Root == repo.Root;
            var workspace = owner._workspace;
            string? stateNotice = null;
            if (!sameRepo)
            {
                var loaded = await owner._workspacePersistence.LoadAsync(repo.Root, refresh.Token);
                if (loaded.IsFailed && loaded.Problem!.Code == ProblemCodes.StoreCorrupt)
                { workspace = new(); stateNotice = loaded.Problem.Message; }
                else workspace = loaded.OrThrow() ?? new();
            }
            if (!sameRepo)
            {
                owner._comparison?.Cancel(); owner._githubLoad?.Cancel();
                await owner.ClearReviewSessionsAsync();
            }
            refresh.Token.ThrowIfCancellationRequested();
            var unchanged = sameRepo && owner._document?.Revision == document.Revision && owner._repository!.Refs.SequenceEqual(repo.Refs);
            if (opening) { owner._pendingLocal = false; owner._pendingRemote = null; owner.HasPendingChanges = false; }
            if (sameRepo && !opening && !unchanged && owner.Sections.Count > 0 && !owner._applyingChanges)
            { owner._pendingLocal = true; owner.HasPendingChanges = true; owner.Status = "New local changes available"; return; }
            var groups = new List<StackGroupViewModel>();
            if (!unchanged)
                foreach (var stack in document.Stacks) groups.Add(new(stack, await owner._stacks.SnapshotAsync(repo, stack, refresh.Token)) { IsExpanded = !sameRepo || owner.LocalGroups.FirstOrDefault(g => g.Definition.Id == stack.Id)?.IsExpanded != false });
            refresh.Token.ThrowIfCancellationRequested();
            var previousGroup = owner._activeGroup; var selectedBranches = previousGroup?.Layers.Where(l => owner._selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch).ToHashSet() ?? []; var mode = owner.Mode;
            if (!sameRepo) { owner._comparison?.Cancel(); owner._githubLoad?.Cancel(); DisposeViews(); owner.Sections.Clear(); owner.RemoteGroups.Clear(); owner.OtherGroups.Clear(); owner._remoteSnapshot = null; owner._historyRemoteGraph = null; owner._activeGroup = null; owner.GitHubStatus = "Not connected"; }
            owner._repository = repo; owner._document = document; owner.RepositoryPath = repo.Root;
            if (!sameRepo) { owner._workspace = workspace; owner._displayedStateKey = null; owner._pendingRemote = null; owner._pendingLocal = false; owner.HasPendingChanges = false; owner.ActiveReview = null; owner.IsReviewOpen = false; }
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
                var selected = sameRepo && previousGroup?.IsRemote == true ? previousGroup : groups.FirstOrDefault(g => owner.GroupKey(g) == owner._workspace.ActiveGroup) ?? groups.FirstOrDefault(g => g.Definition.Id == previousGroup?.Definition.Id) ?? groups.FirstOrDefault();
                if (selected is not null)
                {
                    SetActiveGroup(selected);
                    owner._suppress = true; owner.Mode = sameRepo && selected.Definition.Id == previousGroup?.Definition.Id ? mode : DiffMode.FullStack;
                    owner._selectedPositions = sameRepo ? selected.Layers.Where(l => selectedBranches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray() : [];
                    if (!sameRepo && owner._workspace.Stacks.TryGetValue(owner.GroupKey(selected), out var saved))
                    { owner.Mode = saved.Mode; owner._selectedPositions = selected.Layers.Where(l => saved.Branches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray(); }
                    if (owner.Mode != DiffMode.FullStack && owner._selectedPositions.Length == 0) { owner.Mode = DiffMode.FullStack; owner.Error = "The selected layer disappeared. Showing the entire stack."; }
                    owner._suppress = false; await CompareAsync();
                }
                else { owner._activeGroup = null; owner._suppress = true; owner.SelectedStack = null; owner._suppress = false; owner.Layers.Clear(); owner.Sections.Clear(); owner.EmptyMessage = "Create a stack, or connect GitHub to discover PRs."; }
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
                    var restore = owner._workspace.ActiveGroup;
                    await owner.RefreshGitHubAsync();
                    foreach (var group in owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups))
                        if (owner._workspace.Stacks.TryGetValue(owner.GroupKey(group), out var saved)) group.IsExpanded = saved.Expanded;
                    var target = owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups).FirstOrDefault(g => owner.GroupKey(g) == restore);
                    if (target is not null) await SelectGroupAsync(target, saveCurrent: false);
                }
                owner.SetWindowActive(owner._windowActive);
            }
            if (stateNotice is not null) owner.Error = stateNotice;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!refresh.IsCancellationRequested) owner.Error = ex.Message + (owner._repository is null ? "" : " Previous data retained."); }
        finally { if (ReferenceEquals(owner._refresh, refresh)) { owner._refresh = null; owner.IsBusy = false; } refresh.Dispose(); }
    }

    public void SetActiveGroup(StackGroupViewModel group)
    {
        owner._comparison?.Cancel();
        foreach (var g in owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups)) { g.IsSelected = ReferenceEquals(g, group); foreach (var l in g.Layers) { l.IsSelected = false; l.IsChecked = false; } }
        owner._activeGroup = group; owner._suppress = true; owner.SelectedStack = group.Definition; owner._suppress = false;
        owner.Layers.Clear(); foreach (var layer in group.Layers) owner.Layers.Add(layer);
        owner.Notify(nameof(owner.BaseLabel)); owner.Notify(nameof(owner.IsLocalSelected)); owner.Notify(nameof(owner.CanRestackActiveStack));
    }

    public async Task SelectGroupAsync(StackGroupViewModel group, bool saveCurrent = true)
    {
        if (saveCurrent) await owner.SaveWorkspaceAsync();
        if (group.IsSinglePr) { SetActiveGroup(group); owner._selectedPositions = [0]; owner._suppress = true; owner.Mode = DiffMode.Layer; owner._suppress = false; await CompareAsync(); return; }
        SetActiveGroup(group); owner._selectedPositions = []; owner._suppress = true; owner.Mode = DiffMode.FullStack;
        if (owner._workspace.Stacks.TryGetValue(owner.GroupKey(group), out var saved))
        {
            owner._selectedPositions = group.Layers.Where(l => saved.Branches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray();
            if (saved.Branches.Length == owner._selectedPositions.Length) owner.Mode = saved.Mode;
            else { owner._selectedPositions = []; owner.EmptyMessage = "The saved layer is no longer available. Showing Overview."; }
        }
        owner._suppress = false; await CompareAsync();
    }

    public async Task SelectLayerAsync(LayerViewModel layer, bool toggle)
    {
        await owner.SaveWorkspaceAsync();
        var group = layer.Group!;
        if (!ReferenceEquals(owner._activeGroup, group)) { SetActiveGroup(group); owner._selectedPositions = []; }
        owner._selectedPositions = toggle ? (owner._selectedPositions.Contains(layer.Snapshot.Position) ? owner._selectedPositions.Where(p => p != layer.Snapshot.Position) : owner._selectedPositions.Append(layer.Snapshot.Position)).Order().ToArray() : [layer.Snapshot.Position];
        owner._suppress = true; owner.Mode = owner._selectedPositions.Length == 0 ? DiffMode.FullStack : owner._selectedPositions.Length == 1 ? DiffMode.Layer : DiffMode.MultiLayer; owner._suppress = false;
        await CompareAsync();
    }

    public void SelectLayers(IEnumerable<LayerViewModel> layers)
    {
        owner._selectedPositions = layers.Select(l => l.Snapshot.Position).Order().ToArray();
        owner._suppress = true; owner.Mode = owner._selectedPositions.Length > 1 ? DiffMode.MultiLayer : DiffMode.Layer; owner._suppress = false; _ = CompareAsync();
    }

    public async Task CompareAsync()
    {
        owner.ResetSearch();
        owner._comparison?.Cancel(); var compare = new CancellationTokenSource(); owner._comparison = compare;
        var group = owner._activeGroup; var repo = owner._repository; var mode = owner.Mode; var positions = owner._selectedPositions.ToArray();
        if (repo is null || group is null || owner._disposed) { compare.Dispose(); if (ReferenceEquals(owner._comparison, compare)) owner._comparison = null; return; }
        owner.Notify(nameof(owner.HasPrDiscussion)); owner.Notify(nameof(owner.CanReview));
        owner.IsBusy = true; owner.Error = "";
        try
        {
            var comparisonRepository = repo; var stack = group.Definition;
            if (group.IsRemote)
            {
                if (owner._remoteSnapshot is null) throw new StackerException("Refresh to load this stack.");
                comparisonRepository = await owner.PrepareRemoteAsync(group, mode, positions, compare.Token);
                stack = new(group.Definition.Id, group.Name, RemoteRefs.BaseRef(group.PullRequests[0]), group.PullRequests.Select(RemoteRefs.HeadRef).ToArray());
                // For a single PR's review use its declared server base, even if the discovered parent has moved.
                if (mode == DiffMode.Layer && positions.Length == 1)
                {
                    var pr = group.PullRequests[positions[0]];
                    stack = new(stack.Id, stack.Name, RemoteRefs.BaseRef(pr), [RemoteRefs.HeadRef(pr)]); positions = [0];
                }
            }
            var key = MainViewModel.ComparisonKey(comparisonRepository, stack, mode, positions) + "|" + owner.Settings.Theme;
            if (!owner._views.TryGetValue(key, out var sections))
            {
                var results = await owner._diff.CompareAsync(new(comparisonRepository, stack, mode, positions), compare.Token);
                compare.Token.ThrowIfCancellationRequested();
                sections = results.Select(result =>
                {
                    var pr = group.IsRemote ? group.PullRequests.FirstOrDefault(p => RemoteRefs.HeadRef(p) == result.Layer) : owner.FindLocalPr(result.Layer);
                    return new DiffSectionViewModel(owner._git, comparisonRepository.Root, result, owner._highlighter, owner.Settings.Theme)
                    { AssociatedPr = pr, GitHubContext = owner._remoteSnapshot?.Context, IsPrSnapshot = group.IsRemote && mode == DiffMode.Layer, IsDemo = owner.IsDemo, WrapCode = owner.Settings.WrapCode };
                }).ToList();
                // Carry file/filter/scroll state across a changed snapshot of the same comparison.
                foreach (var section in sections)
                {
                    var old = owner.Sections.FirstOrDefault(s => s.Result.Layer == section.Result.Layer);
                    if (old is not null) section.RestoreView(old);
                }
                owner._views[key] = sections;
                while (owner._views.Count > 24) { var victim = owner._views.First(kv => kv.Key != key && !kv.Value.Any(owner.Sections.Contains)); foreach (var s in victim.Value) s.Dispose(); owner._views.Remove(victim.Key); }
            }
            compare.Token.ThrowIfCancellationRequested();
            if (!owner.Sections.SequenceEqual(sections)) { owner.Sections.Clear(); foreach (var section in sections) owner.Sections.Add(section); }
            foreach (var layer in group.Layers) { layer.IsChecked = owner._selectedPositions.Contains(layer.Snapshot.Position); layer.IsSelected = layer.IsChecked; }
            owner.ComparisonTitle = mode switch
            {
                DiffMode.FullStack => $"Entire stack · {group.Name}",
                DiffMode.Cumulative => $"Through layer {(owner._selectedPositions.FirstOrDefault() + 1)} · {group.Name}",
                DiffMode.MultiLayer => $"Selected layers · {string.Join(", ", owner._selectedPositions.Select(p => p + 1))}",
                _ => group.IsSinglePr ? $"Pull request · {group.PullRequests[0].Label}" : $"Layer {(owner._selectedPositions.FirstOrDefault() + 1)} · {group.Layers.ElementAtOrDefault(owner._selectedPositions.FirstOrDefault())?.Title}"
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
        finally { if (ReferenceEquals(owner._comparison, compare)) { owner._comparison = null; owner.IsBusy = false; } compare.Dispose(); }
    }

    public async Task<bool> SaveStackAsync(StackDefinition stack)
    {
        if (owner._repository is null || owner._document is null) return false;
        var updated = owner._document.Stacks.Any(s => s.Id == stack.Id) ? owner._document.Stacks.Select(s => s.Id == stack.Id ? stack : s).ToArray() : owner._document.Stacks.Append(stack).ToArray();
        return await SaveStacksAsync(updated);
    }

    public async Task DeleteSelectedStackAsync()
    {
        if (owner._document is not null && owner.SelectedStack is not null && owner._activeGroup?.IsRemote != true) await SaveStacksAsync(owner._document.Stacks.Where(s => s.Id != owner.SelectedStack.Id).ToArray());
    }

    public async Task<bool> SaveStacksAsync(IReadOnlyList<StackDefinition> stacks)
    {
        if (owner._repository is null || owner._document is null) return false;
        try
        {
            (await owner._store.SaveAsync(owner._repository.Root, stacks, owner._document.Revision)).OrThrow();
            // The refresh reports its own problems; the save has already succeeded by here.
            owner._applyingChanges = true;
            try { await RefreshAsync(); } finally { owner._applyingChanges = false; }
            return true;
        }
        catch (Exception ex) { owner.Error = ex.Message; return false; }
    }

    public async Task SaveSettingsAsync()
    {
        try { owner._executable.Override = owner.Settings.GitPath; if (owner._commandLog is not null) owner._commandLog.GitExecutablePath = owner._executable.Resolve(); if (owner._ghExecutable is not null) { owner._ghExecutable.Override = owner.Settings.GhPath; owner._ghExecutable.UseSavedCredentials = owner.Settings.UseSavedGhCredentials; } (await owner._settingsStore.SaveAsync(owner.Settings)).OrThrow(); }
        catch (Exception ex) { owner.Error = "Cannot save settings: " + ex.Message; throw; }
    }

    public async Task SetWrapCodeAsync(bool enabled)
    {
        owner.Settings.WrapCode = enabled;
        foreach (var section in owner._views.Values.SelectMany(views => views).Distinct()) section.WrapCode = enabled;
        await SaveSettingsAsync();
    }

    public void DisposeViews() { foreach (var section in owner._views.Values.SelectMany(v => v).Distinct()) section.Dispose(); owner._views.Clear(); }

    public void Dispose() { if (owner._disposed) return; owner._disposed = true; if (owner._commandLog is not null) owner._commandLog.Changed -= owner.LogChanged; if (owner._commands is not null) { owner._commands.Reported -= owner.ReportCommand; owner._commands.Starting -= owner.CommandStarting; } owner._searchLoad?.Cancel(); owner._refresh?.Cancel(); owner._comparison?.Cancel(); owner._githubLoad?.Cancel(); owner._coordinator?.Dispose(); owner.DisposeReviewSessions(); DisposeViews(); }

}
