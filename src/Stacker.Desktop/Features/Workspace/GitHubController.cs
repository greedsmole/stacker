using Stacker.Core.Framework;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

internal sealed class GitHubController(IGitHubPresentation owner, WorkspaceRuntime runtime)
{
    public async Task OpenDemoAsync()
    {
        if (runtime._demo is null) return;
        owner.IsBusy = true;
        try { await owner.OpenRepositoryAsync((await runtime._demo.CreateAsync()).OrThrow()); }
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
        if (runtime._repository is null) return;
        runtime._githubLoad?.Cancel(); var load = CancellationTokenSource.CreateLinkedTokenSource(ct); runtime._githubLoad = load;
        var root = runtime._repository.Root; owner.GitHubBusy = true;
        try
        {
            owner.GitHubPreferences = (await runtime._applicationStore.ReadAsync<RepositoryPreferences>("repositories", root, load.Token)).OrThrow() ?? new();
            GitHubSnapshot snapshot;
            if (owner.IsDemo) snapshot = runtime._git is IDemoSnapshotReader demo
                ? (await demo.ReadDemoSnapshotAsync(root, load.Token)).OrThrow() ?? throw new StackerException("The offline demo snapshot is missing.")
                : throw new StackerException("This repository reader cannot load offline demo snapshots.");
            else
            {
                if (runtime._github is null) return;
                var context = (await runtime._github.ConnectAsync(root, owner.GitHubPreferences.GitHubOverride, load.Token)).OrThrow();
                snapshot = (await runtime._github.SnapshotAsync(context, load.Token)).OrThrow();
            }
            load.Token.ThrowIfCancellationRequested();
            var historyGraph = runtime.HistoryGraph is null ? null : await runtime.HistoryGraph.ObserveRemoteAsync(snapshot, owner.GitHubPreferences.BoundaryBranches ?? [snapshot.Context.DefaultBranch], load.Token);
            if (runtime.Identities is not null && !background)
            {
                foreach (var pr in snapshot.PullRequests.Where(p => p.HeadRepositoryId == snapshot.Context.RepositoryId))
                {
                    var reference = "refs/heads/" + pr.HeadRef;
                    if (runtime._repository.Refs.Any(r => r.Name == reference && r.Sha == pr.HeadSha))
                    {
                        var binding = await runtime.Identities.BindAsync(runtime._repository.Scope, reference, snapshot.Context, pr, load.Token);
                        if (binding.IsFailed) owner.Error = binding.Problem!.Message;
                    }
                }
            }
            var changed = runtime._remoteSnapshot is null || runtime._remoteSnapshot.Context != snapshot.Context || !runtime._remoteSnapshot.PullRequests.SequenceEqual(snapshot.PullRequests) || historyGraph is not null && runtime._historyRemoteGraph is not null && !historyGraph.Edges.SequenceEqual(runtime._historyRemoteGraph.Edges);
            if (changed && runtime._remoteSnapshot is not null && owner.Sections.Count > 0 && !runtime._applyingChanges)
            { runtime.Session.Offer(snapshot, historyGraph); owner.HasPendingChanges = true; owner.GitHubStatus = $"New changes available · checked {DateTime.Now:HH:mm}"; return; }
            runtime._remoteSnapshot = snapshot; runtime._historyRemoteGraph = historyGraph;
            (await runtime._applicationStore.WriteAsync("snapshots", root, snapshot, load.Token)).OrThrow();
            owner.GitHubPreferences.BoundaryBranches ??= new[] { snapshot.Context.DefaultBranch }.Concat(new[] { "main", "master", "develop", "test" }.Where(b => runtime._repository!.Refs.Any(r => r.Name.EndsWith('/' + b, StringComparison.Ordinal)) || snapshot.PullRequests.Any(p => p.BaseRef == b || p.HeadRef == b))).Distinct().ToList();
            (await runtime._applicationStore.WriteAsync("repositories", root, owner.GitHubPreferences, load.Token)).OrThrow();
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
                var saved = (await runtime._applicationStore.ReadAsync<GitHubSnapshot>("snapshots", root, load.Token)).OrThrow();
                if (runtime._remoteSnapshot is null && saved is not null) { runtime._remoteSnapshot = saved; BuildDiscoveredGroups(); owner.GitHubStatus += $" · Cached {saved.Context.Display} · {saved.UpdatedAt.LocalDateTime:g}"; }
            }
            catch (Exception cacheError) when (cacheError is not OperationCanceledException) { owner.GitHubStatus += " · Cache unavailable"; }
            if (background) throw;
        }
        finally { if (ReferenceEquals(runtime._githubLoad, load)) { runtime._githubLoad = null; owner.GitHubBusy = false; } load.Dispose(); }
    }

    public void BuildDiscoveredGroups()
    {
        if (runtime._remoteSnapshot is null) return;
        var result = runtime._historyRemoteGraph is { } graph && graph.Scope == runtime._remoteSnapshot.Context.Identity
            ? GraphDiscovery.Discover(runtime._remoteSnapshot, graph)
            : StackDiscovery.Discover(runtime._remoteSnapshot.PullRequests, owner.GitHubPreferences.BoundaryBranches ?? [runtime._remoteSnapshot.Context.DefaultBranch]);
        var previous = owner.RemoteGroups.Concat(owner.OtherGroups).ToDictionary(g => g.Definition.Id);
        StackGroupViewModel Retain(StackGroupViewModel next)
        {
            if (!previous.TryGetValue(next.Definition.Id, out var old)) return next;
            if (old.PullRequests.SequenceEqual(next.PullRequests) && old.SharedPrefix == next.SharedPrefix) return old;
            next.IsExpanded = old.IsExpanded; return next;
        }
        owner.RemoteGroups.Clear(); owner.OtherGroups.Clear();
        foreach (var stack in result.Stacks) owner.RemoteGroups.Add(Retain(WorkspaceRuntime.RemoteGroup(stack.Id, stack.Name, stack.Layers, stack.SharedPrefix)));
        foreach (var pr in result.Other) owner.OtherGroups.Add(Retain(WorkspaceRuntime.RemoteGroup($"github-{pr.Number}", pr.Title, [pr], false)));
        if (result.Warnings.Count > 0) owner.Error = string.Join("\n", result.Warnings);
        // Keep local stack bindings informative without changing their authored definition.
        foreach (var group in owner.LocalGroups)
            for (var i = 0; i < group.Layers.Count; i++)
            {
                var old = group.Layers[i]; var pr = FindLocalPr(old.Snapshot.Branch);
                if (pr is not null) group.Layers[i] = new(old.Snapshot, pr) { Group = group, IsSelected = old.IsSelected, IsChecked = old.IsChecked };
            }
        foreach (var section in runtime._views.Values.SelectMany(s => s).Where(s => !s.IsPrSnapshot))
            if (FindLocalPr(section.Result.Layer) is { } pr) section.BindPr(pr, runtime._remoteSnapshot.Context);
        if (runtime._activeGroup?.IsRemote == true)
        {
            var replacement = owner.RemoteGroups.Concat(owner.OtherGroups).FirstOrDefault(g => g.Definition.Id == runtime._activeGroup.Definition.Id);
            if (replacement is not null) { owner.SetActiveGroup(replacement); }
            else
            {
                runtime._comparison?.Cancel(); runtime._activeGroup = null; runtime._selectedPositions = []; owner.Layers.Clear(); owner.Sections.Clear();
                runtime._suppress = true; owner.SelectedStack = null; runtime._suppress = false;
                owner.EmptyMessage = "The selected PR stack changed or closed. Select an available stack.";
                owner.Notify(nameof(owner.BaseLabel)); owner.Notify(nameof(owner.HasSelectedLayer)); owner.Notify(nameof(owner.IsLocalSelected));
            }
        }
        owner.Notify(nameof(owner.HasPrDiscussion)); owner.Notify(nameof(owner.CanReview)); owner.Notify(nameof(owner.HasRemoteGroups)); owner.Notify(nameof(owner.HasOtherGroups));
    }

    public PullRequest? FindLocalPr(string branch)
    {
        var name = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) ? branch[20..] : "";
        var matches = runtime._remoteSnapshot?.PullRequests.Where(p => p.HeadRepositoryId == runtime._remoteSnapshot.Context.RepositoryId && p.HeadRef == name).ToArray();
        return matches?.Length == 1 ? matches[0] : null;
    }

    public async Task<RepositorySnapshot> PrepareRemoteAsync(StackGroupViewModel group, DiffMode mode, int[] positions, CancellationToken ct)
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
            return runtime._repository! with { Refs = refs };
        }
        var repository = runtime._repository ?? throw new StackerException("Open a local repository first.");
        if (repository.Identity is not { } identity) throw new StackerException("Repository identity is unavailable.");
        if (runtime._prObjects is not { } objects) throw new StackerException("PR object preparation is unavailable.");
        (await objects.EnsureAsync(identity, runtime._remoteSnapshot!.Context, prs, ct)).OrThrow();
        return repository with { Refs = prs.SelectMany(pr => new[] { new BranchRef(RemoteRefs.HeadRef(pr), pr.HeadSha), new BranchRef(RemoteRefs.BaseRef(pr), pr.BaseSha) }).DistinctBy(r => r.Name).ToArray() };
    }

    public async Task SaveGitHubPreferencesAsync(RepositoryPreferences preferences)
    {
        if (runtime._repository is null) return;
        try
        {
            (await runtime._applicationStore.WriteAsync("repositories", runtime._repository.Root, preferences)).OrThrow(); owner.GitHubPreferences = preferences;
            runtime._applyingChanges = true;
            try { await RefreshGitHubAsync(); BuildDiscoveredGroups(); if (runtime._activeGroup is not null) await owner.CompareAsync(); }
            finally { runtime._applyingChanges = false; }
        }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    public async Task ClearGitHubCacheAsync()
    {
        if (runtime._remoteSnapshot is null || runtime._prObjects is null || owner.IsDemo) return;
        try { runtime._comparison?.Cancel(); (await runtime._prObjects.ClearAsync(runtime._repository!.Identity!, runtime._remoteSnapshot.Context)).OrThrow(); owner.DisposeViews(); owner.Sections.Clear(); owner.GitHubStatus = "PR retention references cleared. Objects remain managed by the local Git repository."; }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    public async Task SaveRemoteAsLocalAsync()
    {
        if (runtime._activeGroup?.IsRemote != true || runtime._repository is null) return;
        try
        {
            var prs = runtime._activeGroup.PullRequests;
            string Resolve(string branch, long repositoryId)
            {
                if (repositoryId != runtime._remoteSnapshot!.Context.RepositoryId) throw new StackerException("Fork refs cannot be mapped to local branches automatically.");
                return new[] { "refs/heads/" + branch, "refs/remotes/origin/" + branch }.FirstOrDefault(r => runtime._repository.Refs.Any(p => p.Name == r)) ?? throw new StackerException($"No local ref for {branch}. Fetch it outside Stacker first.");
            }
            await owner.SaveStackAsync(new(Guid.NewGuid().ToString("N"), runtime._activeGroup.Name, Resolve(prs[0].BaseRef, prs[0].BaseRepositoryId), prs.Select(p => Resolve(p.HeadRef, p.HeadRepositoryId)).ToArray()));
        }
        catch (Exception ex) { owner.Error = ex.Message; }
    }

    public DiffSectionViewModel? CreateDiscussionContext()
    {
        if (owner.SelectedPrForDiscussion is not { } pr || runtime._remoteSnapshot is null || runtime._repository is null) return null;
        return new(runtime._git, runtime._repository.Root, new(pr.HeadRef, pr.BaseRef, pr.BaseSha, pr.HeadSha, null, []))
        { AssociatedPr = pr, GitHubContext = runtime._remoteSnapshot.Context, IsDemo = owner.IsDemo };
    }

    public async Task OpenPrChangesAsync(DiffSectionViewModel section)
    {
        var pr = section.AssociatedPr; if (pr is null || runtime._remoteSnapshot is null) return;
        var group = owner.RemoteGroups.Concat(owner.OtherGroups).FirstOrDefault(g => g.PullRequests.Any(p => p.Number == pr.Number));
        if (group is null) return;
        var index = group.PullRequests.ToList().FindIndex(p => p.Number == pr.Number);
        await owner.SelectLayerAsync(group.Layers[index], false);
    }

}
