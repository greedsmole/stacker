// Purpose: The explicit stack actions the sync window can run; the view model implements it and tests can fake it.
using Stacker.Core;
namespace Stacker.Desktop;

public interface ISyncActions
{
    /// <summary>What the working tree holds right now, including any unfinished operation.</summary>
    WorkingTreeState? WorkingTree { get; }
    /// <summary>True while a stash taken by Stacker is waiting to be restored.</summary>
    bool CanRestoreStash { get; }
    /// <summary>Rebuilds the preview, optionally preserving merge commits.</summary>
    Task<RestackPlan?> BuildRestackPlanAsync(bool preserveMerges);
    /// <summary>Rebases the stack; a conflict stops and leaves the rebase in progress.</summary>
    Task<RestackOutcome?> RestackAsync(RestackPlan plan, IProgress<string>? progress);
    /// <summary>Pushes the stack's branches, the rewritten ones under a lease.</summary>
    Task<(bool Ok, string Message)> PushAsync(RestackPlan plan, bool wholeStack);
    Task<(bool Ok, string Message)> UndoAsync();
    Task<(PullRequestPublicationPlan? Plan, string Message)> CheckPullRequestsAsync(string stackId);
    Task<(bool Ok, string Message)> PublishPullRequestsAsync(PullRequestPublicationPlan plan);
    /// <summary>Opens the conflicted files in the editor the user chose.</summary>
    Task<(bool Ok, string Message)> OpenConflictsInEditorAsync();
    Task<(bool Ok, string Message)> StageConflictsAsync();
    Task<(bool Ok, string Message)> ContinueOperationAsync();
    Task<(bool Ok, string Message)> SkipCommitAsync();
    Task<(bool Ok, string Message)> AbortOperationAsync();
    Task<(bool Ok, string Message)> StashAndRestackAsync(RestackPlan plan);
    Task<(bool Ok, string Message)> RestoreStashAsync();
    Task<(bool Ok, string Message)> SwitchToBranchAsync(string branch);
    Task<(bool Ok, string Message)> CommitAsync(string message, bool stageAll);
    Task<(bool Ok, string Message)> RefreshWorkingTreeAsync(CancellationToken ct = default);
}
