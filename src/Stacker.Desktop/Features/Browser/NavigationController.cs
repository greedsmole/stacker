using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class NavigationController(INavigationPresentation owner, WorkspaceRuntime runtime)
{
    public async Task InitializeAsync()
    {
        try
        {
            owner.Settings = (await runtime._settingsStore.LoadAsync()).OrThrow();
            runtime._executable.Override = owner.Settings.GitPath;
            if (runtime._ghExecutable is not null)
            {
                runtime._ghExecutable.Override = owner.Settings.GhPath;
                runtime._ghExecutable.UseSavedCredentials = owner.Settings.UseSavedGhCredentials;
            }
            foreach (var path in owner.Settings.RecentRepositories.Take(10))
                owner.RecentRepositories.Add(path);
            if (runtime._settingsStore is ISettingsDiagnostics { LastProblem: { } settingsProblem })
                owner.Error = settingsProblem;
            if (runtime._applicationStore.LastProblem is { } stateProblem)
                owner.Error = stateProblem;
        }
        catch (Exception ex) { owner.Error = "Cannot load settings: " + ex.Message; }
    }

    public Task RefreshAsync() => ReadRepositoryAsync(runtime._repository?.Root ?? owner.RepositoryPath, false);

    public Task OpenRepositoryAsync(string path) => ReadRepositoryAsync(path, true);

    public async Task ReadRepositoryAsync(string path, bool opening)
    {
        if (string.IsNullOrWhiteSpace(path) || runtime._disposed)
            return;
        if (opening && (owner.IsSyncing || runtime._commands?.IsBusy == true || runtime._reviews.Values.Any(review => review.IsBusy)))
        {
            owner.Error = "Wait for the current operation before switching repositories.";
            return;
        }
        runtime._refresh?.Cancel();
        var refresh = new CancellationTokenSource();
        runtime._refresh = refresh;
        owner.IsBusy = true;
        owner.Error = "";
        try
        {
            if (opening)
                owner.QueueWorkspaceSave();
            var repo = (await runtime._git.OpenRefsAsync(path, refresh.Token)).OrThrow();
            var document = (await runtime._store.LoadAsync(repo.Root, refresh.Token)).OrThrow();
            var sameRepo = runtime._repository is { } previousRepository && previousRepository.Root == repo.Root &&
                previousRepository.WorkspaceKey == repo.WorkspaceKey && previousRepository.Scope == repo.Scope;
            var workspace = runtime._workspace;
            string? stateNotice = null;
            if (!sameRepo)
            {
                var loaded = await runtime.LoadWorkspaceAsync(repo.WorkspaceKey, repo.Root, refresh.Token);
                if (loaded.IsFailed && loaded.Problem!.Code == ProblemCodes.StoreCorrupt)
                {
                    workspace = new();
                    stateNotice = loaded.Problem.Message;
                }
                else
                    workspace = loaded.OrThrow() ?? new();
            }
            if (!sameRepo)
            {
                runtime._comparison?.Cancel();
                runtime._githubLoad?.Cancel();
                await owner.ClearReviewSessionsAsync();
            }
            refresh.Token.ThrowIfCancellationRequested();
            var unchanged = sameRepo && runtime._document?.Revision == document.Revision && runtime._repository!.Refs.SequenceEqual(repo.Refs);
            if (opening)
            {
                runtime._pendingLocal = false;
                runtime._pendingRemote = null;
                owner.HasPendingChanges = false;
            }
            if (sameRepo && !opening && !unchanged && owner.Sections.Count > 0 && !runtime._applyingChanges)
            {
                runtime._pendingLocal = true;
                owner.HasPendingChanges = true;
                owner.Status = "New local changes available";
                await owner.RefreshWorkingTreeAsync(refresh.Token);
                await runtime.ObserveLocalHistoryAsync(repo, document, refresh.Token);
                return;
            }
            var groups = new List<StackGroupViewModel>();
            if (!unchanged)
            {
                var refs = repo.Refs.ToDictionary(reference => reference.Name, reference => reference.Sha, StringComparer.Ordinal);
                var oldGroups = runtime._repository?.Scope == repo.Scope
                    ? owner.LocalGroups.ToDictionary(group => group.Definition.Id, StringComparer.Ordinal) : [];
                foreach (var stack in document.Stacks)
                {
                    oldGroups.TryGetValue(stack.Id, out var prior);
                    var layers = prior is not null && SameLayerInputs(prior, stack, refs)
                        ? prior.Layers.Select(layer => layer.Snapshot).ToArray()
                        : await runtime._stacks.SnapshotAsync(repo, stack, refresh.Token);
                    groups.Add(new(stack, layers)
                    {
                        IsExpanded = sameRepo ? prior?.IsExpanded == true : workspace.Stacks.GetValueOrDefault("local:" + stack.Id)?.Expanded == true
                    });
                }
            }
            refresh.Token.ThrowIfCancellationRequested();
            var previousGroup = runtime._activeGroup;
            var selectedBranches = previousGroup?.Layers.Where(l => runtime._selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch).ToHashSet() ?? [];
            var mode = owner.Mode;
            if (!sameRepo)
            {
                runtime._comparison?.Cancel();
                runtime._githubLoad?.Cancel();
                DisposeViews();
                owner.Sections.Clear();
                owner.RemoteGroups.Clear();
                owner.OtherGroups.Clear();
                runtime._remoteSnapshot = null;
                runtime._historyRemoteGraph = null;
                runtime._activeGroup = null;
                owner.ResetCheckoutState();
                owner.Status = "";
                owner.GitHubStatus = "Not connected";
            }
            if (!unchanged)
            {
                runtime._repository = repo;
                runtime._document = document;
            }
            owner.RepositoryPath = repo.Root;
            if (!sameRepo)
            {
                runtime._workspace = workspace;
                runtime._displayedStateKey = null;
                runtime._displayedGroupKey = null;
                runtime._pendingRemote = null;
                runtime._pendingLocal = false;
                owner.HasPendingChanges = false;
                owner.ActiveReview = null;
                owner.IsReviewOpen = false;
            }
            owner.Settings.LastRepository = repo.Root;
            owner.IsDemo = repo.IsDemo;
            var authoring = !sameRepo ? owner.LoadRepositoryAuthoringAsync(repo, refresh.Token) : Task.CompletedTask;
            owner.RepositoryTitle = owner.IsDemo ? "Demo / offline" : Path.GetFileName(repo.Root);
            owner.CanEdit = true;
            var comparison = Task.CompletedTask;
            if (!unchanged)
            {
                owner.Stacks.Clear();
                owner.LocalGroups.Clear();
                foreach (var group in groups)
                {
                    for (var i = 0; i < group.Layers.Count; i++)
                        if (owner.FindLocalPr(group.Layers[i].Snapshot.Branch) is { } pr)
                            group.Layers[i] = new(group.Layers[i].Snapshot, pr)
                            {
                                Group = group
                            };
                    owner.Stacks.Add(group.Definition);
                    owner.LocalGroups.Add(group);
                }
                // Navigation saves the view being left. A refresh should prefer the group currently on screen.
                var selected = sameRepo && previousGroup is { IsRemote: true } or { UsesGhStack: true }
                    ? previousGroup
                    : (sameRepo ? groups.FirstOrDefault(g => g.Definition.Id == previousGroup?.Definition.Id) : null)
                        ?? groups.FirstOrDefault(g => owner.GroupKey(g) == runtime._workspace.ActiveGroup)
                        ?? groups.FirstOrDefault();
                if (selected is not null)
                {
                    SetActiveGroup(selected);
                    runtime._suppress = true;
                    owner.Mode = sameRepo && selected.Definition.Id == previousGroup?.Definition.Id ? mode : DiffMode.FullStack;
                    runtime._selectedPositions = sameRepo ? selected.Layers.Where(l => selectedBranches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray() : [];
                    if (!sameRepo && runtime._workspace.Stacks.TryGetValue(owner.GroupKey(selected), out var saved))
                    {
                        owner.Mode = saved.Mode;
                        runtime._selectedPositions = selected.Layers.Where(l => saved.Branches.Contains(l.Snapshot.Branch)).Select(l => l.Snapshot.Position).ToArray();
                    }
                    if (owner.Mode != DiffMode.FullStack && runtime._selectedPositions.Length == 0)
                    {
                        owner.Mode = DiffMode.FullStack;
                        owner.Error = "The selected layer disappeared. Showing the entire stack.";
                    }
                    runtime._suppress = false;
                    comparison = CompareAsync(refresh.Token);
                }
                else
                {
                    runtime._activeGroup = null;
                    runtime._suppress = true;
                    owner.SelectedStack = null;
                    runtime._suppress = false;
                    owner.Layers.Clear();
                    owner.Sections.Clear();
                    owner.Notify(nameof(owner.IsLocalSelected));
                    owner.Notify(nameof(owner.IsRemoteStackSelected));
                    owner.EmptyMessage = "Create a stack, or connect GitHub to discover PRs.";
                }
            }
            if (runtime._comparison is null)
                owner.IsBusy = false;
            var workingTree = owner.RefreshWorkingTreeAsync(refresh.Token);
            var history = !unchanged ? runtime.ObserveLocalHistoryAsync(repo, document, refresh.Token) : Task.CompletedTask;
            var syncState = !unchanged ? owner.RefreshSyncAvailabilityAsync(refresh.Token) : Task.CompletedTask;
            await Task.WhenAll(comparison, workingTree, history, syncState, authoring);
            refresh.Token.ThrowIfCancellationRequested();
            owner.Status = $"Working tree: {owner.WorkingTree?.Entries.Count ?? repo.ChangedFiles} changed entries · Updated {DateTime.Now:HH:mm:ss}";
            if (opening)
            {
                owner.Settings.RecentRepositories.Remove(repo.Root);
                owner.Settings.RecentRepositories.Insert(0, repo.Root);
                owner.Settings.RecentRepositories = owner.Settings.RecentRepositories.Take(10).ToList();
                owner.RecentRepositories.Clear();
                foreach (var recent in owner.Settings.RecentRepositories)
                    owner.RecentRepositories.Add(recent);
                var restore = runtime._workspace.ActiveGroup;
                var selection = runtime.Session.Selection;
                var files = owner.Sections.Select(section => section.SelectedFile).ToArray();
                await Task.WhenAll(SaveSettingsAsync(), owner.RefreshGitHubAsync(refresh.Token));
                refresh.Token.ThrowIfCancellationRequested();
                foreach (var group in owner.RemoteGroups.Concat(owner.OtherGroups))
                    if (runtime._workspace.Stacks.TryGetValue(owner.GroupKey(group), out var saved))
                        group.IsExpanded = saved.Expanded;
                if (selection == runtime.Session.Selection && files.SequenceEqual(owner.Sections.Select(section => section.SelectedFile)))
                {
                    var target = owner.LocalGroups.Concat(owner.RemoteGroups).Concat(owner.OtherGroups).FirstOrDefault(g => owner.GroupKey(g) == restore);
                    if (target is not null && !ReferenceEquals(runtime._activeGroup, target))
                        await SelectGroupAsync(target, saveCurrent: false);
                }
                owner.SetWindowActive(runtime._windowActive);
            }
            if (stateNotice is not null)
                owner.Error = stateNotice;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!refresh.IsCancellationRequested) owner.Error = ex.Message + (runtime._repository is null ? "" : " Previous data retained."); }
        finally
        {
            if (ReferenceEquals(runtime._refresh, refresh))
            {
                runtime._refresh = null;
                if (runtime._comparison is null)
                    owner.IsBusy = false;
            }
            refresh.Dispose();
        }
    }

    private static bool SameLayerInputs(StackGroupViewModel group, StackDefinition stack, IReadOnlyDictionary<string, string> refs) =>
        group.Definition.Base == stack.Base && group.Definition.Branches.SequenceEqual(stack.Branches) && group.Layers.All(layer =>
            layer.Snapshot.HeadSha == refs.GetValueOrDefault(layer.Snapshot.Branch) &&
            layer.Snapshot.ParentSha == refs.GetValueOrDefault(layer.Snapshot.Parent));
}
