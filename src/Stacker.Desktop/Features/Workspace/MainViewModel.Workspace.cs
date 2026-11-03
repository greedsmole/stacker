using Stacker.Core.Framework;
// Purpose: Repository and stack workspace lifecycle: opening, refreshing, saving definitions, and preserving each stack’s view.
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    internal WorkspaceState _workspace = new();
    internal readonly WorkspacePersistence _workspacePersistence;
    internal Task _workspaceSave = Task.CompletedTask;
    internal readonly Dictionary<string, ReviewViewModel> _reviews = [];
    internal readonly LinkedList<string> _reviewOrder = new();
    internal readonly SemaphoreSlim _reviewGate = new(1, 1);
    internal readonly SemaphoreSlim _syncGate = new(1, 1);
    internal string? _displayedStateKey;
    internal bool _applyingChanges;
    internal GitHubSnapshot? _pendingRemote;
    internal bool _pendingLocal;
    internal RefreshCoordinator? _coordinator;
    internal bool _windowActive;
    [ObservableProperty] internal bool _hasPendingChanges;
    [ObservableProperty] internal bool _isReviewOpen;
    [ObservableProperty] internal ReviewViewModel? _activeReview;
    public string ComparisonDetail => Sections.Count == 1 ? Sections[0].Comparison : "Each layer is compared with its own parent";
    public string ReviewLabel => ActiveReview?.ReviewLabel ?? "Review";
    public bool CanReview => HasPrDiscussion;
    public bool IsSingleLayer => Mode == DiffMode.Layer;
    public bool IsThroughLayer => Mode == DiffMode.Cumulative;
    internal string GroupKey(StackGroupViewModel group) => _workspaceController.GroupKey(group);
    internal string ViewKey() => _workspaceController.ViewKey();
    /// <summary>Enables or suspends the background GitHub polling loop as the window gains or loses focus.</summary>
    /// <remarks>Activation does not reopen the repository or recreate local diff sections.</remarks>
    public void SetWindowActive(bool active) => _workspaceController.SetWindowActive(active);
    /// <summary>Refreshes remote metadata in the background while retaining the currently displayed snapshot.</summary>
    public Task PollAsync(CancellationToken ct = default) => _workspaceController.PollAsync(ct);
    [RelayCommand] public Task RefreshAllAsync() => _workspaceController.RefreshAllAsync();
    [RelayCommand] public Task UpdateChangesAsync() => _workspaceController.UpdateChangesAsync();
    // A separate state key per repository and stack prevents one stack's file/filter/scroll choices leaking into another.
    internal void CaptureWorkspace() => _workspaceController.CaptureWorkspace();
    /// <summary>Persists the active selection and per-stack view state in the application data directory.</summary>
    public Task SaveWorkspaceAsync() => _workspaceController.SaveWorkspaceAsync();
    internal void RestoreSections() => _workspaceController.RestoreSections();
    /// <summary>Opens a stack's aggregate comparison, the top-level action for reviewing the whole change.</summary>
    public Task SelectOverviewAsync(StackGroupViewModel group) => _workspaceController.SelectOverviewAsync(group);
    public Task<ReviewViewModel?> EnsureReviewAsync(DiffSectionViewModel section, CancellationToken ct = default) => _workspaceController.EnsureReviewAsync(section, ct);
    public Task ShowReviewAsync(DiffSectionViewModel? section = null, int tab = 3) => _workspaceController.ShowReviewAsync(section, tab);
    partial void OnActiveReviewChanged(ReviewViewModel? value) => OnPropertyChanged(nameof(ReviewLabel));
    [RelayCommand] internal void CloseReview() => _workspaceController.CloseReview();
    public Task FlushReviewsAsync() => _workspaceController.FlushReviewsAsync();

    /// <summary>Best-effort shutdown: persists everything it can, collects what it cannot, and never throws.</summary>
    /// <remarks>The window must always be able to close; a failing save is reported instead of trapping the user.</remarks>
    public Task<IReadOnlyList<string>> ShutdownAsync() => _workspaceController.ShutdownAsync();
    internal Task ClearReviewSessionsAsync() => _workspaceController.ClearReviewsAsync();
    internal void DisposeReviewSessions() => _workspaceController.DisposeReviews();
}
