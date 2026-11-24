// Purpose: Repository and stack workspace lifecycle: opening, refreshing, saving definitions, and preserving each stack’s view.
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] internal bool _hasPendingChanges;
    [ObservableProperty] internal bool _isReviewOpen;
    [ObservableProperty] internal ReviewViewModel? _activeReview;
    internal StackGroupViewModel? ActiveStackGroup => _runtime._activeGroup is { IsStack: true } group ? group : null;
    public string? StackPositionLabel => ActiveStackGroup is not { } group ? null
        : SelectedPositions is [var position] ? $"{group.Name} · {position + 1}/{group.Layers.Count}" : group.Summary;
    public string? CheckoutLayer => IsLocalSelected && SelectedPositions is [var position]
        && _runtime._activeGroup?.Layers.ElementAtOrDefault(position) is { LocalBranchName: { } branch, Snapshot.HeadSha: not null }
        && !string.Equals(branch, CheckedOutBranch, StringComparison.Ordinal) ? branch : null;
    public string ComparisonDetail => Sections.Count == 1 ? Sections[0].Comparison : "Each layer is compared with its own parent";
    public string ReviewLabel => CanPresentActiveReview ? ActiveReview!.ReviewLabel : "Review";
    internal bool CanPresentActiveReview => ActiveReview is { } review &&
        SelectedPrForDiscussion?.Number == review.PullRequest.Number &&
        _runtime._remoteSnapshot?.Context.Identity == review.Context.Identity;
    internal bool HasReviewablePrDiff => Sections.Count == 1 && Sections[0] is { IsPrSnapshot: true, Review: { } review } &&
        ReferenceEquals(review, ActiveReview);
    public bool CanReview => HasPrDiscussion;
    public bool IsSingleLayer => Mode == DiffMode.Layer;
    public bool IsThroughLayer => Mode == DiffMode.Cumulative;
    public string GroupKey(StackGroupViewModel group) => _workspaceController.GroupKey(group);
    internal string ViewKey() => _workspaceController.ViewKey();
    /// <summary>Enables or suspends the background GitHub polling loop as the window gains or loses focus.</summary>
    /// <remarks>Activation does not reopen the repository or recreate local diff sections.</remarks>
    public void SetWindowActive(bool active) => _workspaceController.SetWindowActive(active);
    /// <summary>Refreshes remote metadata in the background while retaining the currently displayed snapshot.</summary>
    public Task PollAsync(CancellationToken ct = default) => _workspaceController.PollAsync(ct);
    [RelayCommand] public Task RefreshAllAsync() => _workspaceController.RefreshAllAsync();
    [RelayCommand]
    public async Task UpdateChangesAsync()
    {
        var reopen = ActiveHistory is not null;
        await _workspaceController.UpdateChangesAsync();
        if (reopen)
            await ShowHistoryAsync();
    }
    // A separate state key per repository and stack prevents one stack's file/filter/scroll choices leaking into another.
    internal void CaptureWorkspace() => _workspaceController.CaptureWorkspace();
    /// <summary>Persists the active selection and per-stack view state in the application data directory.</summary>
    public Task SaveWorkspaceAsync() => _workspaceController.SaveWorkspaceAsync();
    public void RestoreSections() => _workspaceController.RestoreSections();
    /// <summary>Opens a stack's aggregate comparison, the top-level action for reviewing the whole change.</summary>
    public async Task SelectOverviewAsync(StackGroupViewModel group)
    {
        await _workspaceController.SelectOverviewAsync(group);
        await SynchronizeHistoryAsync();
    }
    public Task<ReviewViewModel?> EnsureReviewAsync(DiffSectionViewModel section, CancellationToken ct = default) => _workspaceController.EnsureReviewAsync(section, ct);
    public Task ShowReviewAsync(DiffSectionViewModel? section = null, int tab = 3) => _workspaceController.ShowReviewAsync(section, tab);
    partial void OnActiveReviewChanged(ReviewViewModel? value) => OnPropertyChanged(nameof(ReviewLabel));
    [RelayCommand] internal void CloseReview() => _workspaceController.CloseReview();
    public Task FlushReviewsAsync() => _workspaceController.FlushReviewsAsync();

    /// <summary>Best-effort shutdown: persists everything it can, collects what it cannot, and never throws.</summary>
    /// <remarks>The window must always be able to close; a failing save is reported instead of trapping the user.</remarks>
    public Task<IReadOnlyList<string>> ShutdownAsync() => _workspaceController.ShutdownAsync();
    public Task ClearReviewSessionsAsync() => _workspaceController.ClearReviewsAsync();
    public void DisposeReviewSessions() => _workspaceController.DisposeReviews();
}
