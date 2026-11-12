using Stacker.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private HistoryWorkspaceViewModel? _activeHistory;
    private string? _historyGroup;
    private int _historySelectionsInProgress;
    private int _checkpointRevision;
    public bool IsHistoryOpen => ActiveHistory is not null;
    partial void OnActiveHistoryChanged(HistoryWorkspaceViewModel? value) => OnPropertyChanged(nameof(IsHistoryOpen));
    public async Task ShowHistoryAsync()
    {
        var filePath = Sections.Count == 1 ? Sections[0].SelectedFile?.NewPath : null;
        var next = await OpenHistoryAsync();
        if (next is null) return;
        ActiveHistory?.Dispose(); ActiveHistory = next; _historyGroup = _runtime._activeGroup?.Definition.Id;
        next.SelectionChanged = async (node, file) =>
        {
            if (_runtime._activeGroup is not { } group) return;
            var token = next.LayerLoadToken;
            _historySelectionsInProgress++;
            try
            {
                var layer = group.Layers.FirstOrDefault(l => node.PullRequest is not null ? l.PullRequest?.Number == node.PullRequest : l.Snapshot.Branch == node.Reference);
                if (layer is not null && (!layer.IsSelected || _runtime._comparison is not null ||
                    _runtime._selectedPositions.Length != 1 || _runtime._selectedPositions[0] != layer.Snapshot.Position))
                    await _navigationController.SelectLayerAsync(layer, false, token);
                if (token.IsCancellationRequested || ActiveHistory != next || next.SelectedNode != node || _runtime._activeGroup != group) return;
                var section = Sections.FirstOrDefault();
                if (section is not null && file is not null) section.SelectedFile = section.Files.FirstOrDefault(f => f.NewPath == file.NewPath);
                _runtime.Session.Select(new(node.Id, section is null ? null : ComparisonId.For(node.Id, section.Result.BaseSha, section.Result.HeadSha).Value, file?.NewPath));
            }
            finally { _historySelectionsInProgress--; }
        };
        next.CheckpointChanged = async () =>
        {
            _checkpointRevision++;
            foreach (var section in Sections.ToArray()) await RefreshFileReviewStatusesAsync(section);
        };
        var selected = _runtime._selectedPositions.FirstOrDefault();
        if (selected < next.Nodes.Count)
        {
            next.SelectedNode = next.Nodes[selected];
            await next.LoadCurrentDiffAsync(filePath);
        }
    }
    [RelayCommand] private void CloseHistory() { ActiveHistory?.Dispose(); ActiveHistory = null; }
    private async Task SynchronizeHistoryAsync()
    {
        if (ActiveHistory is not { } history || _historySelectionsInProgress > 0) return;
        if (_historyGroup != _runtime._activeGroup?.Definition.Id) { await ShowHistoryAsync(); return; }
        var position = _runtime._selectedPositions.FirstOrDefault();
        if (position >= history.Nodes.Count || history.SelectedNode == history.Nodes[position]) return;
        history.SelectedNode = history.Nodes[position];
        await history.LoadCurrentDiffAsync(Sections.Count == 1 ? Sections[0].SelectedFile?.NewPath : null);
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
            : null, _runtime._highlighter, Settings.Theme) { WrapCode = Settings.WrapCode };
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
        section.ReviewVersion = (await _runtime.HistoryStore.ObserveAsync(new("", node.Id, declaredBase, section.Result.HeadSha, section.Result.BaseSha, DateTimeOffset.UtcNow), ct)).OrThrow();
        section.ReviewScope = scope;
        try { await RefreshFileReviewStatusesAsync(section, ct); }
        catch (StackerException ex) { Error = "Cannot load file review status: " + ex.Message; }
    }
    private async Task RefreshFileReviewStatusesAsync(DiffSectionViewModel section, CancellationToken ct = default)
    {
        if (_runtime.HistoryProgress is not { } progress || _runtime._repository?.Identity is not { } repository ||
            section.ReviewVersion is not { } version || section.ReviewScope is not { } scope) return;
        while (true)
        {
            var revision = _checkpointRevision;
            var statuses = await progress.StatusAsync(repository, scope, version, section.Result.Files, ct);
            ct.ThrowIfCancellationRequested();
            if (revision != _checkpointRevision) continue;
            section.SetReviewStatuses(statuses);
            return;
        }
    }
    internal async Task ToggleFileReviewedAsync(DiffSectionViewModel section, DiffFileRow row)
    {
        if (!Sections.Contains(section) || !row.CanChangeReview || _runtime.HistoryStore is not { } store ||
            section.ReviewVersion is not { } version || row.ReviewStatus is not { } status) return;
        var remove = row.IsReviewed;
        row.SavingReview = true;
        try
        {
            (await store.SetCheckpointAsync(new(version.ChangeId, row.Change.NewPath, version.Id, DateTimeOffset.UtcNow), remove)).OrThrow();
            _checkpointRevision++;
            row.ReviewStatus = status with
            {
                State = remove ? FileReviewState.Unreviewed : FileReviewState.Reviewed,
                CheckedVersion = remove ? null : version
            };
            if (ActiveHistory is { } history) await history.RefreshFileReviewStatusesAsync();
            foreach (var active in Sections.Where(view => view != section).ToArray()) await RefreshFileReviewStatusesAsync(active);
        }
        finally { row.SavingReview = false; }
    }
    internal async Task ObserveHistoryMetadataAsync(CancellationToken ct)
    {
        if (_runtime.HistoryGraph is null || _runtime._repository is null) return;
        await _runtime.HistoryGraph.ObserveLocalAsync(_runtime._repository, _runtime._document?.Stacks ?? [], ct);
        if (_runtime._remoteSnapshot is not null) await _runtime.HistoryGraph.ObserveRemoteAsync(_runtime._remoteSnapshot, GitHubPreferences.BoundaryBranches ?? [], ct);
    }
}
