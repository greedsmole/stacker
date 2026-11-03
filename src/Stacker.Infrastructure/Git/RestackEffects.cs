using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

internal sealed class RestackEffects(GitRunner git, IJournal journal, IClock clock, RecoveryEffects recovery)
{
    public async Task<RestackOutcome> RestackAsync(RestackPlan plan, IProgress<string>? progress = null, CancellationToken ct = default, JournalEntry? opened = null)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            if (plan.Blockers.Count > 0) return git.Refused(plan.Blockers[0]);
            var steps = plan.Steps.Where(step => step.NeedsRebase && !step.Blocked).ToArray();
            if (steps.Length == 0) return new(RestackStatus.NothingToDo, null, [], "Every layer is already based on its parent.", null);
            if (steps.Any(step => !step.IsLocalBranch)) return git.Refused("Only local branches can be restacked.");
            if (await git.VerifyPlanAsync(plan, ct) is { } stale) return git.Refused(stale);
            var root = plan.RepositoryRoot;
            // Write-ahead: an interrupted or crashed run always leaves a record of what it intended and what it did.
            // The first step is not an effect: it keeps the branch the user started on so recovery can return there.
            var planned = steps.Select((step, index) => new JournalStep($"layer:{index}", "rebase", step.Branch, step.BranchTip, null, StepState.Planned))
                .Prepend(new JournalStep("original", "original", plan.OriginalBranch, plan.OriginalBranch, null, StepState.Planned) { RestackPlan = plan }).ToArray();
            var entry = opened ?? (await journal.OpenAsync(root, "restack", planned, ct)).OrThrow();
            return await ExecutePlanAsync(plan, entry, progress, ct);
        }
        finally { git.Gate.Release(); }
    }

    private async Task<RestackOutcome> ExecutePlanAsync(RestackPlan plan, JournalEntry entry, IProgress<string>? progress, CancellationToken ct)
    {
        var root = plan.RepositoryRoot;
        var stamp = clock.Now.UtcDateTime.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + entry.Id;
        var steps = plan.Steps.Where(step => step.NeedsRebase && !step.Blocked).ToArray();
        var planned = entry.Steps.ToArray();
        var moved = entry.Moved.ToList();
        try
        {
            for (var index = 0; index < steps.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var step = steps[index];
                var plannedStep = planned[index + 1];
                if (plannedStep.State == StepState.Done) continue;
                if (await git.ShaAsync(root, step.Branch, ct) != step.BranchTip)
                    throw new StackerException($"{step.BranchName} moved while this restack was interrupted. No further layers were changed.");
                var expectedParent = entry.Moved.LastOrDefault(movedStep => movedStep.Target == step.ParentRef)?.NewSha ?? step.OldParentTip;
                if (await git.ShaAsync(root, step.ParentRef, ct) != expectedParent)
                {
                    var message = $"{step.ParentName} moved outside this restack. No further layers were changed. Inspect the branches before recovery.";
                    entry = (await journal.CloseAsync(entry, JournalPhase.NeedsUser, message: message, ct: ct)).OrThrow();
                    return git.Refused(message, entry);
                }
                entry = (await journal.StepAsync(entry, plannedStep with { State = StepState.Started }, ct)).OrThrow();
                progress?.Report($"Restacking {step.BranchName} onto {step.ParentName}");
                await git.Run(root, ["update-ref", $"{GitRunner.BackupRoot}/{stamp}/{StackRefs.Short(step.Branch)}", step.BranchTip!], ct);
                var rebase = await git.Run(root, git.RebaseArgs(plan, step), ct, allowFailure: true);
                if (rebase.ExitCode != 0)
                {
                    var conflicts = await git.ConflictsAsync(root, ct);
                    if (conflicts.Count > 0)
                    {
                        // A conflict belongs to the user: the entry keeps the pending layer so a later undo can reverse it too.
                        entry = (await journal.CloseAsync(entry, JournalPhase.NeedsUser, plan.StashRef, plannedStep.Id,
                            $"{step.BranchName} does not apply cleanly onto {step.ParentName}. Resolve the conflicting files, stage them, then continue.", ct)).OrThrow();
                        return new(RestackStatus.Conflicted, step.BranchName, conflicts,
                            $"{step.BranchName} does not apply cleanly onto {step.ParentName}. Resolve the conflicting files, stage them, then continue.", entry);
                    }
                    await recovery.RestoreAsync(root, entry, plan.OriginalBranch, ct);
                    return new(RestackStatus.RolledBack, step.BranchName, [],
                        $"{step.BranchName} could not be rebased. Every branch was restored. {rebase.StdErr.Trim()}".Trim(), entry);
                }
                var newSha = await git.ShaAsync(root, step.Branch, ct) ?? throw new StackerException($"Git reported success, but {step.BranchName} cannot be resolved.");
                var finished = plannedStep.Finished(plannedStep.OldSha, newSha, StepState.Done);
                entry = entry with { Steps = entry.Steps.Select(s => s.Id == finished.Id ? finished : s).ToArray() };
                entry = (await journal.StepAsync(entry, finished, ct)).OrThrow();
                moved.Add(finished);
                var ancestor = await git.Run(root, ["merge-base", "--is-ancestor", step.ParentRef, step.Branch], ct, allowFailure: true);
                if (ancestor.ExitCode != 0)
                {
                    await recovery.RestoreAsync(root, entry, plan.OriginalBranch, ct);
                    return new(RestackStatus.RolledBack, step.BranchName, [],
                        $"{step.BranchName} was rewritten but still does not contain {step.ParentName}. Every branch was restored.", entry);
                }
                var replayed = git.ParseCount((await git.Run(root, ["rev-list", "--count", $"{step.OldParentTip}..{newSha}"], ct)).StdOut);
                if (replayed < step.CommitCount)
                    progress?.Report($"{step.BranchName}: {step.CommitCount - replayed} commit(s) were already present in {step.ParentName} and were dropped.");
            }
            await git.SwitchToAsync(root, plan.OriginalBranch, ct, required: true);
            entry = (await journal.CloseAsync(entry, JournalPhase.Applied, plan.StashRef, message: $"{moved.Count} layer(s) restacked.", ct: ct)).OrThrow();
            await git.PruneBackupsAsync(root, ct);
            return new(RestackStatus.Applied, null, [], $"{moved.Count} layer(s) restacked. Backups are kept at {GitRunner.BackupRoot}/{stamp}.", entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything that escapes a step must not leave the repository half-changed; the entry records the truth.
            await recovery.RestoreAsync(root, entry, plan.OriginalBranch, ct);
            return new(RestackStatus.RolledBack, null, [], $"The restack failed and every branch was restored. {ex.Message}", entry);
        }
        catch (OperationCanceledException)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await recovery.RestoreAsync(root, entry, plan.OriginalBranch, cleanup.Token);
            throw;
        }
    }

    public async Task<RestackOutcome> ContinueAsync(string root, string? editorCommand, CancellationToken ct = default, JournalEntry? target = null)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var state = await git.StatusAsync(root, ct);
            if (state.Operation != WorkingOperation.Rebase) return git.Refused("There is no rebase to continue.");
            if (state.HasConflicts)
                return new(RestackStatus.Conflicted, null, state.ConflictedFiles, "Resolve and stage these files before continuing.", (await journal.ActiveAsync(root, ct)).OrThrow());
            return await ResumeAsync(root, ["rebase", "--continue"], editorCommand, ct, target);
        }
        finally { git.Gate.Release(); }
    }

    public async Task<RestackOutcome> SkipAsync(string root, CancellationToken ct = default, JournalEntry? target = null)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var state = await git.StatusAsync(root, ct);
            if (state.Operation != WorkingOperation.Rebase) return git.Refused("There is no rebase to skip a commit in.");
            if (state.HasConflicts) await git.Run(root, ["reset", "--", .. state.ConflictedFiles], ct, allowFailure: true);
            return await ResumeAsync(root, ["rebase", "--skip"], null, ct, target);
        }
        finally { git.Gate.Release(); }
    }

    private async Task<RestackOutcome> ResumeAsync(string root, string[] arguments, string? editorCommand, CancellationToken ct, JournalEntry? target)
    {
        var entry = target ?? (await journal.ActiveAsync(root, ct)).OrThrow();
        var result = await git.Run(root, arguments, ct, allowFailure: true, timeout: GitRunner.InteractiveTimeout,
            editor: string.IsNullOrWhiteSpace(editorCommand) ? "true" : ShellCommand.Parse(editorCommand).OrThrow().ForGitEditor());
        if (result.ExitCode != 0)
        {
            var conflicts = await git.ConflictsAsync(root, ct);
            if (conflicts.Count > 0)
            {
                if (entry is not null) entry = (await journal.CloseAsync(entry, JournalPhase.NeedsUser, pendingStepId: entry.PendingStepId, message: "The next commit does not apply cleanly either.", ct: ct)).OrThrow();
                return new(RestackStatus.Conflicted, null, conflicts,
                    "The next commit does not apply cleanly either. Resolve these files, stage them, then continue again.", entry);
            }
            var detail = (result.StdErr.Trim() + " " + result.StdOut.Trim()).Trim();
            return git.Refused($"Git refused to continue. {detail}", entry);
        }
        // The layer that was waiting is now rewritten: record it so undo reverses it as well.
        if (entry?.Pending is { } pending && pending.OldSha is { } oldSha)
        {
            var newSha = await git.ShaAsync(root, pending.Target, ct);
            if (newSha is not null) entry = (await journal.StepAsync(entry, pending.Finished(oldSha, newSha, StepState.Done), ct)).OrThrow();
        }
        if (entry?.RestackPlan is { } plan) return await ExecutePlanAsync(plan, entry, null, ct);
        if (entry is not null) entry = (await journal.CloseAsync(entry, JournalPhase.Applied, message: "The layer was completed.", ct: ct)).OrThrow();
        await git.SwitchToAsync(root, entry is null ? "" : git.OriginalBranch(entry), ct, required: false);
        return new(RestackStatus.Applied, null, [], "The layer was completed. Press Restack again to finish any layers above it.", entry);
    }
}
