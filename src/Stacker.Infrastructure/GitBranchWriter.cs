// Purpose: The only component that mutates the user's repository; every effect is recorded in the write-ahead journal before it happens.
using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

/// <summary>Restacks, pushes, stashes, commits and resolves stack branches; every rewrite is journalled, reversible and recoverable.</summary>
/// <remarks>Hooks, editors, GPG prompts and the global rebase update-refs setting are neutralized for internal rewrites so a cascade cannot block; a user's own commit keeps their configuration.</remarks>
public sealed class GitBranchWriter(IProcessRunner runner, GitExecutable executable, IGitRepositoryReader reader, IJournal journal, IClock clock) : IGitBranchWriter
{
    private const string BackupRoot = "refs/stacker/backup";
    private const int RetainedBackups = 10;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(3);
    private static readonly IReadOnlyDictionary<string, string> WriteEnvironment = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "Never", ["GIT_PAGER"] = "cat",
        ["GIT_EDITOR"] = "true", ["GIT_SEQUENCE_EDITOR"] = "true", ["LC_ALL"] = "C"
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Builds the preview from read-only facts; no ref is touched.</summary>
    public async Task<RestackPlan> PlanAsync(RepositorySnapshot repository, StackDefinition stack, bool preserveMerges = false, CancellationToken ct = default)
    {
        if (stack.Branches.Count == 0) return new(repository.Root, stack.Id, stack.Name, "", false, preserveMerges, [], ["This stack has no layers."]);
        var preflight = await PreflightAsync(repository, stack, ct);
        return StackSyncPlanner.Plan(repository, stack, preflight, preserveMerges);
    }

    private async Task<SyncPreflight> PreflightAsync(RepositorySnapshot repository, StackDefinition stack, CancellationToken ct)
    {
        var root = repository.Root;
        var status = await StatusAsync(root, ct);
        var refs = repository.Refs.ToDictionary(r => r.Name, r => r.Sha, StringComparer.Ordinal);
        var layers = new List<SyncLayerState>();
        for (var i = 0; i < stack.Branches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var branch = stack.Branches[i];
            var parent = i == 0 ? stack.Base : stack.Branches[i - 1];
            if (!refs.TryGetValue(branch, out var headSha) || !refs.TryGetValue(parent, out var parentSha)) { layers.Add(new(branch, 0, 0, false)); continue; }
            var (_, behind) = await reader.CountsAsync(root, parentSha, headSha, ct);
            var commits = ParseCount((await Run(root, ["rev-list", "--count", $"{parentSha}..{headSha}"], ct)).StdOut);
            var merges = ParseCount((await Run(root, ["rev-list", "--merges", "--count", $"{parentSha}..{headSha}"], ct)).StdOut) > 0;
            layers.Add(new(branch, behind, commits, merges));
        }
        var origin = await Run(root, ["remote", "get-url", "origin"], ct, allowFailure: true);
        return new(root, status.Branch, status.HasTrackedChanges, status.HasOperation ? WorkingOperations.Noun(status.Operation) : null,
            await OtherWorktreeBranchesAsync(root, ct), layers, origin.ExitCode == 0);
    }

    /// <summary>Rebases each planned layer onto its parent; the journal exists before the first ref moves.</summary>
    public async Task<RestackOutcome> RestackAsync(RestackPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (plan.Blockers.Count > 0) return Refused(plan.Blockers[0]);
            var steps = plan.Steps.Where(step => step.NeedsRebase && !step.Blocked).ToArray();
            if (steps.Length == 0) return new(RestackStatus.NothingToDo, null, [], "Every layer is already based on its parent.", null);
            if (steps.Any(step => !step.IsLocalBranch)) return Refused("Only local branches can be restacked.");
            if (await VerifyPlanAsync(plan, ct) is { } stale) return Refused(stale);
            var root = plan.RepositoryRoot;
            var stamp = clock.Now.UtcDateTime.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
            // Write-ahead: an interrupted or crashed run always leaves a record of what it intended and what it did.
            // The first step is not an effect: it keeps the branch the user started on so recovery can return there.
            var planned = steps.Select((step, index) => new JournalStep($"layer:{index}", "rebase", step.Branch, step.BranchTip, null, StepState.Planned))
                .Prepend(new JournalStep("original", "original", plan.OriginalBranch, plan.OriginalBranch, null, StepState.Planned)).ToArray();
            var entry = await journal.OpenAsync(root, "restack", planned, ct);
            var moved = new List<JournalStep>();
            try
            {
                for (var index = 0; index < steps.Length; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = steps[index];
                    var plannedStep = planned[index + 1];
                    progress?.Report($"Restacking {step.BranchName} onto {step.ParentName}");
                    await Run(root, ["update-ref", $"{BackupRoot}/{stamp}/{StackRefs.Short(step.Branch)}", step.BranchTip!], ct);
                    var rebase = await Run(root, RebaseArgs(plan, step), ct, allowFailure: true);
                    if (rebase.ExitCode != 0)
                    {
                        var conflicts = await ConflictsAsync(root, ct);
                        if (conflicts.Count > 0)
                        {
                            // A conflict belongs to the user: the entry keeps the pending layer so a later undo can reverse it too.
                            entry = await journal.CloseAsync(entry, JournalPhase.NeedsUser, plan.StashRef, plannedStep.Id,
                                $"{step.BranchName} does not apply cleanly onto {step.ParentName}. Resolve the conflicting files, stage them, then continue.", ct);
                            return new(RestackStatus.Conflicted, step.BranchName, conflicts,
                                $"{step.BranchName} does not apply cleanly onto {step.ParentName}. Resolve the conflicting files, stage them, then continue.", entry);
                        }
                        await RestoreAsync(root, entry, plan.OriginalBranch, ct);
                        return new(RestackStatus.RolledBack, step.BranchName, [],
                            $"{step.BranchName} could not be rebased. Every branch was restored. {rebase.StdErr.Trim()}".Trim(), entry);
                    }
                    var newSha = await ShaAsync(root, step.Branch, ct) ?? throw new StackerException($"Git reported success, but {step.BranchName} cannot be resolved.");
                    var finished = plannedStep.Finished(plannedStep.OldSha, newSha, StepState.Done);
                    entry = await journal.StepAsync(entry, finished, ct);
                    moved.Add(finished);
                    var ancestor = await Run(root, ["merge-base", "--is-ancestor", step.ParentRef, step.Branch], ct, allowFailure: true);
                    if (ancestor.ExitCode != 0)
                    {
                        await RestoreAsync(root, entry, plan.OriginalBranch, ct);
                        return new(RestackStatus.RolledBack, step.BranchName, [],
                            $"{step.BranchName} was rewritten but still does not contain {step.ParentName}. Every branch was restored.", entry);
                    }
                    var replayed = ParseCount((await Run(root, ["rev-list", "--count", $"{step.OldParentTip}..{newSha}"], ct)).StdOut);
                    if (replayed < step.CommitCount)
                        progress?.Report($"{step.BranchName}: {step.CommitCount - replayed} commit(s) were already present in {step.ParentName} and were dropped.");
                }
                await SwitchToAsync(root, plan.OriginalBranch, ct, required: true);
                entry = await journal.CloseAsync(entry, JournalPhase.Applied, plan.StashRef, message: $"{moved.Count} layer(s) restacked.", ct: ct);
                await PruneBackupsAsync(root, ct);
                return new(RestackStatus.Applied, null, [], $"{moved.Count} layer(s) restacked. Backups are kept at {BackupRoot}/{stamp}.", entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Anything that escapes a step must not leave the repository half-changed; the entry records the truth.
                await RestoreAsync(root, entry, plan.OriginalBranch, ct);
                return new(RestackStatus.RolledBack, null, [], $"The restack failed and every branch was restored. {ex.Message}", entry);
            }
            catch (OperationCanceledException)
            {
                await RestoreAsync(root, entry, plan.OriginalBranch, ct);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Resumes an interrupted rebase after the user resolved and staged the conflicts.</summary>
    public async Task<RestackOutcome> ContinueAsync(string root, string? editorCommand, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await StatusAsync(root, ct);
            if (state.Operation != WorkingOperation.Rebase) return Refused("There is no rebase to continue.");
            if (state.HasConflicts)
                return new(RestackStatus.Conflicted, null, state.ConflictedFiles, "Resolve and stage these files before continuing.", await journal.ActiveAsync(root, ct));
            return await ResumeAsync(root, ["rebase", "--continue"], editorCommand, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drops the commit that conflicted and resumes the rebase.</summary>
    public async Task<RestackOutcome> SkipAsync(string root, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await StatusAsync(root, ct);
            if (state.Operation != WorkingOperation.Rebase) return Refused("There is no rebase to skip a commit in.");
            if (state.HasConflicts) await Run(root, ["reset", "--", .. state.ConflictedFiles], ct, allowFailure: true);
            return await ResumeAsync(root, ["rebase", "--skip"], null, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Aborts the operation in progress and restores every branch the interrupted run moved.</summary>
    public async Task<RestackOutcome> AbortAsync(string root, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var entry = await journal.ActiveAsync(root, ct);
            var state = await StatusAsync(root, ct);
            if (!state.HasOperation && entry is null) return Refused("There is no operation to abort.");
            var restored = await RestoreAsync(root, entry, entry is null ? "" : OriginalBranch(entry), ct, close: false);
            var message = restored == 0 ? "The operation was aborted." : $"The operation was aborted and {restored} branch(es) were restored.";
            if (entry is not null) entry = await journal.CloseAsync(entry, JournalPhase.RolledBack, message: message, ct: ct);
            return new(RestackStatus.RolledBack, null, [], message, entry);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Shared tail of continue and skip: finish the pending step, or record where it stopped.</summary>
    private async Task<RestackOutcome> ResumeAsync(string root, string[] arguments, string? editorCommand, CancellationToken ct)
    {
        var entry = await journal.ActiveAsync(root, ct);
        var result = await Run(root, arguments, ct, allowFailure: true, timeout: InteractiveTimeout,
            editor: string.IsNullOrWhiteSpace(editorCommand) ? "true" : editorCommand);
        if (result.ExitCode != 0)
        {
            var conflicts = await ConflictsAsync(root, ct);
            if (conflicts.Count > 0)
            {
                if (entry is not null) entry = await journal.CloseAsync(entry, JournalPhase.NeedsUser, message: "The next commit does not apply cleanly either.", ct: ct);
                return new(RestackStatus.Conflicted, null, conflicts,
                    "The next commit does not apply cleanly either. Resolve these files, stage them, then continue again.", entry);
            }
            var detail = (result.StdErr.Trim() + " " + result.StdOut.Trim()).Trim();
            return Refused($"Git refused to continue. {detail}", entry);
        }
        // The layer that was waiting is now rewritten: record it so undo reverses it as well.
        if (entry?.Pending is { } pending && pending.OldSha is { } oldSha)
        {
            var newSha = await ShaAsync(root, pending.Target, ct);
            if (newSha is not null) entry = await journal.StepAsync(entry, pending.Finished(oldSha, newSha, StepState.Done), ct);
        }
        if (entry is not null) entry = await journal.CloseAsync(entry, JournalPhase.Applied, message: "The layer was completed.", ct: ct);
        await SwitchToAsync(root, entry is null ? "" : OriginalBranch(entry), ct, required: false);
        return new(RestackStatus.Applied, null, [], "The layer was completed. Press Restack again to finish any layers above it.", entry);
    }

    /// <summary>Restores every branch the entry intended to move to the commit it recorded, whether or not the effect was recorded.</summary>
    private async Task<int> RestoreAsync(string root, JournalEntry? entry, string originalBranch, CancellationToken ct, bool close = true)
    {
        var operation = await OperationAsync(root, ct);
        if (operation != WorkingOperation.None) await Run(root, AbortArgs(operation), ct, allowFailure: true);
        var current = await CurrentBranchRefAsync(root, ct);
        var restored = 0;
        foreach (var step in entry?.Restorable ?? [])
        {
            var branch = step.Target;
            var sha = await ShaAsync(root, branch, ct);
            // Untouched branches are already where they belong; a branch that moved must go back.
            if (sha is null || sha == step.OldSha) continue;
            // A branch that is currently checked out must move together with its working tree.
            var result = string.Equals(branch, current, StringComparison.Ordinal)
                ? await Run(root, ["reset", "--hard", step.OldSha!], ct, allowFailure: true)
                : await Run(root, ["update-ref", branch, step.OldSha!], ct, allowFailure: true);
            if (result.ExitCode == 0) restored++;
        }
        await SwitchToAsync(root, originalBranch, ct, required: false);
        if (close && entry is not null)
            await journal.CloseAsync(entry, JournalPhase.RolledBack, message: $"Every branch was restored ({restored}).", ct: ct);
        return restored;
    }

    /// <summary>Pushes the layers a restack rewrote.</summary>
    public Task<IReadOnlyList<string>> PushAsync(RestackPlan plan, CancellationToken ct = default) => PushStepsAsync(plan, plan.PushSteps, ct);

    /// <summary>Pushes every layer of the stack, forcing only where the remote is not an ancestor.</summary>
    public Task<IReadOnlyList<string>> PushStackAsync(RestackPlan plan, CancellationToken ct = default) => PushStepsAsync(plan, plan.StackPushSteps, ct);

    /// <summary>Fetches each branch, requires the remote to still hold what the plan recorded, then pushes.</summary>
    private async Task<IReadOnlyList<string>> PushStepsAsync(RestackPlan plan, IReadOnlyList<PushStep> steps, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!plan.HasOrigin) throw new StackerException("This repository has no origin remote. Restacking stays local.");
            if (steps.Count == 0) throw new StackerException("There is nothing to push for this stack.");
            var root = plan.RepositoryRoot;
            var pushed = new List<string>();
            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                var name = StackRefs.Short(step.Branch);
                var reference = "refs/heads/" + name;
                var fetch = await Run(root, ["fetch", "--no-tags", "--no-write-fetch-head", "origin", $"+{reference}:refs/remotes/origin/{name}"],
                    ct, allowFailure: true, timeout: NetworkTimeout);
                string? remoteSha = null;
                if (fetch.ExitCode != 0)
                {
                    if (!fetch.StdErr.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
                        throw new StackerException($"Cannot read {name} from origin. {fetch.StdErr.Trim()}");
                }
                else remoteSha = await ShaAsync(root, "refs/remotes/origin/" + name, ct);
                if (remoteSha != step.RemoteTip)
                    throw new StackerException(remoteSha is null
                        ? $"{name} exists on origin but was not part of the preview. Nothing was pushed: refresh the repository and restack again."
                        : $"{name} moved on origin after the preview was built. Nothing was pushed. Refresh and restack again.");
                var local = await ShaAsync(root, reference, ct) ?? throw new StackerException($"{name} no longer exists.");
                var force = remoteSha is not null
                    && (await Run(root, ["merge-base", "--is-ancestor", remoteSha, local], ct, allowFailure: true)).ExitCode != 0;
                var push = await Run(root, remoteSha is null
                        ? ["push", "--set-upstream", "origin", $"{reference}:{reference}"]
                        : force
                            ? ["push", $"--force-with-lease={reference}:{remoteSha}", "origin", $"{reference}:{reference}"]
                            : ["push", "origin", $"{reference}:{reference}"],
                    ct, allowFailure: true, timeout: NetworkTimeout);
                if (push.ExitCode != 0) throw new StackerException($"Pushing {name} failed and the remaining layers were left alone. {push.StdErr.Trim()}");
                pushed.Add(name);
            }
            return pushed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The newest entry a user can still undo.</summary>
    public Task<JournalEntry?> LastJournalAsync(string root, CancellationToken ct = default) => journal.UndoableAsync(root, ct);

    /// <summary>The newest entry that still needs the user: undoable, conflicted, or holding a stash.</summary>
    public async Task<JournalEntry?> ActiveJournalAsync(string root, CancellationToken ct = default) =>
        await journal.ActiveAsync(root, ct) ?? await journal.UndoableAsync(root, ct);

    /// <summary>Closes the newest entry that still needs the user; the repository is left untouched.</summary>
    public async Task DismissAsync(string root, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var entry = await ActiveJournalAsync(root, ct) ?? throw new StackerException("There is nothing to dismiss in this repository.");
            await journal.CloseAsync(entry, JournalPhase.Dismissed, message: entry.Message, ct: ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Restores the branches of the last applied restack; refused when the working tree is not clean.</summary>
    public async Task<RestackOutcome> UndoLastAsync(string root, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var entry = await journal.UndoableAsync(root, ct) ?? throw new StackerException("There is no restack to undo in this repository.");
            var state = await StatusAsync(root, ct);
            if (state.HasTrackedChanges) throw new StackerException("The working tree has uncommitted changes. Commit or stash them before undoing.");
            if (state.HasOperation) throw new StackerException($"{WorkingOperations.Noun(state.Operation)} is in progress. Finish or abort it before undoing.");
            var current = await CurrentBranchRefAsync(root, ct);
            foreach (var step in entry.Restorable)
            {
                ct.ThrowIfCancellationRequested();
                var sha = await ShaAsync(root, step.Target, ct);
                // A branch that still holds the commit the restack produced may be reversed; anything else is someone's new work.
                if (step.NewSha is not null && sha != step.NewSha)
                    throw new StackerException($"{StackRefs.Short(step.Target)} moved after the restack. Undo would discard that work, so nothing was changed.");
                if (sha is null || sha == step.OldSha) continue;
                if (string.Equals(step.Target, current, StringComparison.Ordinal)) await Run(root, ["reset", "--hard", step.OldSha!], ct);
                else await Run(root, ["update-ref", step.Target, step.OldSha!], ct);
            }
            await SwitchToAsync(root, OriginalBranch(entry), ct, required: true);
            entry = await journal.CloseAsync(entry, JournalPhase.Undone,
                message: $"Restored {entry.Moved.Count} branch(es) to their commits before the restack. Remote branches were not changed.", ct: ct);
            return new(RestackStatus.RolledBack, null, [],
                $"Restored {entry.Moved.Count} branch(es) to their commits before the restack. Remote branches were not changed.", entry);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads what the working tree holds, including any unfinished operation and unmerged paths.</summary>
    public Task<WorkingTreeState> StateAsync(string root, CancellationToken ct = default) => StatusAsync(root, ct);

    /// <summary>Subject line of a branch's newest commit, offered as the title of a new pull request.</summary>
    public async Task<string?> LastCommitSubjectAsync(string root, string branch, CancellationToken ct = default)
    {
        var result = await Run(root, ["log", "-1", "--format=%s", branch], ct, allowFailure: true);
        var subject = result.StdOut.Trim();
        return result.ExitCode == 0 && subject.Length > 0 ? subject : null;
    }

    /// <summary>Creates a layer branch and checks it out; the commit itself stays with the user.</summary>
    public async Task<string> CreateLayerAsync(string root, string branchName, string startRef, CancellationToken ct = default)
    {
        var name = StackRefs.Short(branchName.Trim());
        if (name.Length == 0) throw new StackerException("Enter a branch name for the new layer.");
        var state = await StatusAsync(root, ct);
        if (state.HasTrackedChanges) throw new StackerException("The working tree has uncommitted changes. Commit or stash them before creating a layer.");
        if (state.HasOperation) throw new StackerException($"{WorkingOperations.Noun(state.Operation)} is in progress. Finish or abort it before creating a layer.");
        var format = await Run(root, ["check-ref-format", "--branch", name], ct, allowFailure: true);
        if (format.ExitCode != 0) throw new StackerException($"'{name}' is not a valid branch name.");
        if (await ShaAsync(root, "refs/heads/" + name, ct) is not null)
            throw new StackerException($"{name} already exists. Choose another name or edit the stack instead.");
        if (await ShaAsync(root, startRef, ct) is null)
            throw new StackerException($"Cannot start from {StackRefs.Short(startRef)}: that ref no longer exists.");
        await Run(root, ["checkout", "-b", name, startRef], ct);
        return "refs/heads/" + name;
    }

    /// <summary>Stages the named paths so a resolved conflict can be continued.</summary>
    public async Task<IReadOnlyList<string>> StageAsync(string root, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) throw new StackerException("There is nothing to stage.");
        await _gate.WaitAsync(ct);
        try
        {
            await Run(root, ["add", "--", .. paths], ct);
            return paths;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Commits the working tree; the user's hooks and signing configuration apply, unlike an internal rewrite.</summary>
    public async Task<string> CommitAsync(string root, string message, bool stageAll, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new StackerException("Enter a commit message.");
        await _gate.WaitAsync(ct);
        try
        {
            var state = await StatusAsync(root, ct);
            if (state.HasConflicts) throw new StackerException("The conflicted files must be resolved and staged first.");
            if (stageAll) await Run(root, ["add", "--all"], ct);
            else if (!state.Entries.Any(entry => entry.IsStaged)) throw new StackerException("Nothing is staged. Tick \"stage every change\" or stage the files in your editor.");
            var commit = await RunUser(root, ["commit", "-m", message], ct);
            if (commit.ExitCode != 0) throw new StackerException($"Git refused the commit. {(commit.StdErr.Trim() + " " + commit.StdOut.Trim()).Trim()}");
            return await ShaAsync(root, "HEAD", ct) ?? throw new StackerException("The new commit could not be read back.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stashes tracked changes for a rewrite; the stash is recorded on the entry that already needs the user.</summary>
    public async Task<string?> StashAsync(string root, string message, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await StatusAsync(root, ct);
            if (!state.HasTrackedChanges) return null;
            await Run(root, ["stash", "push", "--message", message], ct);
            var stash = await ShaAsync(root, "refs/stash", ct);
            if (stash is null) return null;
            // Never a competing entry: attaching to the active one keeps undo and the stash together.
            var holder = await journal.ActiveAsync(root, ct) ?? await journal.UndoableAsync(root, ct);
            if (holder is not null) await journal.SetStashAsync(holder, stash, ct);
            else await journal.CloseAsync(await journal.OpenAsync(root, "stash", [], ct), JournalPhase.NeedsUser, stash, message: "Stashed changes are waiting to be restored.", ct: ct);
            return stash;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Pops the stash the journal recorded; a conflicting pop is reported as the working-tree state.</summary>
    public async Task<WorkingTreeState> RestoreStashAsync(string root, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var holder = await ActiveJournalAsync(root, ct);
            var stash = holder?.StashRef ?? throw new StackerException("There are no stashed changes to restore.");
            var list = (await Run(root, ["stash", "list", "--format=%H"], ct)).StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var index = Array.IndexOf(list, stash);
            if (index < 0) throw new StackerException("The stashed changes are no longer in the stash list. Inspect them with git stash list before removing anything.");
            var pop = await Run(root, ["stash", "pop", $"stash@{{{index}}}"], ct, allowFailure: true);
            var failed = pop.ExitCode != 0 && !(pop.StdErr + pop.StdOut).Contains("CONFLICT", StringComparison.OrdinalIgnoreCase);
            if (failed) throw new StackerException($"Restoring the stashed changes failed. {pop.StdErr.Trim()}");
            await journal.SetStashAsync(holder!, null, ct);
            return await StatusAsync(root, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Checks out an existing local branch after confirming the working tree can move.</summary>
    public async Task SwitchAsync(string root, string branch, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var name = StackRefs.Short(branch);
            var state = await StatusAsync(root, ct);
            if (state.HasTrackedChanges) throw new StackerException("The working tree has uncommitted changes. Commit or stash them before switching.");
            if (state.HasOperation) throw new StackerException($"{WorkingOperations.Noun(state.Operation)} is in progress. Finish or abort it before switching.");
            if (await ShaAsync(root, "refs/heads/" + name, ct) is null) throw new StackerException($"{name} does not exist.");
            if (string.Equals(state.Branch, name, StringComparison.Ordinal)) return;
            var checkout = await Run(root, ["checkout", name], ct, allowFailure: true);
            if (checkout.ExitCode != 0) throw new StackerException($"Switching to {name} failed. {checkout.StdErr.Trim()}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Confirms the preview still matches the repository, so a stale window cannot rewrite something else.</summary>
    private async Task<string?> VerifyPlanAsync(RestackPlan plan, CancellationToken ct)
    {
        var root = plan.RepositoryRoot;
        var state = await StatusAsync(root, ct);
        if (state.HasTrackedChanges)
            return "The working tree changed after the preview was built. Close it, commit or stash, then open the preview again.";
        if (state.HasOperation)
            return $"{WorkingOperations.Noun(state.Operation)} started after the preview was built. Finish or abort it first.";
        foreach (var step in plan.Steps.Where(step => step.NeedsRebase && !step.Blocked))
        {
            if (await ShaAsync(root, step.Branch, ct) != step.BranchTip) return $"{step.BranchName} moved after the preview was built. Refresh and restack again.";
            if (await ShaAsync(root, step.ParentRef, ct) != step.OldParentTip) return $"{step.ParentName} moved after the preview was built. Refresh and restack again.";
        }
        return null;
    }

    /// <summary>Keeps the newest backup namespaces so recovery stays possible without unbounded ref growth.</summary>
    private async Task PruneBackupsAsync(string root, CancellationToken ct)
    {
        var output = (await Run(root, ["for-each-ref", "--format=%(refname)", BackupRoot], ct)).StdOut;
        var stamps = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(reference => reference.Split('/'))
            .Where(parts => parts.Length > 4)
            .GroupBy(parts => parts[3], StringComparer.Ordinal)
            .OrderByDescending(group => group.Key, StringComparer.Ordinal)
            .Skip(RetainedBackups).Select(group => group.Key).ToArray();
        foreach (var stamp in stamps)
        {
            var references = (await Run(root, ["for-each-ref", "--format=%(refname)", BackupRoot + "/" + stamp], ct)).StdOut;
            foreach (var reference in references.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await Run(root, ["update-ref", "-d", reference], ct, allowFailure: true);
        }
    }

    private async Task<WorkingTreeState> StatusAsync(string root, CancellationToken ct)
    {
        var porcelain = (await Run(root, ["status", "--porcelain=v1", "-z", "--untracked-files=normal"], ct, allowFailure: true)).StdOut;
        var head = await Run(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct, allowFailure: true);
        var branch = head.ExitCode == 0 && head.StdOut.Trim().Length > 0 ? head.StdOut.Trim() : null;
        return new(branch, await OperationAsync(root, ct), WorkingTreeParser.Parse(porcelain));
    }

    private async Task<WorkingOperation> OperationAsync(string root, CancellationToken ct)
    {
        foreach (var (name, operation) in new[] { ("rebase-merge", WorkingOperation.Rebase), ("rebase-apply", WorkingOperation.Rebase), ("MERGE_HEAD", WorkingOperation.Merge), ("CHERRY_PICK_HEAD", WorkingOperation.CherryPick), ("REVERT_HEAD", WorkingOperation.Revert) })
        {
            var path = await GitPathAsync(root, name, ct);
            if (path is not null && (File.Exists(path) || Directory.Exists(path)))
            {
                // git am shares rebase-apply with the apply backend; only it leaves an "applying" marker.
                if (name == "rebase-apply" && await GitPathAsync(root, "rebase-apply/applying", ct) is { } applying && File.Exists(applying))
                    return WorkingOperation.Am;
                return operation;
            }
        }
        return WorkingOperation.None;
    }

    private async Task<string?> GitPathAsync(string root, string name, CancellationToken ct)
    {
        var path = (await Run(root, ["rev-parse", "--git-path", name], ct, allowFailure: true)).StdOut.Trim();
        if (path.Length == 0) return null;
        return Path.IsPathRooted(path) ? path : Path.Combine(root, path);
    }

    /// <summary>Branches checked out in another worktree cannot be rebased here; the main checkout is the user's own and stays allowed.</summary>
    private async Task<IReadOnlyList<string>> OtherWorktreeBranchesAsync(string root, CancellationToken ct)
    {
        var output = (await Run(root, ["worktree", "list", "--porcelain"], ct)).StdOut;
        var branches = new List<string>();
        var index = -1;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) { index++; continue; }
            if (index >= 1 && line.StartsWith("branch ", StringComparison.Ordinal)) branches.Add(line[7..].Trim());
        }
        return branches;
    }

    private async Task<IReadOnlyList<string>> ConflictsAsync(string root, CancellationToken ct)
    {
        var output = (await Run(root, ["diff", "--name-only", "--diff-filter=U", "-z"], ct, allowFailure: true)).StdOut;
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToArray();
    }

    private async Task<string?> ShaAsync(string root, string reference, CancellationToken ct)
    {
        var result = await Run(root, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], ct, allowFailure: true);
        var value = result.StdOut.Trim();
        return result.ExitCode == 0 && value.Length > 0 ? value : null;
    }

    private async Task<string> CurrentBranchRefAsync(string root, CancellationToken ct)
    {
        var result = await Run(root, ["symbolic-ref", "--quiet", "HEAD"], ct, allowFailure: true);
        return result.ExitCode == 0 ? result.StdOut.Trim() : "";
    }

    /// <summary>The branch the user started on; the journal keeps it so recovery works after a restart.</summary>
    private static string OriginalBranch(JournalEntry entry) =>
        entry.Steps.FirstOrDefault(step => step.Kind == "original")?.Target ?? "";

    /// <summary>Returns to the branch the user started on once every ref is settled.</summary>
    private async Task SwitchToAsync(string root, string branch, CancellationToken ct, bool required)
    {
        if (branch.Length == 0) return;
        var name = StackRefs.Short(branch);
        if (string.Equals(await CurrentBranchRefAsync(root, ct), "refs/heads/" + name, StringComparison.Ordinal)) return;
        var result = await Run(root, ["checkout", name], ct, allowFailure: true);
        if (result.ExitCode != 0 && required)
            throw new StackerException($"The branches are in place, but returning to {name} failed: {result.StdErr.Trim()}");
    }

    private static RestackOutcome Refused(string message, JournalEntry? entry = null) => new(RestackStatus.Refused, null, [], message, entry);

    private static string[] RebaseArgs(RestackPlan plan, RestackStep step)
    {
        var args = new List<string> { "rebase", "--onto", step.ParentRef, step.OldParentTip!, step.BranchName };
        if (plan.PreserveMerges) args.Add("--rebase-merges");
        args.AddRange(["--no-autostash", "--no-autosquash", "--empty=drop"]);
        return [.. args];
    }

    private static string[] AbortArgs(WorkingOperation operation) => operation switch
    {
        WorkingOperation.Merge => ["merge", "--abort"],
        WorkingOperation.CherryPick => ["cherry-pick", "--abort"],
        WorkingOperation.Revert => ["revert", "--abort"],
        WorkingOperation.Am => ["am", "--abort"],
        _ => ["rebase", "--abort"]
    };

    // Hooks, editors, signing and rebase.updateRefs are neutralized for internal rewrites so a cascade cannot block, prompt, or move a branch Stacker did not plan.
    private async Task<ProcessResult> Run(string root, string[] args, CancellationToken ct, bool allowFailure = false, TimeSpan? timeout = null, string? editor = null)
    {
        var environment = editor is null
            ? WriteEnvironment
            : new Dictionary<string, string>(WriteEnvironment) { ["GIT_EDITOR"] = editor };
        var result = await runner.RunAsync(new(executable.Resolve(),
            ["-c", "core.hooksPath=/dev/null", "-c", "commit.gpgSign=false", "-c", "rebase.updateRefs=false", .. args],
            root, timeout ?? WriteTimeout, MaxOutputBytes: 4 * 1024 * 1024, Environment: environment), ct);
        if (!allowFailure && result.ExitCode != 0) throw new StackerException($"Git exited with code {result.ExitCode}: {result.StdErr.Trim()}");
        return result;
    }

    /// <summary>A commit the user asked for keeps their hooks and signing configuration.</summary>
    private Task<ProcessResult> RunUser(string root, string[] args, CancellationToken ct) =>
        runner.RunAsync(new(executable.Resolve(), args, root, WriteTimeout, MaxOutputBytes: 4 * 1024 * 1024, Environment: WriteEnvironment), ct);

    private static int ParseCount(string value) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
