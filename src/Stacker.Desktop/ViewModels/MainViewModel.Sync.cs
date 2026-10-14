// Purpose: Explicit, previewed stack writes: restack, publish and undo, with progress and no automatic action.
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IGitBranchWriter? _branchWriter;
    private IGitHubPullRequestWriter? _pullRequestWriter;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _syncProgress = "";
    /// <summary>The restack entry point is offered only for a local stack that can be rewritten.</summary>
    public bool CanRestackActiveStack => _repository is not null && _activeGroup is { IsRemote: false } && !IsSyncing;
    partial void OnIsSyncingChanged(bool value) => OnPropertyChanged(nameof(CanRestackActiveStack));

    /// <summary>Builds the preview for the active local stack without touching a single ref.</summary>
    public async Task<RestackPlan?> BuildRestackPlanAsync(bool preserveMerges = false)
    {
        if (_branchWriter is null || _repository is null) { Error = "Open a repository first."; return null; }
        if (_activeGroup is not { IsRemote: false } group) { Error = "Select a local stack to restack. GitHub stacks use the server's own branches."; return null; }
        IsSyncing = true; Error = "";
        try
        {
            // A preview must describe the repository as it is now, never the last displayed snapshot.
            if (!HasPendingChanges) await RefreshAsync();
            if (HasPendingChanges) await UpdateChangesAsync();
            if (HasPendingChanges || _repository is null)
            { Error = "The repository changed and could not be reloaded. Refresh, then open the restack preview again."; return null; }
            return await _branchWriter.PlanAsync(_repository, (LocalGroups.FirstOrDefault(g => g.Definition.Id == group.Definition.Id) ?? group).Definition, preserveMerges);
        }
        catch (Exception ex) { Error = ex.Message; return null; }
        finally { IsSyncing = false; }
    }

    /// <summary>Rebases the stack and reloads the workspace so every layer shows its new commits.</summary>
    public async Task<RestackOutcome?> RestackAsync(RestackPlan plan, IProgress<string>? progress = null)
    {
        if (_branchWriter is null || IsSyncing) return null;
        IsSyncing = true; Error = "";
        try
        {
            var outcome = await _branchWriter.RestackAsync(plan, progress);
            Status = outcome.Message;
            // A conflict is the user's to resolve, so it is reported in the conflict panel rather than as a failure.
            if (!outcome.Applied && !outcome.Conflicted) Error = outcome.Message;
            if (outcome.Applied || outcome.Conflicted || outcome.RolledBack) await ReloadAsync();
            return outcome;
        }
        catch (Exception ex) { Error = ex.Message; return new(RestackStatus.Refused, null, [], ex.Message, null); }
        finally { IsSyncing = false; }
    }

    /// <summary>Pushes the stack: the rewritten layers under a lease, the rest only when a force is genuinely needed.</summary>
    public async Task<(bool Ok, string Message)> PushAsync(RestackPlan plan, bool wholeStack)
    {
        if (_branchWriter is null || IsSyncing) return (false, "A stack operation is already running.");
        IsSyncing = true; Error = "";
        try
        {
            var pushed = wholeStack ? await _branchWriter.PushStackAsync(plan) : await _branchWriter.PushAsync(plan);
            var message = $"Pushed {string.Join(", ", pushed)}. The pull requests now point at the current commits.";
            Status = message;
            await RefreshGitHubAsync();
            return (true, message);
        }
        catch (Exception ex) { Error = ex.Message; return (false, "Nothing was pushed. " + ex.Message); }
        finally { IsSyncing = false; }
    }

    /// <summary>Two plans are the same when every layer needs the same action on the same pull request.</summary>
    private static bool SameActions(PullRequestPublicationPlan approved, PullRequestPublicationPlan current) =>
        approved.Steps.Count == current.Steps.Count &&
        approved.Steps.Zip(current.Steps).All(pair => pair.First.Action == pair.Second.Action && pair.First.Number == pair.Second.Number);

    /// <summary>Restores the branches of the last restack; the remote is left untouched.</summary>
    public async Task<(bool Ok, string Message)> UndoAsync()
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        IsSyncing = true; Error = "";
        try
        {
            var outcome = await _branchWriter.UndoLastAsync(_repository.Root);
            Status = outcome.Message;
            await ReloadAsync();
            return (true, outcome.Message);
        }
        catch (Exception ex) { Error = ex.Message; return (false, "Nothing was changed. " + ex.Message); }
        finally { IsSyncing = false; }
    }

    /// <summary>Creates a new layer branch at the chosen start point and appends it to the stack definition.</summary>
    /// <remarks>Stacker creates the branch and checks it out; committing there stays with the user.</remarks>
    public async Task<(bool Ok, string Message)> AddLayerAsync(string stackId, string branchName, string startRef)
    {
        if (_branchWriter is null || _repository is null) return (false, "Open a repository first.");
        if (IsSyncing) return (false, "A stack operation is already running.");
        if (LocalGroups.FirstOrDefault(g => g.Definition.Id == stackId) is not { } group)
            return (false, "This stack is no longer available. Refresh and try again.");
        IsSyncing = true; Error = "";
        try
        {
            var reference = await _branchWriter.CreateLayerAsync(_repository.Root, branchName, startRef);
            var updated = group.Definition with { Branches = [.. group.Definition.Branches, reference] };
            if (!await SaveStackAsync(updated))
                return (false, $"{StackRefs.Short(reference)} was created and checked out, but the stack definition could not be saved: {Error}");
            var message = $"{StackRefs.Short(reference)} was created from {StackRefs.Short(startRef)} and added as layer {updated.Branches.Count}. Commit there whenever you are ready.";
            Status = message;
            return (true, message);
        }
        catch (Exception ex) { Error = ex.Message; return (false, ex.Message); }
        finally { IsSyncing = false; }
    }

    /// <summary>Reads the live open pull requests and reports what each layer needs; nothing is written.</summary>
    public async Task<(PullRequestPublicationPlan? Plan, string Message)> CheckPullRequestsAsync(string stackId)
    {
        if (IsDemo) return (null, "The demo is offline. Publishing pull requests is disabled.");
        if (_github is null || _pullRequestWriter is null) return (null, "Connect GitHub to publish pull requests.");
        if (_remoteSnapshot is null || _repository is null) return (null, "Refresh GitHub, then check the stack again.");
        if (IsSyncing) return (null, "A stack operation is already running.");
        if (LocalGroups.FirstOrDefault(g => g.Definition.Id == stackId) is not { } group)
            return (null, "This stack is no longer available. Close the preview and open it again.");
        IsSyncing = true; Error = "";
        try
        {
            var context = _remoteSnapshot.Context;
            await _github.VerifyAccessAsync(context);
            var snapshot = await _github.SnapshotAsync(context);
            var plan = PullRequestPlanner.Plan(group.Definition, snapshot.PullRequests, context.RepositoryId);
            var message = plan.Blockers.Count > 0 ? string.Join(" ", plan.Blockers)
                : plan.PendingCount == 0 ? "Every layer already has an open pull request into its parent."
                : $"{plan.Steps.Count(step => step.Action == PullRequestAction.Create)} to create, {plan.Steps.Count(step => step.Action == PullRequestAction.UpdateBase)} to retarget.";
            return (plan, message);
        }
        catch (Exception ex) { Error = ex.Message; return (null, "Could not read the pull requests. " + ex.Message); }
        finally { IsSyncing = false; }
    }

    /// <summary>Creates and retargets exactly the pull requests the checked plan listed, one layer at a time.</summary>
    public async Task<(bool Ok, string Message)> PublishPullRequestsAsync(PullRequestPublicationPlan plan)
    {
        if (IsDemo) return (false, "The demo is offline. Publishing pull requests is disabled.");
        if (_pullRequestWriter is null || _github is null) return (false, "Connect GitHub to publish pull requests.");
        if (_remoteSnapshot is null || _repository is null) return (false, "Refresh GitHub, then check the stack again.");
        if (IsSyncing) return (false, "A stack operation is already running.");
        if (!plan.CanPublish) return (false, plan.Blockers.Count > 0 ? plan.Blockers[0] : "Nothing to publish. Every layer is already correct.");
        IsSyncing = true; Error = "";
        var done = new List<string>();
        try
        {
            var context = _remoteSnapshot.Context;
            await _github.VerifyAccessAsync(context);
            if (LocalGroups.FirstOrDefault(group => group.Definition.Id == plan.StackId) is not { } stack)
                return (false, "This stack is no longer available. Close the preview and open it again.");
            // The plan is re-derived from the live pull requests: a pull request opened since the check must not be duplicated.
            var live = await _github.SnapshotAsync(context);
            var current = PullRequestPlanner.Plan(stack.Definition, live.PullRequests, context.RepositoryId);
            if (current.Blockers.Count > 0) return (false, "Nothing was published. " + current.Blockers[0]);
            if (!SameActions(plan, current))
                return (false, "The open pull requests changed since the check. Nothing was published: check again.");
            foreach (var step in plan.Steps.Where(step => step.Action != PullRequestAction.Unchanged))
            {
                if (step.Action == PullRequestAction.Create)
                {
                    var subject = _branchWriter is null ? null : await _branchWriter.LastCommitSubjectAsync(_repository.Root, step.Branch);
                    var created = await _pullRequestWriter.CreatePullRequestAsync(context, step.BranchName, step.ParentName,
                        string.IsNullOrWhiteSpace(subject) ? step.BranchName : subject, "", draft: false);
                    done.Add($"#{created.Number} {step.BranchName} → {step.ParentName}");
                }
                else
                {
                    var updated = await _pullRequestWriter.UpdatePullRequestBaseAsync(context, step.Number!.Value, step.ParentName);
                    done.Add($"#{updated.Number} base → {step.ParentName}");
                }
            }
            await RefreshGitHubAsync();
            var message = "Published " + string.Join(", ", done) + ".";
            Status = message;
            return (true, message);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return (false, done.Count == 0 ? "Nothing was published. " + ex.Message : "Published " + string.Join(", ", done) + ", then stopped: " + ex.Message);
        }
        finally { IsSyncing = false; }
    }

    /// <summary>Offers undo only for the stack whose write is still the newest one.</summary>
    public async Task RefreshSyncAvailabilityAsync(CancellationToken ct = default)
    {
        if (_branchWriter is null || _repository is null) return;
        try
        {
            var entry = await _branchWriter.LastJournalAsync(_repository.Root, ct);
            // Undo is offered only for the stack whose branches the entry actually touched.
            foreach (var group in LocalGroups)
                group.UndoAvailable = entry is not null && entry.Moved.Any(step => group.Definition.Branches.Contains(step.Target));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Error = ex.Message; }
    }

    /// <summary>Applies a rewritten snapshot immediately instead of behind the pending-changes prompt.</summary>
    private async Task ReloadAsync()
    {
        _applyingChanges = true;
        try { await RefreshAsync(); await RefreshSyncAvailabilityAsync(); await RefreshWorkingTreeAsync(); }
        finally { _applyingChanges = false; }
    }
}
