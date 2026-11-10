using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Executes an approved restack plan one layer at a time, writing each step to the journal before the rewrite,
/// pinning a backup ref, and restoring every branch when the run cannot finish.</summary>
/// <remarks>The entry is the operation's memory: it is opened with one planned step per layer plus a synthetic
/// <c>original</c> step, rewritten after each layer, and closed with the outcome. That write-ahead order is what makes
/// a crash recoverable, because the stored entry is always at least as strong as the in-memory state.
/// <para>Before each rebase the layer's tip and its parent's tip are re-checked against the recorded plan, so a branch
/// that moved since the plan was built stops the run instead of being overwritten. The layer's pre-rebase commit is
/// also pinned under <see cref="GitRunner.BackupRoot"/>, in a namespace built from the run's stamp plus the layer's
/// short branch name: a ref keeps that commit reachable when Git later garbage-collects unreachable objects, so the
/// rewrite stays recoverable even if the journal is lost. <see cref="GitRunner.PruneBackupsAsync"/> keeps the newest
/// <see cref="GitRunner.RetainedBackups"/> runs and deletes older ones; the count has no recorded rationale, and its
/// effect is that a rewrite can be recovered for the last ten restacks only.</para>
/// <para>A conflict is handed to the user with the rebase left in progress and the entry closed as needing attention,
/// because only the user can resolve the files. Every other failure restores the affected branches through
/// <see cref="RecoveryEffects.RestoreAsync"/> and reports a rollback. What "unknown" means for a layer: a step left in
/// <see cref="StepState.Started"/> means the rebase was dispatched and its result lost, so the branch may already point
/// at a new commit; recovery compares the current commit with the recorded new commit and restores only on a match.
/// Remote branches are outside this class entirely.</para>
/// <para>Cancellation is special: the caller's token is already cancelled, so compensation runs under its own
/// 30-second budget (<c>CancellationTokenSource(TimeSpan.FromSeconds(30))</c>) and the cancellation is rethrown
/// afterwards. The budget bounds how long an already-cancelled command may spend restoring branches; the value has no
/// recorded rationale beyond that bound, and it matches the runner's own cleanup budget. If it expires mid-restore,
/// the entry keeps whatever it recorded and remains the evidence the user needs.</para></remarks>
/// <param name="git">Runner that owns the process policy, the write gate and the ref reads.</param>
/// <param name="journal">Journal that stores the plan and each layer's outcome.</param>
/// <param name="clock">Clock used for the backup namespace stamp, so tests can pin the value.</param>
/// <param name="recovery">Recovery used to restore the branches after a failure or a cancellation.</param>
internal sealed class RestackEffects(GitRunner git, IJournal journal, IClock clock, RecoveryEffects recovery)
{
    /// <summary>Runs the plan after validating it against the repository, then executes it layer by layer.</summary>
    /// <remarks>The checks run in order of cost and consequence: a blocked plan is refused with its first blocker and
    /// no entry is opened; a plan with nothing to rewrite returns a no-op result; a non-local layer is refused because
    /// only local branches may be rewritten; and the repository is re-verified against the plan's snapshot, because the
    /// plan was built from an earlier read and a branch may have moved since. Only then is the entry opened, so a
    /// refusal leaves no recovery record behind. The planned steps are the filtered layers exactly as they will be
    /// executed, each carrying the layer's tip as its old value, and the synthetic <c>original</c> step stores the
    /// branch to return to plus the whole plan on <see cref="JournalStep.RestackPlan"/> so a resume never has to plan
    /// again against a changed repository.
    /// <para>When the command runner already opened an entry it passes it in, and this method uses that entry rather
    /// than opening a second one for the same run.</para></remarks>
    /// <param name="plan">Approved plan; its blockers, layer tips and captured remote tips are the run's input.</param>
    /// <param name="progress">Optional sink receiving one short line before each layer and a note when commits are
    /// dropped; null when the caller does not display progress.</param>
    /// <param name="ct">Cancels between layers; cancellation during an effect triggers the cleanup path.</param>
    /// <param name="opened">Entry the command runner already opened, or null to open one here.</param>
    /// <returns>Applied with the backup location, NothingToDo, Conflicted, RolledBack or Refused, with the entry that
    /// describes the run.</returns>
    /// <exception cref="OperationCanceledException">The run was cancelled; restoration is attempted under the 30-second
    /// cleanup budget before the cancellation is rethrown.</exception>
    /// <exception cref="StackerException">Recovery refused to restore because the tree gained changes or a ref could not
    /// be proven, or a journal write failed; the message says what was left for inspection.</exception>
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

    /// <summary>Walks the remaining layers of a plan, rewriting each one onto its parent and recording the result as it
    /// goes; this is also the resume path after a conflict.</summary>
    /// <remarks>Layers whose step is already <see cref="StepState.Done"/> are skipped, so a resumed run continues
    /// instead of repeating work that already landed. For each layer the branch tip must still equal the planned tip;
    /// the expected parent commit is the new commit recorded by the last moved step that targeted the same parent ref,
    /// falling back to the parent tip captured by the plan. That formula is what lets a cascade resume after this run
    /// already rewrote the parent itself: the parent has moved, but to a commit this entry recorded, so it is still an
    /// expected value rather than drift. A parent that differs from both closes the entry as needing the user and stops
    /// the cascade, because continuing would rebase onto a commit the user never approved.
    /// <para>Each layer is recorded as started, its pre-rebase commit is pinned under the backup root, the rebase runs
    /// with empty commits dropped, and success is checked twice: the rebase must have produced a commit that contains
    /// the parent, and the replayed commit count is compared with the plan so commits already present in the parent can
    /// be reported as dropped. The backup namespace is the UTC time formatted <c>yyyyMMddTHHmmss</c> plus the entry id,
    /// so two runs that start within the same second still get separate namespaces and a resume of this run reuses the
    /// one it started with. A rebase that fails with conflicts leaves the rebase in progress, remembers the pending
    /// step and returns a conflicted outcome; a rebase that fails without conflicts restores every branch.</para>
    /// <para>When every layer is rewritten the user is returned to the original branch, and only then is the entry
    /// closed as applied and the old backup namespaces pruned. Because the return is required, a checkout that fails
    /// here is treated like any other failure and rolls the rewrite back, which keeps the repository consistent at the
    /// cost of discarding a rewrite that had already succeeded.</para></remarks>
    /// <param name="plan">Plan whose original branch, stash and layer order drive the run.</param>
    /// <param name="entry">Entry holding the planned steps and the moves recorded so far.</param>
    /// <param name="progress">Optional progress sink; null on a resume that displays no per-layer line.</param>
    /// <param name="ct">Cancels between layers.</param>
    /// <returns>Applied, Conflicted or RolledBack, with the entry reflecting what was recorded.</returns>
    /// <exception cref="OperationCanceledException">Cancellation during a layer or before the next one; restoration is
    /// attempted under the 30-second cleanup budget first.</exception>
    /// <exception cref="StackerException">Recovery refused to restore because the tree gained changes or a ref could not
    /// be proven, or a journal write failed.</exception>
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

    /// <summary>Finishes a rebase that stopped on a conflict, after refusing while unresolved conflicts remain.</summary>
    /// <remarks>Continuing is only meaningful while a rebase is in progress, so anything else is refused rather than
    /// attempted. Git refuses to continue while the index still holds unmerged entries, so the conflict check turns
    /// that into a message naming the files instead of a raw Git failure. The editor is resolved by the resume path,
    /// and the entry is found again by that path because the user may have restarted the application since the
    /// conflict.</remarks>
    /// <param name="root">Repository root holding the stopped rebase.</param>
    /// <param name="editorCommand">The user's editor command for the commit-message step, or null for the platform
    /// default supplied by the resume path.</param>
    /// <param name="ct">Cancels the check and the resume.</param>
    /// <param name="target">Entry to resume, or null to use the journal's active entry.</param>
    /// <returns>A conflicted outcome while conflicts remain, otherwise whatever the resume reports.</returns>
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

    /// <summary>Drops the commit the stopped rebase cannot apply and moves on.</summary>
    /// <remarks>Skipping is only offered while a rebase is in progress. Unmerged index entries would make Git refuse
    /// the skip, so they are reset first (with failure allowed, because a path may already be gone); that clears the
    /// conflict state for exactly the conflicted paths without touching anything else in the index. The rest of the
    /// work is the shared resume path, which records the layer and continues the plan.</remarks>
    /// <param name="root">Repository root holding the stopped rebase.</param>
    /// <param name="ct">Cancels the reset and the skip.</param>
    /// <param name="target">Entry to resume, or null to use the journal's active entry.</param>
    /// <returns>A conflicted outcome when the next commit also conflicts, otherwise whatever the resume reports.</returns>
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

    /// <summary>Runs <c>rebase --continue</c> or <c>rebase --skip</c>, records the layer it completed, and continues the
    /// remaining plan.</summary>
    /// <remarks>The interactive budget applies here because Git may open the user's editor to accept the commit message,
    /// so the run is allowed <see cref="GitRunner.InteractiveTimeout"/> instead of the ordinary write timeout; a blank
    /// editor setting becomes the <c>true</c> no-op command, which keeps a non-interactive rebase from blocking on an
    /// editor. Another conflict closes the entry as needing the user (keeping the same pending step) and returns a
    /// conflicted outcome; any other refusal is reported with Git's own explanation and the entry is not advanced.
    /// <para>On success the pending layer is recorded as finished when its branch now has a commit, so a later undo
    /// reverses the resumed layer too. If the entry still carries the approved plan, execution continues with the
    /// remaining layers rather than closing after this one; only a resumed run that has no plan attached (for example
    /// a conflict resolved after the entry was truncated) closes as applied and tells the user to restack again.</para>
    /// </remarks>
    /// <param name="root">Repository root holding the stopped rebase.</param>
    /// <param name="arguments">The rebase sub-command and its flags, already split.</param>
    /// <param name="editorCommand">The user's editor command, or null to use the no-op editor.</param>
    /// <param name="ct">Cancels the resume and any remaining layers.</param>
    /// <param name="target">Entry to resume, or null to use the journal's active entry.</param>
    /// <returns>Applied, Conflicted or Refused with the entry that reflects the outcome.</returns>
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
