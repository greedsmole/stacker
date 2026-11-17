// Purpose: The explicit stack actions the sync window can run; the view model implements it and tests can fake it.
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop;

/// <summary>Explicit stack and working-tree actions exposed by the sync window.</summary>
/// <remarks>Mutations are user-invoked; preview methods return plans before refs move and outcomes retain recovery messages.</remarks>
public interface ISyncActions
{
    /// <summary>What the working tree holds right now, including any unfinished operation.</summary>
    WorkingTreeState? WorkingTree
    {
        get;
    }
    /// <summary>True while a stash taken by Stacker is waiting to be restored.</summary>
    bool CanRestoreStash
    {
        get;
    }
    /// <summary>Whether the active stack has an eligible local undo.</summary>
    bool? UndoAvailable => null;
    /// <summary>Stashes recorded for this repository and available to restore.</summary>
    IReadOnlyList<SavedStash> SavedStashes => [];
    /// <summary>Rebuilds the preview, optionally preserving merge commits.</summary>
    Task<RestackPlan?> BuildRestackPlanAsync(bool preserveMerges);
    /// <summary>Rebases the stack; a conflict stops and leaves the rebase in progress.</summary>
    Task<RestackOutcome?> RestackAsync(RestackPlan plan, IProgress<string>? progress);
    /// <summary>Pushes the stack's branches, the rewritten ones under a lease.</summary>
    Task<(bool Ok, string Message)> PushAsync(RestackPlan plan, bool wholeStack);
    Task<(bool Ok, string Message)> UndoAsync();
    /// <summary>Checks live pull requests and returns a publication plan without writing.</summary>
    Task<(PullRequestPublicationPlan? Plan, string Message)> CheckPullRequestsAsync(string stackId);
    /// <summary>Publishes exactly the previously checked plan.</summary>
    Task<(bool Ok, string Message)> PublishPullRequestsAsync(PullRequestPublicationPlan plan);
    /// <summary>Opens the conflicted files in the editor the user chose.</summary>
    Task<(bool Ok, string Message)> OpenConflictsInEditorAsync();
    Task<(bool Ok, string Message)> StageConflictsAsync();
    Task<(bool Ok, string Message)> ContinueOperationAsync();
    Task<(bool Ok, string Message)> SkipCommitAsync();
    Task<(bool Ok, string Message)> AbortOperationAsync();
    Task<(bool Ok, string Message)> StashAndRestackAsync(RestackPlan plan);
    Task<(bool Ok, string Message)> RestoreStashAsync();
    Task<(bool Ok, string Message)> RestoreStashAsync(string sha) => RestoreStashAsync();
    Task<(bool Ok, string Message)> SwitchToBranchAsync(string branch);
    Task<(bool Ok, string Message)> CommitAsync(string message, bool stageAll);
    Task<(bool Ok, string Message)> RefreshWorkingTreeAsync(CancellationToken ct = default);
}
