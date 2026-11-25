using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private HistoryWorkspaceViewModel? _activeHistory;
    [ObservableProperty] private bool _isHistoryOpen;
    [ObservableProperty] private bool _isHistoryLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHistoryError))] private string _historyError = "";
    public bool HasHistoryError => HistoryError.Length > 0;
    private CancellationTokenSource? _historyOpen;
    private StackGroupViewModel? _historyGroup;
    private int _historySelectionsInProgress;
    private int _checkpointRevision;
    [RelayCommand]
    public async Task ShowHistoryAsync()
    {
        if (HasPendingChanges)
        {
            Error = "Apply Update changes before opening review history.";
            return;
        }
        _historyOpen?.Cancel();
        using var request = new CancellationTokenSource();
        _historyOpen = request;
        var group = _runtime._activeGroup;
        var selected = _runtime._selectedPositions.FirstOrDefault();
        var filePath = Sections.Count == 1 ? Sections[0].SelectedFile?.NewPath : null;
        IsHistoryOpen = true;
        IsHistoryLoading = true;
        HistoryError = "";
        ActiveHistory?.Dispose();
        ActiveHistory = null;
        _historyGroup = group;
        try
        {
            var next = await CreateHistoryAsync(request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(group, _runtime._activeGroup))
            {
                next.Dispose();
                return;
            }
            ConnectHistory(next);
            ActiveHistory = next;
            IsHistoryLoading = false;
            next.SelectedNode = next.Nodes.ElementAtOrDefault(selected) ?? next.Nodes.FirstOrDefault();
            await next.LoadCurrentDiffAsync(filePath, synchronize: false);
            if (!request.IsCancellationRequested)
                _ = next.LoadHistoryDetailsAsync();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) HistoryError = ex.Message; }
        finally
        {
            if (ReferenceEquals(_historyOpen, request))
            {
                _historyOpen = null;
                IsHistoryLoading = false;
            }
        }
    }

    private void ConnectHistory(HistoryWorkspaceViewModel next)
    {
        next.SelectionChanged = async (node, file) =>
        {
            if (!ReferenceEquals(ActiveHistory, next) || _runtime._activeGroup is not { } group)
                return;
            var token = next.LayerLoadToken;
            _historySelectionsInProgress++;
            try
            {
                var layer = group.Layers.FirstOrDefault(l => node.PullRequest is not null ? l.PullRequest?.Number == node.PullRequest : l.Snapshot.Branch == node.Reference);
                if (layer is not null && (!layer.IsSelected ||
                    _runtime._selectedPositions.Length != 1 || _runtime._selectedPositions[0] != layer.Snapshot.Position))
                    await _navigationController.SelectLayerAsync(layer, false, token);
                if (token.IsCancellationRequested || ActiveHistory != next || next.SelectedNode != node || _runtime._activeGroup != group)
                    return;
                var section = Sections.FirstOrDefault();
                if (section is not null && file is not null)
                    section.SelectedFile = section.Result.Files.FirstOrDefault(f => f.NewPath == file.NewPath);
                _runtime.Session.Select(new(node.Id, section is null ? null : ComparisonId.For(node.Id, section.Result.BaseSha, section.Result.HeadSha).Value, file?.NewPath));
            }
            finally { _historySelectionsInProgress--; }
        };
        next.CheckpointChanged = async () =>
        {
            _checkpointRevision++;
            foreach (var section in Sections.ToArray())
                await RefreshFileReviewStatusesAsync(section);
        };
    }
    [RelayCommand]
    private void CloseHistory()
    {
        _historyOpen?.Cancel();
        _historyOpen = null;
        ActiveHistory?.Dispose();
        ActiveHistory = null;
        IsHistoryOpen = false;
        IsHistoryLoading = false;
        HistoryError = "";
    }
    private async Task SynchronizeHistoryAsync()
    {
        if (!IsHistoryOpen || _historySelectionsInProgress > 0)
            return;
        if (ActiveHistory is not { } history || !ReferenceEquals(_historyGroup, _runtime._activeGroup))
        {
            await ShowHistoryAsync();
            return;
        }
        var position = _runtime._selectedPositions.FirstOrDefault();
        if (position >= history.Nodes.Count || history.SelectedNode == history.Nodes[position])
            return;
        history.SelectedNode = history.Nodes[position];
        await history.LoadCurrentDiffAsync(Sections.Count == 1 ? Sections[0].SelectedFile?.NewPath : null);
    }
    public async Task<HistoryWorkspaceViewModel?> OpenHistoryAsync(CancellationToken ct = default)
    {
        HistoryWorkspaceViewModel? view = null;
        try
        {
            view = await CreateHistoryAsync(ct);
            using var cancellation = ct.Register(view.CancelLoads);
            await view.InitializeAsync();
            ct.ThrowIfCancellationRequested();
            return view;
        }
        catch (OperationCanceledException) { view?.Dispose(); throw; }
        catch (Exception ex) { view?.Dispose(); Error = ex.Message; return null; }
    }
    public async Task CaptureHistorySectionAsync(DiffSectionViewModel section, CancellationToken ct)
    {
        if (_runtime.HistoryStore is null || _runtime.HistoryObjects is null || _runtime._repository is not { } repo)
            return;
        var remote = section.IsPrSnapshot && section.AssociatedPr is not null && section.GitHubContext is not null;
        if (!remote && _runtime._document is { } document)
            await _runtime.ObserveLocalHistoryAsync(repo, document, ct, _runtime._activeGroup is { UsesGhStack: true } group ? group.Definition : null);
        ct.ThrowIfCancellationRequested();
        var scope = remote ? section.GitHubContext!.Identity : repo.Scope;
        var node = (await _runtime.HistoryStore.ResolveNodeAsync(scope, remote ? section.AssociatedPr!.HeadRef : section.Result.Layer, remote ? section.AssociatedPr!.Title : section.Title,
            remote ? section.AssociatedPr!.Number : null, ct)).OrThrow();
        var declaredBase = remote ? section.AssociatedPr!.BaseSha : repo.Refs.FirstOrDefault(r => r.Name == section.Result.ParentRef)?.Sha ?? section.Result.BaseSha;
        (await _runtime.HistoryObjects.CaptureAsync(repo.Identity!, scope, [declaredBase, section.Result.HeadSha], ct)).OrThrow();
        section.ReviewVersion = (await _runtime.HistoryStore.ObserveAsync(new("", node.Id, declaredBase, section.Result.HeadSha, section.Result.BaseSha, DateTimeOffset.UtcNow), ct)).OrThrow();
        section.ReviewScope = scope;
        try
        {
            await RefreshFileReviewStatusesAsync(section, ct);
        }
        catch (StackerException ex) { Error = "Cannot load file review status: " + ex.Message; }
    }
    private async Task RefreshFileReviewStatusesAsync(DiffSectionViewModel section, CancellationToken ct = default)
    {
        if (_runtime.HistoryProgress is not { } progress || _runtime._repository?.Identity is not { } repository ||
            section.ReviewVersion is not { } version || section.ReviewScope is not { } scope)
            return;
        while (true)
        {
            var revision = _checkpointRevision;
            var statuses = await progress.StatusAsync(repository, scope, version, section.Result.Files, ct);
            ct.ThrowIfCancellationRequested();
            if (revision != _checkpointRevision)
                continue;
            section.SetReviewStatuses(statuses);
            return;
        }
    }
    internal async Task ToggleFileReviewedAsync(DiffSectionViewModel section, DiffFileRow row)
    {
        if (!Sections.Contains(section) || !row.CanChangeReview || _runtime.HistoryStore is not { } store ||
            section.ReviewVersion is not { } version || row.ReviewStatus is not { } status)
            return;
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
            if (ActiveHistory is { } history)
                await history.RefreshFileReviewStatusesAsync();
            foreach (var active in Sections.Where(view => view != section).ToArray())
                await RefreshFileReviewStatusesAsync(active);
        }
        finally { row.SavingReview = false; }
    }
    internal async Task ObserveHistoryMetadataAsync(CancellationToken ct)
    {
        if (_runtime.HistoryGraph is null || _runtime._repository is null)
            return;
        await _runtime.ObserveLocalHistoryAsync(_runtime._repository, _runtime._document ?? new([], null), ct);
        if (_runtime._remoteSnapshot is not null)
            await _runtime.HistoryGraph.ObserveRemoteAsync(_runtime._remoteSnapshot, GitHubPreferences.BoundaryBranches ?? [], ct);
    }
}
