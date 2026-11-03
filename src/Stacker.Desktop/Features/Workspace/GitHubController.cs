using Stacker.Core.Framework;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

internal sealed class GitHubController(MainViewModel owner)
{
    public async Task OpenDemoAsync()
    {
        if (owner._demo is null) return;
        owner.IsBusy = true;
        try { await owner.OpenRepositoryAsync((await owner._demo.CreateAsync()).OrThrow()); }
        catch (Exception ex) { owner.Error = ex.Message; }
        finally { owner.IsBusy = false; }
    }

    public async Task RefreshGitHubAsync()
    {
        try { await RefreshGitHubCoreAsync(default, false); }
        catch (Exception ex) { owner.GitHubStatus = ex.Message; }
    }

    public async Task RefreshGitHubCoreAsync(CancellationToken ct, bool background)
    {
        if (owner._repository is null) return;
        owner._githubLoad?.Cancel(); var load = CancellationTokenSource.CreateLinkedTokenSource(ct); owner._githubLoad = load;
        var root = owner._repository.Root; owner.GitHubBusy = true;
        try
        {
            owner.GitHubPreferences = (await owner._applicationStore.ReadAsync<RepositoryPreferences>("repositories", root, load.Token)).OrThrow() ?? new();
            GitHubSnapshot snapshot;
            if (owner.IsDemo) snapshot = owner._git is IDemoSnapshotReader demo
                ? (await demo.ReadDemoSnapshotAsync(root, load.Token)).OrThrow() ?? throw new StackerException("The offline demo snapshot is missing.")
                : throw new StackerException("This repository reader cannot load offline demo snapshots.");
            else
            {
                if (owner._github is null) return;
                var context = (await owner._github.ConnectAsync(root, owner.GitHubPreferences.GitHubOverride, load.Token)).OrThrow();
                snapshot = (await owner._github.SnapshotAsync(context, load.Token)).OrThrow();
            }
            load.Token.ThrowIfCancellationRequested();
            var historyGraph = owner.HistoryGraph is null ? null : await owner.HistoryGraph.ObserveRemoteAsync(snapshot, owner.GitHubPreferences.BoundaryBranches ?? [snapshot.Context.DefaultBranch], load.Token);
            var changed = owner._remoteSnapshot is null || owner._remoteSnapshot.Context != snapshot.Context || !owner._remoteSnapshot.PullRequests.SequenceEqual(snapshot.PullRequests) || historyGraph is not null && owner._historyRemoteGraph is not null && !historyGraph.Edges.SequenceEqual(owner._historyRemoteGraph.Edges);
            if (changed && owner._remoteSnapshot is not null && owner.Sections.Count > 0 && !owner._applyingChanges)
            { owner._pendingRemote = snapshot; owner.HasPendingChanges = true; owner.GitHubStatus = $"New changes available · checked {DateTime.Now:HH:mm}"; return; }
            owner._remoteSnapshot = snapshot; owner._historyRemoteGraph = historyGraph;
            (await owner._applicationStore.WriteAsync("snapshots", root, snapshot, load.Token)).OrThrow();
            owner.GitHubPreferences.BoundaryBranches ??= new[] { snapshot.Context.DefaultBranch }.Concat(new[] { "main", "master", "develop", "test" }.Where(b => owner._repository!.Refs.Any(r => r.Name.EndsWith('/' + b, StringComparison.Ordinal)) || snapshot.PullRequests.Any(p => p.BaseRef == b || p.HeadRef == b))).Distinct().ToList();
            (await owner._applicationStore.WriteAsync("repositories", root, owner.GitHubPreferences, load.Token)).OrThrow();
            load.Token.ThrowIfCancellationRequested();
            if (changed) BuildDiscoveredGroups();
            owner.GitHubStatus = owner.IsDemo ? "Demo / offline · publishing disabled" : snapshot.Context.Display + $" · Updated {snapshot.UpdatedAt.LocalDateTime:HH:mm}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (load.IsCancellationRequested) return;
            owner.GitHubStatus = ex.Message;
            try
            {
                var saved = (await owner._applicationStore.ReadAsync<GitHubSnapshot>("snapshots", root, load.Token)).OrThrow();
                if (owner._remoteSnapshot is null && saved is not null) { owner._remoteSnapshot = saved; BuildDiscoveredGroups(); owner.GitHubStatus += $" · Cached {saved.Context.Display} · {saved.UpdatedAt.LocalDateTime:g}"; }
            }
            catch (Exception cacheError) when (cacheError is not OperationCanceledException) { owner.GitHubStatus += " · Cache unavailable"; }
            if (background) throw;
        }
        finally { if (ReferenceEquals(owner._githubLoad, load)) { owner._githubLoad = null; owner.GitHubBusy = false; } load.Dispose(); }
    }

    public void BuildDiscoveredGroups()
    {
        if (owner._remoteSnapshot is null) return;
        var result = owner._historyRemoteGraph is { } graph && graph.Scope == owner._remoteSnapshot.Context.Identity
            ? GraphDiscovery.Discover(owner._remoteSnapshot, graph)
            : StackDiscovery.Discover(owner._remoteSnapshot.PullRequests, owner.GitHubPreferences.BoundaryBranches ?? [owner._remoteSnapshot.Context.DefaultBranch]);
        var previous = owner.RemoteGroups.Concat(owner.OtherGroups).ToDictionary(g => g.Definition.Id);
        StackGroupViewModel Retain(StackGroupViewModel next)
        {
            if (!previous.TryGetValue(next.Definition.Id, out var old)) return next;
            if (old.PullRequests.SequenceEqual(next.PullRequests) && old.SharedPrefix == next.SharedPrefix) return old;
            next.IsExpanded = old.IsExpanded; return next;
        }
        owner.RemoteGroups.Clear(); owner.OtherGroups.Clear();
        foreach (var stack in result.Stacks) owner.RemoteGroups.Add(Retain(MainViewModel.RemoteGroup(stack.Id, stack.Name, stack.Layers, stack.SharedPrefix)));
        foreach (var pr in result.Other) owner.OtherGroups.Add(Retain(MainViewModel.RemoteGroup($"github-{pr.Number}", pr.Title, [pr], false)));
        if (result.Warnings.Count > 0) owner.Error = string.Join("\n", result.Warnings);
        // Keep local stack bindings informative without changing their authored definition.
        foreach (var group in owner.LocalGroups)
            for (var i = 0; i < group.Layers.Count; i++)
            {
                var old = group.Layers[i]; var pr = FindLocalPr(old.Snapshot.Branch);
                if (pr is not null) group.Layers[i] = new(old.Snapshot, pr) { Group = group, IsSelected = old.IsSelected, IsChecked = old.IsChecked };
            }
        foreach (var section in owner._views.Values.SelectMany(s => s).Where(s => !s.IsPrSnapshot))
            if (FindLocalPr(section.Result.Layer) is { } pr) section.BindPr(pr, owner._remoteSnapshot.Context);
        if (owner._activeGroup?.IsRemote == true)
        {
            var replacement = owner.RemoteGroups.Concat(owner.OtherGroups).FirstOrDefault(g => g.Definition.Id == owner._activeGroup.Definition.Id);
            if (replacement is not null) { owner.SetActiveGroup(replacement); }
            else
            {
                owner._comparison?.Cancel(); owner._activeGroup = null; owner._selectedPositions = []; owner.Layers.Clear(); owner.Sections.Clear();
                owner._suppress = true; owner.SelectedStack = null; owner._suppress = false;
                owner.EmptyMessage = "The selected PR stack changed or closed. Select an available stack.";
                owner.Notify(nameof(owner.BaseLabel)); owner.Notify(nameof(owner.HasSelectedLayer)); owner.Notify(nameof(owner.IsLocalSelected));
            }
        }
        owner.Notify(nameof(owner.HasPrDiscussion)); owner.Notify(nameof(owner.CanReview)); owner.Notify(nameof(owner.HasRemoteGroups)); owner.Notify(nameof(owner.HasOtherGroups));
    }

    public PullRequest? FindLocalPr(string branch)
    {
        var name = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) ? branch[20..] : "";
        var matches = owner._remoteSnapshot?.PullRequests.Where(p => p.HeadRepositoryId == owner._remoteSnapshot.Context.RepositoryId && p.HeadRef == name).ToArray();
        return matches?.Length == 1 ? matches[0] : null;
    }

    public Task<RepositorySnapshot> PrepareRemoteAsync(StackGroupViewModel group, DiffMode mode, int[] positions, CancellationToken ct)
    {
        var required = mode switch
        {
            DiffMode.FullStack => new[] { 0, group.PullRequests.Count - 1 },
            DiffMode.Cumulative => new[] { 0, positions[0] },
            DiffMode.MultiLayer => positions.Concat(positions.Where(p => p > 0).Select(p => p - 1)).ToArray(),
            _ => positions
        };
        var prs = required.Distinct().Order().Select(index => group.PullRequests[index]).ToArray();
        if (owner.IsDemo)
        {
            IReadOnlyList<BranchRef> refs = prs.SelectMany(p => new[] { new BranchRef(RemoteRefs.HeadRef(p), p.HeadSha), new BranchRef(RemoteRefs.BaseRef(p), p.BaseSha) }).ToArray();
            return Task.FromResult(new RepositorySnapshot(owner._repository!.Root, refs, 0));
        }
        return owner._objectCache is { } cache ? Boundary.UnwrapAsync(cache.PrepareAsync(owner._remoteSnapshot!.Context, prs, ct)) : throw new StackerException("GitHub cache is unavailable.");
    }

    public async Task SaveGitHubPreferencesAsync(RepositoryPreferences preferences)
    {
        if (owner._repository is null) return;
        try
        {
            (await owner._applicationStore.WriteAsync("repositories", owner._repository.Root, preferences)).OrThrow(); owner.GitHubPreferences = preferences;
            owner._applyingChanges = true;
            try { await RefreshGitHubAsync(); BuildDiscoveredGroups(); if (owner._activeGroup is not null) await owner.CompareAsync(); }
            finally { owner._applyingChanges = false; }
        }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    public async Task ClearGitHubCacheAsync()
    {
        if (owner._remoteSnapshot is null || owner._objectCache is null || owner.IsDemo) return;
        try { owner._comparison?.Cancel(); (await owner._objectCache.ClearAsync(owner._remoteSnapshot.Context)).OrThrow(); owner.DisposeViews(); owner.Sections.Clear(); owner.GitHubStatus = "Git object cache cleared. Select a PR to download it again."; }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    public async Task SaveRemoteAsLocalAsync()
    {
        if (owner._activeGroup?.IsRemote != true || owner._repository is null) return;
        try
        {
            var prs = owner._activeGroup.PullRequests;
            string Resolve(string branch, long repositoryId)
            {
                if (repositoryId != owner._remoteSnapshot!.Context.RepositoryId) throw new StackerException("Fork refs cannot be mapped to local branches automatically.");
                return new[] { "refs/heads/" + branch, "refs/remotes/origin/" + branch }.FirstOrDefault(r => owner._repository.Refs.Any(p => p.Name == r)) ?? throw new StackerException($"No local ref for {branch}. Fetch it outside Stacker first.");
            }
            await owner.SaveStackAsync(new(Guid.NewGuid().ToString("N"), owner._activeGroup.Name, Resolve(prs[0].BaseRef, prs[0].BaseRepositoryId), prs.Select(p => Resolve(p.HeadRef, p.HeadRepositoryId)).ToArray()));
        }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    public DiffSectionViewModel? CreateDiscussionContext()
    {
        if (owner.SelectedPrForDiscussion is not { } pr || owner._remoteSnapshot is null || owner._repository is null) return null;
        return new(owner._git, owner._repository.Root, new(pr.HeadRef, pr.BaseRef, pr.BaseSha, pr.HeadSha, null, []))
        { AssociatedPr = pr, GitHubContext = owner._remoteSnapshot.Context, IsDemo = owner.IsDemo };
    }

    public async Task OpenPrChangesAsync(DiffSectionViewModel section)
    {
        var pr = section.AssociatedPr; if (pr is null || owner._remoteSnapshot is null) return;
        var group = owner.RemoteGroups.Concat(owner.OtherGroups).FirstOrDefault(g => g.PullRequests.Any(p => p.Number == pr.Number));
        if (group is null) return;
        var index = group.PullRequests.ToList().FindIndex(p => p.Number == pr.Number);
        await owner.SelectLayerAsync(group.Layers[index], false);
    }

}
