using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class SyncController(ISyncPresentation owner, WorkspaceRuntime runtime)
{

    public async Task<RestackPlan?> BuildRestackPlanAsync(bool preserveMerges = false)
    {
        if (runtime._branchWriter is null || runtime._repository is null)
        {
            owner.Error = "Open a repository first.";
            return null;
        }
        if (runtime._activeGroup is not { IsRemote: false } group)
        {
            owner.Error = "Select a local stack to restack. GitHub stacks use the server's own branches.";
            return null;
        }
        owner.IsSyncing = true;
        owner.Error = "";
        try
        {
            // A preview must describe the repository as it is now, never the last displayed snapshot.
            if (!owner.HasPendingChanges)
                await owner.RefreshAsync();
            if (owner.HasPendingChanges)
                await owner.UpdateChangesAsync();
            if (owner.HasPendingChanges || runtime._repository is null)
            {
                owner.Error = "The repository changed and could not be reloaded. Refresh, then open the restack preview again.";
                return null;
            }
            return (await runtime._branchWriter.PlanAsync(runtime._repository, (owner.LocalGroups.FirstOrDefault(g => g.Definition.Id == group.Definition.Id) ?? group).Definition, preserveMerges)).OrThrow();
        }
        catch (Exception ex) { owner.Error = ex.Message; return null; }
        finally { owner.IsSyncing = false; }
    }

    public async Task<RestackOutcome?> RestackAsync(RestackPlan plan, IProgress<string>? progress = null)
    {
        if (runtime._branchWriter is null || owner.IsSyncing)
            return null;
        owner.IsSyncing = true;
        owner.Error = "";
        try
        {
            var outcome = (await runtime._branchWriter.RestackAsync(plan, progress)).OrThrow();
            owner.Status = outcome.Message;
            // A conflict is the user's to resolve, so it is reported in the conflict panel rather than as a failure.
            if (!outcome.Applied && !outcome.Conflicted)
                owner.Error = outcome.Message;
            if (outcome.Applied || outcome.Conflicted || outcome.RolledBack)
                await ReloadAsync();
            return outcome;
        }
        catch (Exception ex) { owner.Error = ex.Message; return new(RestackStatus.Refused, null, [], ex.Message, null); }
        finally { owner.IsSyncing = false; }
    }

    public async Task<(bool Ok, string Message)> PushAsync(RestackPlan plan, bool wholeStack)
    {
        if (runtime._branchWriter is null || owner.IsSyncing)
            return (false, "A stack operation is already running.");
        owner.IsSyncing = true;
        owner.Error = "";
        try
        {
            var pushed = wholeStack ? (await runtime._branchWriter.PushStackAsync(plan)).OrThrow() : (await runtime._branchWriter.PushAsync(plan)).OrThrow();
            var message = $"Pushed {string.Join(", ", pushed)}. The pull requests now point at the current commits.";
            owner.Status = message;
            await owner.RefreshGitHubAsync();
            return (true, message);
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
        finally { owner.IsSyncing = false; }
    }

    public async Task<(bool Ok, string Message)> UndoAsync()
    {
        if (runtime._branchWriter is null || runtime._repository is null)
            return (false, "Open a repository first.");
        owner.IsSyncing = true;
        owner.Error = "";
        try
        {
            var outcome = (await runtime._branchWriter.UndoLastAsync(runtime._repository.Root)).OrThrow();
            owner.Status = outcome.Message;
            await ReloadAsync();
            return (outcome.Applied || outcome.RolledBack, outcome.Message);
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
        finally { owner.IsSyncing = false; }
    }

    public async Task<(bool Ok, string Message)> AddLayerAsync(string stackId, string branchName, string startRef)
    {
        if (runtime._branchWriter is null || runtime._repository is null)
            return (false, "Open a repository first.");
        if (owner.IsSyncing)
            return (false, "A stack operation is already running.");
        if (owner.LocalGroups.FirstOrDefault(g => g.Definition.Id == stackId) is not { } group)
            return (false, "This stack is no longer available. Refresh and try again.");
        owner.IsSyncing = true;
        owner.Error = "";
        try
        {
            var reference = (await runtime._branchWriter.CreateLayerAsync(runtime._repository.Root, branchName, startRef)).OrThrow();
            var updated = group.Definition with
            {
                Branches = [.. group.Definition.Branches, reference]
            };
            if (!await owner.SaveStackAsync(updated))
                return (false, $"{StackRefs.Short(reference)} was created and checked out, but the stack definition could not be saved: {owner.Error}");
            var message = $"{StackRefs.Short(reference)} was created from {StackRefs.Short(startRef)} and added as layer {updated.Branches.Count}. Commit there whenever you are ready.";
            owner.Status = message;
            return (true, message);
        }
        catch (Exception ex) { owner.Error = ex.Message; return (false, ex.Message); }
        finally { owner.IsSyncing = false; }
    }

    public async Task<(PullRequestPublicationPlan? Plan, string Message)> CheckPullRequestsAsync(string stackId)
    {
        if (owner.IsDemo)
            return (null, "The demo is offline. Publishing pull requests is disabled.");
        if (runtime._github is null || runtime._pullRequestWriter is null)
            return (null, "Connect GitHub to publish pull requests.");
        if (runtime._remoteSnapshot is null || runtime._repository is null)
            return (null, "Refresh GitHub, then check the stack again.");
        if (owner.IsSyncing)
            return (null, "A stack operation is already running.");
        if (owner.LocalGroups.FirstOrDefault(g => g.Definition.Id == stackId) is not { } group)
            return (null, "This stack is no longer available. Close the preview and open it again.");
        owner.IsSyncing = true;
        owner.Error = "";
        try
        {
            var context = runtime._remoteSnapshot.Context;
            (await runtime._github.VerifyAccessAsync(context)).OrThrow();
            var snapshot = (await runtime._github.SnapshotAsync(context)).OrThrow();
            var plan = PullRequestPlanner.Plan(group.Definition, snapshot.PullRequests, context.RepositoryId)
                with
            {
                ContextIdentity = context.Identity,
                RepositoryRoot = runtime._repository.Root
            };
            var message = plan.Blockers.Count > 0 ? string.Join(" ", plan.Blockers)
                : plan.PendingCount == 0 ? "Every layer already has an open pull request into its parent."
                : $"{plan.Steps.Count(step => step.Action == PullRequestAction.Create)} to create, {plan.Steps.Count(step => step.Action == PullRequestAction.UpdateBase)} to retarget.";
            return (plan, message);
        }
        catch (Exception ex) { owner.Error = ex.Message; return (null, "Could not read the pull requests. " + ex.Message); }
        finally { owner.IsSyncing = false; }
    }

    public async Task<(bool Ok, string Message)> PublishPullRequestsAsync(PullRequestPublicationPlan plan)
    {
        if (owner.IsDemo)
            return (false, "The demo is offline. Publishing pull requests is disabled.");
        if (runtime._pullRequestWriter is null || runtime._github is null)
            return (false, "Connect GitHub to publish pull requests.");
        if (runtime._remoteSnapshot is null || runtime._repository is null)
            return (false, "Refresh GitHub, then check the stack again.");
        if (owner.IsSyncing)
            return (false, "A stack operation is already running.");
        if (!plan.CanPublish)
            return (false, plan.Blockers.Count > 0 ? plan.Blockers[0] : "Nothing to publish. Every layer is already correct.");
        owner.IsSyncing = true;
        owner.Error = "";
        var done = new List<string>();
        var phase = PullRequestBatchPhase.CheckingPlan;
        PullRequestStep? currentStep = null;
        try
        {
            var context = runtime._remoteSnapshot.Context;
            var root = runtime._repository.Root;
            if (plan.ContextIdentity != context.Identity || plan.RepositoryRoot != root)
                return (false, "The repository or GitHub identity changed. Nothing was published: check the pull requests again.");
            (await runtime._github.VerifyAccessAsync(context)).OrThrow();
            if (owner.LocalGroups.FirstOrDefault(group => group.Definition.Id == plan.StackId) is not { } stack)
                return (false, "This stack is no longer available. Close the preview and open it again.");
            // The plan is re-derived from the live pull requests: a pull request opened since the check must not be duplicated.
            var live = (await runtime._github.SnapshotAsync(context)).OrThrow();
            var current = PullRequestPlanner.Plan(stack.Definition, live.PullRequests, context.RepositoryId);
            if (current.Blockers.Count > 0)
                return (false, "Nothing was published. " + current.Blockers[0]);
            if (!WorkspaceRuntime.SameActions(plan, current))
                return (false, "The open pull requests changed since the check. Nothing was published: check again.");
            foreach (var step in plan.Steps.Where(step => step.Action != PullRequestAction.Unchanged))
            {
                currentStep = step;
                phase = PullRequestBatchPhase.PreparingLayer;
                if (step.Action == PullRequestAction.Create)
                {
                    var subject = runtime._branchWriter is null ? null : (await runtime._branchWriter.LastCommitSubjectAsync(root, step.Branch)).OrThrow();
                    phase = PullRequestBatchPhase.SendingLayer;
                    var created = (await runtime._pullRequestWriter.CreatePullRequestAsync(context, step.BranchName, step.ParentName,
                        string.IsNullOrWhiteSpace(subject) ? step.BranchName : subject, "", draft: false)).OrThrow();
                    done.Add($"#{created.Number} {step.BranchName} → {step.ParentName}");
                }
                else
                {
                    phase = PullRequestBatchPhase.SendingLayer;
                    var updated = (await runtime._pullRequestWriter.UpdatePullRequestBaseAsync(context, step.Number!.Value, step.ParentName)).OrThrow();
                    done.Add($"#{updated.Number} base → {step.ParentName}");
                }
            }
            phase = PullRequestBatchPhase.Refreshing;
            var message = "Published " + string.Join(", ", done) + ".";
            owner.Status = message;
            await owner.RefreshGitHubAfterPublicationAsync();
            return (true, message);
        }
        catch (Exception ex)
        {
            if (phase == PullRequestBatchPhase.Refreshing)
            {
                owner.Error = "Could not refresh GitHub after publication. " + ex.Message;
                return (true, owner.Status + " " + owner.Error);
            }
            if (phase == PullRequestBatchPhase.SendingLayer && currentStep is { } step)
            {
                var certainty = (ex as StackerException)?.Problem?.Certainty ?? EffectCertainty.Unknown;
                if (certainty == EffectCertainty.Confirmed)
                {
                    done.Add($"{step.BranchName} → {step.ParentName}");
                    owner.Error = "Could not record the confirmed GitHub write locally. " + ex.Message;
                    return (false, "Published " + string.Join(", ", done) + ". " + owner.Error);
                }
                if (certainty == EffectCertainty.Unknown)
                {
                    var confirmed = done.Count == 0 ? "" : "Published " + string.Join(", ", done) + ". ";
                    owner.Error = $"{confirmed}The publication outcome for layer {step.BranchName} is unknown. Check GitHub before retrying. {ex.Message}";
                    return (false, owner.Error);
                }
            }
            owner.Error = ex.Message;
            return (false, done.Count == 0 ? "Nothing was published. " + ex.Message : "Published " + string.Join(", ", done) + ", then stopped: " + ex.Message);
        }
        finally { owner.IsSyncing = false; }
    }

    public async Task RefreshSyncAvailabilityAsync(CancellationToken ct = default)
    {
        if (runtime._branchWriter is null || runtime._repository is null)
            return;
        try
        {
            var entry = (await runtime._branchWriter.LastJournalAsync(runtime._repository.Root, ct)).OrThrow();
            // Undo is offered only for the stack whose branches the entry actually touched.
            foreach (var group in owner.LocalGroups)
                group.UndoAvailable = entry is not null && entry.Moved.Any(step => group.Definition.Branches.Contains(step.Target));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { owner.Error = ex.Message; }
    }

    public async Task ReloadAsync()
    {
        runtime._applyingChanges = true;
        try
        {
            await owner.RefreshAsync();
            await RefreshSyncAvailabilityAsync();
            await owner.RefreshWorkingTreeAsync();
        }
        finally { runtime._applyingChanges = false; }
    }

}
