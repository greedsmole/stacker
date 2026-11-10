using Stacker.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private HistoryWorkspaceViewModel? _activeHistory;
    private string? _historyGroup;
    private bool _synchronizingHistory;
    public bool IsHistoryOpen => ActiveHistory is not null;
    partial void OnActiveHistoryChanged(HistoryWorkspaceViewModel? value) => OnPropertyChanged(nameof(IsHistoryOpen));
    public async Task ShowHistoryAsync()
    {
        var next = await OpenHistoryAsync();
        if (next is null) return;
        ActiveHistory?.Dispose(); ActiveHistory = next; _historyGroup = _runtime._activeGroup?.Definition.Id;
        next.SelectionChanged = async (node, file) =>
        {
            if (_synchronizingHistory || _runtime._activeGroup is null) return;
            _synchronizingHistory = true;
            try
            {
                var layer = _runtime._activeGroup.Layers.FirstOrDefault(l => node.PullRequest is not null ? l.PullRequest?.Number == node.PullRequest : l.Snapshot.Branch == node.Reference);
                if (layer is not null && (_runtime._selectedPositions.Length != 1 || _runtime._selectedPositions[0] != layer.Snapshot.Position)) await _navigationController.SelectLayerAsync(layer, false);
                var section = Sections.FirstOrDefault();
                if (section is not null && file is not null) section.SelectedFile = section.Files.FirstOrDefault(f => f.NewPath == file.NewPath);
                _runtime.Session.Select(new(node.Id, section is null ? null : ComparisonId.For(node.Id, section.Result.BaseSha, section.Result.HeadSha).Value, file?.NewPath));
            }
            finally { _synchronizingHistory = false; }
        };
        var selected = _runtime._selectedPositions.FirstOrDefault();
        if (selected < next.Nodes.Count) { next.SelectedNode = next.Nodes[selected]; await next.LoadLayerCommand.ExecuteAsync(null); }
    }
    [RelayCommand] private void CloseHistory() { ActiveHistory?.Dispose(); ActiveHistory = null; }
    private async Task SynchronizeHistoryAsync()
    {
        if (ActiveHistory is null || _synchronizingHistory) return;
        if (_historyGroup != _runtime._activeGroup?.Definition.Id) { await ShowHistoryAsync(); return; }
        var position = _runtime._selectedPositions.FirstOrDefault();
        if (position < ActiveHistory.Nodes.Count) { ActiveHistory.SelectedNode = ActiveHistory.Nodes[position]; await ActiveHistory.LoadLayerCommand.ExecuteAsync(null); }
    }
    public async Task<HistoryWorkspaceViewModel?> OpenHistoryAsync(CancellationToken ct = default)
    {
        if (_runtime.HistoryStore is null || _runtime.HistoryObjects is null || _runtime.HistoryGraph is null || _runtime.HistoryComparisons is null || _runtime.HistoryProgress is null)
        { Error = "Review history is unavailable in this session."; return null; }
        var group = _runtime._activeGroup; var repo = _runtime._repository;
        if (group is null || repo is null) { Error = "Select a stack or pull request first."; return null; }
        if (HasPendingChanges) { Error = "Apply Update changes before opening review history."; return null; }
        IsBusy = true;
        try
        {
            var graph = group.IsRemote && _runtime._remoteSnapshot is not null
                ? await _runtime.HistoryGraph.ObserveRemoteAsync(_runtime._remoteSnapshot, GitHubPreferences.BoundaryBranches ?? [], ct)
                : await _runtime.HistoryGraph.ObserveLocalAsync(repo, _runtime._document?.Stacks ?? [], ct);
            if (group.IsRemote) await PrepareRemoteAsync(group, DiffMode.MultiLayer, Enumerable.Range(0, group.Layers.Count).ToArray(), ct);
            var repository = repo.Identity ?? throw new StackerException("Repository identity is unavailable.");
            var path = new List<(ChangeNode Node, ChangeVersion Version)>();
            for (var i = 0; i < group.Layers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var layer = group.Layers[i]; var pr = group.IsRemote ? group.PullRequests[i] : null;
                var node = graph.Nodes.Single(n => pr is null ? n.Reference == layer.Snapshot.Branch : n.PullRequest == pr.Number);
                var head = pr?.HeadSha ?? layer.Snapshot.HeadSha; var basis = pr?.BaseSha ?? layer.Snapshot.ParentSha;
                if (head is null || basis is null) throw new StackerException("A layer ref is missing; refresh its definition first.");
                (await _runtime.HistoryObjects.CaptureAsync(repository, graph.Scope, [basis, head], ct)).OrThrow();
                var bases = (await _runtime._git.MergeBasesAsync(repository.Root, basis, head, ct)).OrThrow();
                var version = (await _runtime.HistoryStore.ObserveAsync(new("", node.Id, basis, head, bases.Count == 1 ? bases[0] : null, DateTimeOffset.UtcNow), ct)).OrThrow();
                if (version.MergeBase is null) throw new StackerException("Layer has no unique merge-base. Its metadata was saved; code comparison is unavailable.");
                path.Add((node, version));
            }
            if (path.Count == 0) throw new StackerException("The stack has no layers.");
            var context = _runtime._remoteSnapshot?.Context; var demo = IsDemo;
            var view = new HistoryWorkspaceViewModel(_runtime.HistoryStore, _runtime.HistoryObjects, _runtime._git, _runtime.HistoryComparisons, _runtime.HistoryProgress,
                graph with { Versions = graph.Versions.Concat(path.Select(p => new KeyValuePair<string, string>(p.Node.Id, p.Version.Id))).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value) }, repository, path, group.IsRemote ? async (node, version, token) =>
                {
                    var pr = group.PullRequests.Single(p => p.Number == node.PullRequest) with { BaseSha = version.BaseSha, HeadSha = version.HeadSha };
                    return demo ? DemoReviewData.Discussion(pr) : _runtime._github is null || context is null ? null : (await _runtime._github.DiscussionAsync(context, pr, token)).OrThrow();
                }
            : null);
            view.ContentLease = (await _runtime.HistoryObjects.AcquireAsync(repository, graph.Scope, ct)).OrThrow();
            try { await view.InitializeAsync(); return view; } catch { view.Dispose(); throw; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Error = ex.Message; return null; }
        finally { IsBusy = false; }
    }
    public async Task CaptureHistorySectionAsync(DiffSectionViewModel section, CancellationToken ct)
    {
        if (_runtime.HistoryStore is null || _runtime.HistoryObjects is null || _runtime._repository is null) return;
        var remote = section.IsPrSnapshot && section.AssociatedPr is not null && section.GitHubContext is not null;
        var scope = remote ? section.GitHubContext!.Identity : _runtime._repository.Scope;
        var node = (await _runtime.HistoryStore.ResolveNodeAsync(scope, remote ? section.AssociatedPr!.HeadRef : section.Result.Layer, remote ? section.AssociatedPr!.Title : section.Title,
            remote ? section.AssociatedPr!.Number : null, ct)).OrThrow();
        var declaredBase = remote ? section.AssociatedPr!.BaseSha : _runtime._repository.Refs.FirstOrDefault(r => r.Name == section.Result.ParentRef)?.Sha ?? section.Result.BaseSha;
        (await _runtime.HistoryObjects.CaptureAsync(_runtime._repository.Identity!, scope, [declaredBase, section.Result.HeadSha], ct)).OrThrow();
        (await _runtime.HistoryStore.ObserveAsync(new("", node.Id, declaredBase, section.Result.HeadSha, section.Result.BaseSha, DateTimeOffset.UtcNow), ct)).OrThrow();
    }
    internal async Task ObserveHistoryMetadataAsync(CancellationToken ct)
    {
        if (_runtime.HistoryGraph is null || _runtime._repository is null) return;
        await _runtime.HistoryGraph.ObserveLocalAsync(_runtime._repository, _runtime._document?.Stacks ?? [], ct);
        if (_runtime._remoteSnapshot is not null) await _runtime.HistoryGraph.ObserveRemoteAsync(_runtime._remoteSnapshot, GitHubPreferences.BoundaryBranches ?? [], ct);
    }
}
