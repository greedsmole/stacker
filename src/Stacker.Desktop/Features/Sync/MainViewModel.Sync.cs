// Purpose: Explicit, previewed stack writes: restack, publish and undo, with progress and no automatic action.
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] internal bool _isSyncing;
    [ObservableProperty] internal string _syncProgress = "";
    /// <summary>The restack entry point is offered only for a local stack that can be rewritten.</summary>
    public bool? UndoAvailable => _runtime._activeGroup?.UndoAvailable ?? false;
    public bool CanRestackActiveStack => _runtime._repository is not null && _runtime._activeGroup is { IsRemote: false } && !IsSyncing;
    partial void OnIsSyncingChanged(bool value) => OnPropertyChanged(nameof(CanRestackActiveStack));

    /// <summary>Builds the preview for the active local stack without touching a single ref.</summary>
    public Task<RestackPlan?> BuildRestackPlanAsync(bool preserveMerges = false) => _syncController.BuildRestackPlanAsync(preserveMerges);

    /// <summary>Rebases the stack and reloads the workspace so every layer shows its new commits.</summary>
    public Task<RestackOutcome?> RestackAsync(RestackPlan plan, IProgress<string>? progress = null) => _syncController.RestackAsync(plan, progress);

    /// <summary>Pushes the stack: the rewritten layers under a lease, the rest only when a force is genuinely needed.</summary>
    public Task<(bool Ok, string Message)> PushAsync(RestackPlan plan, bool wholeStack) => _syncController.PushAsync(plan, wholeStack);

    /// <summary>Two plans are the same when every layer needs the same action on the same pull request.</summary>
    internal static bool SameActions(PullRequestPublicationPlan approved, PullRequestPublicationPlan current) =>
        approved.StackId == current.StackId && approved.Steps.SequenceEqual(current.Steps);

    /// <summary>Restores the branches of the last restack; the remote is left untouched.</summary>
    public Task<(bool Ok, string Message)> UndoAsync() => _syncController.UndoAsync();

    /// <summary>Creates a new layer branch at the chosen start point and appends it to the stack definition.</summary>
    /// <remarks>Stacker creates the branch and checks it out; committing there stays with the user.</remarks>
    public Task<(bool Ok, string Message)> AddLayerAsync(string stackId, string branchName, string startRef) => _syncController.AddLayerAsync(stackId, branchName, startRef);

    /// <summary>Reads the live open pull requests and reports what each layer needs; nothing is written.</summary>
    public Task<(PullRequestPublicationPlan? Plan, string Message)> CheckPullRequestsAsync(string stackId) => _syncController.CheckPullRequestsAsync(stackId);

    /// <summary>Creates and retargets exactly the pull requests the checked plan listed, one layer at a time.</summary>
    public Task<(bool Ok, string Message)> PublishPullRequestsAsync(PullRequestPublicationPlan plan) => _syncController.PublishPullRequestsAsync(plan);

    /// <summary>Offers undo only for the stack whose write is still the newest one.</summary>
    public Task RefreshSyncAvailabilityAsync(CancellationToken ct = default) => _syncController.RefreshSyncAvailabilityAsync(ct);

    /// <summary>Applies a rewritten snapshot immediately instead of behind the pending-changes prompt.</summary>
    public Task ReloadAsync() => _syncController.ReloadAsync();
}
