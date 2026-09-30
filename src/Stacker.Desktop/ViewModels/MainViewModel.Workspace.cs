using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed class WorkspaceState
{
    public string? ActiveGroup { get; set; }
    public Dictionary<string, StackViewState> Stacks { get; set; } = [];
}
public sealed class StackViewState
{
    public DiffMode Mode { get; set; } = DiffMode.FullStack;
    public string[] Branches { get; set; } = [];
    public bool Expanded { get; set; } = true;
    public Dictionary<string, SectionViewState[]> Comparisons { get; set; } = [];
}
public sealed record SectionViewState(string Layer, string? File, string Filter, double X, double Y, bool Expanded);

public sealed partial class MainViewModel
{
    private WorkspaceState _workspace = new();
    private Task _workspaceSave = Task.CompletedTask;
    private readonly Dictionary<string, ReviewSession> _reviews = [];
    private readonly SemaphoreSlim _reviewGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private string? _displayedStateKey;
    private bool _applyingChanges;
    private GitHubSnapshot? _pendingRemote;
    private bool _pendingLocal;
    private RefreshCoordinator? _coordinator;
    private bool _windowActive;
    [ObservableProperty] private bool _hasPendingChanges;
    [ObservableProperty] private bool _isReviewOpen;
    [ObservableProperty] private ReviewViewModel? _activeReview;
    public string ComparisonDetail => Sections.Count == 1 ? Sections[0].Comparison : "Each layer is compared with its own parent";
    public string ReviewLabel => ActiveReview?.ReviewLabel ?? "Review";
    public bool CanReview => HasPrDiscussion;
    public bool IsSingleLayer => Mode == DiffMode.Layer;
    public bool IsThroughLayer => Mode == DiffMode.Cumulative;
    private string GroupKey(StackGroupViewModel group) => (group.IsRemote ? "github:" + _remoteSnapshot?.Context.Identity : "local") + ":" + group.Definition.Id;
    private string ViewKey() => Mode + ":" + string.Join("|", _activeGroup?.Layers.Where(l => _selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch) ?? []);
    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        _coordinator ??= new(PollAsync);
        _coordinator.SetActive(active && Settings.BackgroundRefresh && !IsDemo && _repository is not null);
    }
    public async Task PollAsync(CancellationToken ct = default)
    {
        if (_repository is null || IsDemo || !_syncGate.Wait(0)) return;
        try
        {
            await RefreshGitHubCoreAsync(ct, background: true);
            if ((_pendingRemote is null || _pendingRemote.Context.Identity == _remoteSnapshot?.Context.Identity) && ActiveReview is { IsBusy: false } review) await review.RefreshDiscussionAsync(ct);
        }
        finally { _syncGate.Release(); }
    }
    [RelayCommand] public async Task RefreshAllAsync()
    {
        if (!_syncGate.Wait(0)) return;
        try
        {
            _coordinator?.Reset();
            await RefreshAsync(); await RefreshGitHubCoreAsync(default, background: false);
            if (ActiveReview is { IsBusy: false } review) await review.ReloadAsync();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { _syncGate.Release(); SetWindowActive(_windowActive); }
    }
    [RelayCommand] public async Task UpdateChangesAsync()
    {
        if (_reviews.Values.Any(r => r.IsBusy)) { Error = "Wait for the current review operation to finish. Your draft is safe."; return; }
        await SaveWorkspaceAsync();
        _applyingChanges = true;
        try
        {
            if (_pendingLocal) { _pendingLocal = false; await RefreshAsync(); if (HasError) _pendingLocal = true; }
            if (_pendingRemote is { } snapshot)
            {
                _pendingRemote = null; _remoteSnapshot = snapshot; BuildDiscoveredGroups();
                if (_activeGroup is not null) await CompareAsync();
                if (HasError) _pendingRemote = snapshot;
            }
            HasPendingChanges = _pendingLocal || _pendingRemote is not null;
        }
        finally { _applyingChanges = false; }
    }
    private void CaptureWorkspace()
    {
        foreach (var group in LocalGroups.Concat(RemoteGroups).Concat(OtherGroups))
        {
            var key = GroupKey(group);
            if (!_workspace.Stacks.TryGetValue(key, out var state)) _workspace.Stacks[key] = state = new();
            state.Expanded = group.IsExpanded;
        }
        if (_activeGroup is null) return;
        var activeKey = GroupKey(_activeGroup);
        var active = _workspace.Stacks[activeKey];
        _workspace.ActiveGroup = activeKey;
        if (_displayedStateKey is not null)
            active.Comparisons[_displayedStateKey] = Sections.Select(s => new SectionViewState(s.Result.Layer, s.SelectedFile?.NewPath, s.Filter, s.ScrollX, s.ScrollY, s.IsExpanded)).ToArray();
    }
    public async Task SaveWorkspaceAsync()
    {
        if (_repository is null) return;
        CaptureWorkspace();
        var root = _repository.Root;
        var copy = JsonSerializer.Deserialize<WorkspaceState>(JsonSerializer.Serialize(_workspace))!;
        var previous = _workspaceSave;
        _workspaceSave = SaveAsync();
        await _workspaceSave;
        async Task SaveAsync()
        {
            await previous;
            try { await _applicationStore.WriteAsync("workspace", root, copy); }
            catch (Exception ex) { Error = "Cannot save workspace: " + ex.Message; }
        }
    }
    private void RestoreSections()
    {
        if (_activeGroup is null) return;
        var key = GroupKey(_activeGroup);
        if (!_workspace.Stacks.TryGetValue(key, out var state)) _workspace.Stacks[key] = state = new();
        _displayedStateKey = ViewKey(); state.Mode = Mode;
        state.Branches = _activeGroup.Layers.Where(l => _selectedPositions.Contains(l.Snapshot.Position)).Select(l => l.Snapshot.Branch).ToArray();
        if (state.Comparisons.TryGetValue(_displayedStateKey, out var saved))
            foreach (var section in Sections)
                if (saved.FirstOrDefault(s => s.Layer == section.Result.Layer) is { } view)
                {
                    section.Filter = view.Filter;
                    section.SelectedFile = section.Files.FirstOrDefault(f => f.NewPath == view.File) ?? section.Files.FirstOrDefault();
                    section.ScrollX = view.X; section.ScrollY = view.Y; section.IsExpanded = view.Expanded;
                }
    }
    public async Task SelectOverviewAsync(StackGroupViewModel group)
    {
        await SaveWorkspaceAsync(); SetActiveGroup(group); _selectedPositions = [];
        _suppress = true; Mode = DiffMode.FullStack; _suppress = false; await CompareAsync();
    }
    public async Task<ReviewViewModel?> EnsureReviewAsync(DiffSectionViewModel section, CancellationToken ct = default)
    {
        if (section.AssociatedPr is not { } pr || section.GitHubContext is not { } context || _github is null || _writer is null) return null;
        await _reviewGate.WaitAsync(ct);
        try
        {
            var key = context.Identity + "/" + pr.Number;
            if (!_reviews.TryGetValue(key, out var session))
            {
                session = new(_github, _writer, _applicationStore, _git, context, pr, section, IsDemo);
                await session.InitializeAsync(ct: ct); ct.ThrowIfCancellationRequested();
                _reviews[key] = session;
                session.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ReviewViewModel.ReviewLabel)) OnPropertyChanged(nameof(ReviewLabel)); };
            }
            session.Attach(section); return session;
        }
        finally { _reviewGate.Release(); }
    }
    public async Task ShowReviewAsync(DiffSectionViewModel? section = null, int tab = 3)
    {
        section ??= Sections.FirstOrDefault(s => s.IsPrSnapshot) ?? CreateDiscussionContext();
        if (section is null) return;
        try { ActiveReview = await EnsureReviewAsync(section); }
        catch (Exception ex) { Error = ex.Message; return; }
        if (ActiveReview is not null) { ActiveReview.PanelTab = tab; if (tab == 0) ActiveReview.BeginPrComment(); IsReviewOpen = true; }
    }
    partial void OnActiveReviewChanged(ReviewViewModel? value) => OnPropertyChanged(nameof(ReviewLabel));
    [RelayCommand] private void CloseReview() => IsReviewOpen = false;
    public async Task FlushReviewsAsync() { foreach (var review in _reviews.Values) await review.FlushAsync(); }
}
