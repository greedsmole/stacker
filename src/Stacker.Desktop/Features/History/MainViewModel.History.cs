using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    internal IHistoryStore? HistoryStore;
    internal IHistoryGit? HistoryObjects;
    internal ChangeGraphService? HistoryGraph;
    internal VersionComparisonService? HistoryComparisons;
    internal ReviewProgressService? HistoryProgress;
    public async Task<HistoryWorkspaceViewModel?> OpenHistoryAsync(CancellationToken ct = default)
    {
        if (HistoryStore is null || HistoryObjects is null || HistoryGraph is null || HistoryComparisons is null || HistoryProgress is null)
        { Error = "Review history is unavailable in this session."; return null; }
        var group = _activeGroup; var repo = _repository;
        if (group is null || repo is null) { Error = "Select a stack or pull request first."; return null; }
        if (HasPendingChanges) { Error = "Apply Update changes before opening review history."; return null; }
        IsBusy = true;
        try
        {
            var graph = group.IsRemote && _remoteSnapshot is not null
                ? await HistoryGraph.ObserveRemoteAsync(_remoteSnapshot, GitHubPreferences.BoundaryBranches ?? [], ct)
                : await HistoryGraph.ObserveLocalAsync(repo, _document?.Stacks ?? [], ct);
            var source = group.IsRemote ? await PrepareRemoteAsync(group, DiffMode.MultiLayer, Enumerable.Range(0, group.Layers.Count).ToArray(), ct) : repo;
            var path = new List<(ChangeNode Node, ChangeVersion Version)>(); string? objectRoot = null;
            for (var i = 0; i < group.Layers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var layer = group.Layers[i]; var pr = group.IsRemote ? group.PullRequests[i] : null;
                var node = graph.Nodes.Single(n => pr is null ? n.Reference == layer.Snapshot.Branch : n.PullRequest == pr.Number);
                var head = pr?.HeadSha ?? layer.Snapshot.HeadSha; var basis = pr?.BaseSha ?? layer.Snapshot.ParentSha;
                if (head is null || basis is null) throw new StackerException("A layer ref is missing; refresh its definition first.");
                objectRoot = (await HistoryObjects.CaptureAsync(graph.Scope, source.Root, [basis, head], ct)).OrThrow();
                var bases = (await _git.MergeBasesAsync(objectRoot, basis, head, ct)).OrThrow();
                var version = (await HistoryStore.ObserveAsync(new("", node.Id, basis, head, bases.Count == 1 ? bases[0] : null, DateTimeOffset.UtcNow), ct)).OrThrow();
                if (version.MergeBase is null) throw new StackerException("Layer has no unique merge-base. Its metadata was saved; code comparison is unavailable.");
                path.Add((node, version));
            }
            if (objectRoot is null) throw new StackerException("The stack has no layers.");
            var context = _remoteSnapshot?.Context; var demo = IsDemo;
            var view = new HistoryWorkspaceViewModel(HistoryStore, HistoryObjects, _git, HistoryComparisons, HistoryProgress,
                graph with { Versions = graph.Versions.Concat(path.Select(p => new KeyValuePair<string, string>(p.Node.Id, p.Version.Id))).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value) }, objectRoot, path, group.IsRemote ? async (node, version, token) =>
                {
                    var pr = group.PullRequests.Single(p => p.Number == node.PullRequest) with { BaseSha = version.BaseSha, HeadSha = version.HeadSha };
                    return demo ? DemoReviewData.Discussion(pr) : _github is null || context is null ? null : (await _github.DiscussionAsync(context, pr, token)).OrThrow();
                } : null);
            await view.InitializeAsync(); return view;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Error = ex.Message; return null; }
        finally { IsBusy = false; }
    }
    internal async Task CaptureHistorySectionAsync(DiffSectionViewModel section, CancellationToken ct)
    {
        if (HistoryStore is null || HistoryObjects is null || _repository is null) return;
        var remote = section.IsPrSnapshot && section.AssociatedPr is not null && section.GitHubContext is not null;
        var scope = remote ? section.GitHubContext!.Identity : "local:" + _repository.Root;
        var node = (await HistoryStore.ResolveNodeAsync(scope, remote ? section.AssociatedPr!.HeadRef : section.Result.Layer, remote ? section.AssociatedPr!.Title : section.Title,
            remote ? section.AssociatedPr!.Number : null, ct)).OrThrow();
        var declaredBase = remote ? section.AssociatedPr!.BaseSha : _repository.Refs.FirstOrDefault(r => r.Name == section.Result.ParentRef)?.Sha ?? section.Result.BaseSha;
        (await HistoryObjects.CaptureAsync(scope, section.Root, [declaredBase, section.Result.HeadSha], ct)).OrThrow();
        (await HistoryStore.ObserveAsync(new("", node.Id, declaredBase, section.Result.HeadSha, section.Result.BaseSha, DateTimeOffset.UtcNow), ct)).OrThrow();
    }
    internal async Task ObserveHistoryMetadataAsync(CancellationToken ct)
    {
        if (HistoryGraph is null || _repository is null) return;
        await HistoryGraph.ObserveLocalAsync(_repository, _document?.Stacks ?? [], ct);
        if (_remoteSnapshot is not null) await HistoryGraph.ObserveRemoteAsync(_remoteSnapshot, GitHubPreferences.BoundaryBranches ?? [], ct);
    }
}
