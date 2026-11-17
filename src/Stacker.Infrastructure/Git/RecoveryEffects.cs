using System.Globalization;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Restores, dismisses or undoes the effects recorded in a journal entry, and only ever moves a ref whose
/// current commit it can prove belongs to that operation.</summary>
/// <remarks>Recovery is the only path allowed to reverse an interrupted command, so it is written to preserve
/// unrelated work rather than to reach a tidy end state. Three rules decide every decision here: an unfinished Git
/// operation is aborted first, because a rebase in progress keeps the index and working tree in a state a ref move
/// cannot cleanly pass through; a working tree with tracked changes refuses recovery, because moving a branch under
/// uncommitted work would silently carry those edits to another commit or discard them; and a ref is restored only
/// when the current commit equals the new commit the journal recorded for it, which is the proof that this operation
/// produced it. A ref that moved since is preserved and reported for inspection instead.
/// <para>Only recorded rebase steps are restorable (<see cref="JournalEntry.Restorable"/>). Remote writes are excluded
/// because a push cannot be retracted and no local read proves what origin holds, so a push step's unknown outcome is
/// handed to the user instead of being compensated. What "unknown" means per effect: for a rebase the branch may
/// already point at the new commit, so recovery compares commits and restores only on a match; for a push the remote
/// is never touched; for a stash the recorded commit id is the evidence, and restoring it is offered separately,
/// outside this class.</para>
/// <para>Every restore runs as a single <c>update-ref</c> transaction with an expected-current value per ref, so a
/// concurrent change makes the whole transaction fail with no branch moved, and the entry is closed only after that
/// transaction returned. Closing is what makes a rollback claim durable: a rollback with no stored evidence would be
/// indistinguishable from a crash.</para></remarks>
/// <param name="git">Runner that owns the write gate, the ref reads and the transactional restore.</param>
/// <param name="journal">Journal holding the write-ahead record that recovery reads and closes.</param>
internal sealed class RecoveryEffects(GitRunner git, IJournal journal)
{
    /// <summary>Aborts whatever Git operation is in progress and restores the branches an interrupted restack had
    /// already moved, then closes its entry as rolled back.</summary>
    /// <remarks>An unfinished restack owns a stopped rebase only when its recorded branch and commits match Git's
    /// worktree markers. A separate Git operation is aborted on its own. With no Git operation, the unfinished restack
    /// can still restore its recorded refs. A failed restore leaves the entry available for another attempt.</remarks>
    /// <param name="root">Repository root to recover.</param>
    /// <param name="ct">Cancels the abort and the restore.</param>
    /// <param name="entryId">The restack selected for restore, or null to abort the current Git operation.</param>
    /// <returns>A rolled-back outcome whose message counts the branches that were actually restored, with the closed
    /// entry or null when no entry was involved.</returns>
    /// <exception cref="StackerException">The working tree gained tracked changes, or a ref cannot be proven to belong
    /// to the operation.</exception>
    public async Task<RestackOutcome> AbortAsync(string root, CancellationToken ct, string? entryId)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var entry = entryId is null ? (await journal.ActiveAsync(root, ct)).OrThrow()
                : (await journal.ByIdAsync(root, entryId, ct)).OrThrow();
            if (entry is not { Kind: "restack", NeedsAttention: true })
                entry = null;
            if (entryId is not null && entry is null)
                return git.Refused("The interrupted restack is no longer available. Refresh the recovery list.");
            var operation = await git.OperationAsync(root, ct);
            if (operation != WorkingOperation.None &&
                (operation != WorkingOperation.Rebase || await FindStoppedRebaseAsync(root, entry, ct) is null))
            {
                if (entryId is not null)
                    return git.Refused("A separate Git operation is in progress. Finish or abort it before restoring the restack.", entry);
                await git.Run(root, git.AbortArgs(operation), ct);
                return new(RestackStatus.RolledBack, null, [], "The Git operation was aborted.", null);
            }
            if (entry is null)
                return git.Refused("There is no interrupted restack to restore. Inspect any uncertain effects before dismissing their record.");
            int restored;
            try
            {
                restored = await RestoreAsync(root, entry, git.OriginalBranch(entry), ct, close: false);
            }
            catch (Exception ex)
            {
                return git.Refused($"Could not complete restack recovery. {ex.Message}", entry);
            }
            var message = restored == 0 ? "The operation was aborted." : $"The operation was aborted and {restored} branch(es) were restored.";
            return await CompleteRecoveryAsync(entry, JournalPhase.RolledBack, message);
        }
        finally { git.Gate.Release(); }
    }

    /// <summary>Finds the interrupted layer using Git's worktree markers, even if the pending id was never saved.</summary>
    internal async Task<JournalStep?> FindStoppedRebaseAsync(string root, JournalEntry? entry, CancellationToken ct)
    {
        if (entry is not { Kind: "restack", NeedsAttention: true })
            return null;
        foreach (var step in entry.Steps)
        {
            if (step.Kind != "rebase" || step.OldSha is null)
                continue;
            var legacyPending = entry.SchemaVersion == 2 && step.Id == entry.PendingStepId && step.State == StepState.Planned;
            if (!legacyPending && step.State is not (StepState.Started or StepState.Unknown))
                continue;
            string? onto = null;
            if (entry.RestackPlan is { } plan)
            {
                var approved = plan.Steps.FirstOrDefault(layer => layer.Branch == step.Target && layer.NeedsRebase && !layer.Blocked);
                if (approved is null || (onto = ExpectedParent(entry, approved)) is null)
                    continue;
            }
            if (await git.MatchesRebaseAsync(root, step, onto, ct))
                return step;
        }
        return null;
    }

    internal static string? ExpectedParent(JournalEntry entry, RestackStep step) =>
        entry.Moved.LastOrDefault(moved => moved.Target == step.ParentRef)?.NewSha ?? step.OldParentTip;

    /// <summary>Restores every branch the entry records as moved, after proving ownership of each one, in a single ref
    /// transaction.</summary>
    /// <remarks>Callers must hold the write gate; this method does not take it, so <see cref="AbortAsync"/> can restore
    /// inside one gate acquisition. The owned rebase is aborted first, so the index is
    /// clean and a checkout can follow. The tree is then checked again for tracked changes, because an abort can leave
    /// edits behind, and recovery refuses rather than carrying them onto another commit. Branches checked out in
    /// another worktree are collected once, before the loop, so the same snapshot decides every step. A step whose ref
    /// already points at the old commit is skipped, which is what makes a retried restore succeed after a partial
    /// earlier attempt.
    /// <para>Each remaining step must currently point at the recorded new commit; a missing ref, a ref without a
    /// recorded new commit, a ref that moved, or one checked out elsewhere stops the whole restore before any ref is
    /// written, and the message says the current commit was preserved. The proven set is then applied together, so
    /// either every branch moves or none does; the checkout afterwards returns the user to the branch they started
    /// on.</para></remarks>
    /// <param name="root">Repository root whose refs are restored.</param>
    /// <param name="entry">Entry holding the recorded moves.</param>
    /// <param name="originalBranch">Branch to check out afterwards; an empty value keeps the current branch.</param>
    /// <param name="ct">Cancels the abort, the checks and the restore.</param>
    /// <param name="close">Whether to close the entry as rolled back once the refs are restored; false leaves it to
    /// the caller, which is how an abort keeps its own wording.</param>
    /// <returns>The number of branches actually moved by this call, which excludes refs that were already restored.
    /// </returns>
    /// <exception cref="StackerException">The working tree has tracked changes, or a ref cannot be proven to belong to
    /// the operation.</exception>
    public async Task<int> RestoreAsync(string root, JournalEntry entry, string originalBranch, CancellationToken ct, bool close = true)
    {
        var operation = await git.OperationAsync(root, ct);
        if (operation != WorkingOperation.None)
        {
            if (operation != WorkingOperation.Rebase || await FindStoppedRebaseAsync(root, entry, ct) is null)
                throw new StackerException("A separate Git operation is in progress. Finish or abort it before restoring the restack.");
            await git.Run(root, git.AbortArgs(operation), ct);
        }
        if ((await git.StatusAsync(root, ct)).HasTrackedChanges)
            throw new StackerException("Recovery stopped: the working tree contains new changes. Commit or stash them first.");
        var moves = new List<JournalStep>();
        var elsewhere = await git.OtherWorktreeBranchesAsync(root, ct);
        foreach (var step in entry.Restorable)
        {
            var sha = await git.ShaAsync(root, step.Target, ct);
            if (sha == step.OldSha)
                continue;
            if (sha is null || step.NewSha is null || sha != step.NewSha || elsewhere.Contains(step.Target))
                throw new StackerException($"Recovery cannot prove ownership of {StackRefs.Short(step.Target)}. Its current commit was preserved; inspect Git before continuing.");
            moves.Add(step);
        }
        await git.RestoreRefsAsync(root, moves, originalBranch, ct);
        if (close)
            (await journal.CloseAsync(entry, JournalPhase.RolledBack, message: $"Every branch was restored ({moves.Count}).", ct: ct)).OrThrow();
        return moves.Count;
    }

    /// <summary>The newest entry that can still be undone, or <see langword="null"/> when there is none.</summary>
    /// <remarks>A journal read failure is unwrapped into a thrown problem rather than reported as "nothing to undo", so
    /// a damaged journal is never mistaken for an empty history.</remarks>
    /// <param name="root">Repository scope to search.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The undoable entry, or null when no entry moved a branch.</returns>
    public Task<JournalEntry?> LastJournalAsync(string root, CancellationToken ct = default) => Boundary.UnwrapAsync(journal.UndoableAsync(root, ct));

    /// <summary>The entry the recovery surface should offer: the unfinished or needs-user restack first, otherwise the
    /// newest undoable one.</summary>
    /// <remarks>An unfinished restack outranks every other entry because it owns the repository's working state; only
    /// when nothing needs attention does the undoable restack become the candidate, which is what keeps a finished but
    /// still reversible restack reachable after a restart.</remarks>
    /// <param name="root">Repository scope to search.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The entry to attend to, or null when there is nothing outstanding.</returns>
    public async Task<JournalEntry?> ActiveJournalAsync(string root, CancellationToken ct = default) =>
        (await journal.ActiveAsync(root, ct)).OrThrow() ?? (await journal.UndoableAsync(root, ct)).OrThrow();

    /// <summary>Closes the active entry as dismissed without changing any ref, so its effects stop being reported while
    /// staying exactly where they are.</summary>
    /// <remarks>This is for effects the user has inspected and decided not to reverse, such as a push whose outcome is
    /// unknown. Nothing is restored, so a dismissed entry cannot be undone later; the stored message is passed back
    /// unchanged, because closing with no message would clear it and the history would lose the explanation.</remarks>
    /// <param name="root">Repository scope holding the entry.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <param name="target">Entry to dismiss, or null to use the active-or-undoable entry.</param>
    /// <exception cref="StackerException">There is nothing to dismiss in this repository.</exception>
    public async Task DismissAsync(string root, CancellationToken ct = default, JournalEntry? target = null)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var entry = target ?? await ActiveJournalAsync(root, ct) ?? throw new StackerException("There is nothing to dismiss in this repository.");
            (await journal.CloseAsync(entry, JournalPhase.Dismissed, message: entry.Message, ct: ct)).OrThrow();
        }
        finally { git.Gate.Release(); }
    }

    /// <summary>Reverses a restack that finished successfully by putting every branch back on the commit recorded before
    /// it, then closes the entry as undone.</summary>
    /// <remarks>Undo inspects every rebase that may have run. Refs already restored to their old commit can be skipped
    /// on retry. A changed ref must match the recorded new commit before it can be restored. Every candidate is checked
    /// before the single ref transaction runs, so an unproven rewrite prevents partial undo. The entry closes as
    /// <see cref="JournalPhase.Undone"/> after the local refs and checkout are restored.</remarks>
    /// <param name="root">Repository root whose branches are restored.</param>
    /// <param name="ct">Cancels the checks and the restore.</param>
    /// <returns>A rolled-back outcome mentioning the branch count, with the closed entry attached.</returns>
    /// <exception cref="StackerException">There is no restack to undo, the tree has changes or an operation in progress,
    /// or a branch moved since the restack.</exception>
    public async Task<RestackOutcome> UndoLastAsync(string root, CancellationToken ct = default)
    {
        await git.Gate.WaitAsync(ct);
        try
        {
            var entry = (await journal.UndoableAsync(root, ct)).OrThrow() ?? throw UndoBlocked("There is no restack to undo in this repository.");
            var state = await git.StatusAsync(root, ct);
            if (state.HasTrackedChanges)
                throw UndoBlocked("The working tree has uncommitted changes. Commit or stash them before undoing.");
            if (state.HasOperation)
                throw UndoBlocked($"{WorkingOperations.Noun(state.Operation)} is in progress. Finish or abort it before undoing.");
            var elsewhere = await git.OtherWorktreeBranchesAsync(root, ct);
            var moves = new List<JournalStep>();
            foreach (var step in entry.Restorable)
            {
                var sha = await git.ShaAsync(root, step.Target, ct);
                if (sha == step.OldSha)
                    continue;
                if (elsewhere.Contains(step.Target))
                    throw UndoBlocked($"{StackRefs.Short(step.Target)} is checked out in another worktree.");
                if (sha is null || step.NewSha is null || sha != step.NewSha)
                    throw UndoBlocked($"Undo cannot prove ownership of {StackRefs.Short(step.Target)}. Inspect the repository before restoring any branches.");
                moves.Add(step);
            }
            try
            {
                await git.RestoreRefsAsync(root, moves, git.OriginalBranch(entry), ct);
            }
            catch (Exception ex)
            {
                return git.Refused($"Could not complete Undo. Check the repository and retry Undo to finish recovery. {ex.Message}", entry);
            }
            return await CompleteRecoveryAsync(entry, JournalPhase.Undone,
                $"Restored {entry.Moved.Count} branch(es) to their commits before the restack. Remote branches were not changed.");
        }
        finally { git.Gate.Release(); }
    }

    private static StackerException UndoBlocked(string message) => new(Problem.Of(ProblemCodes.GitFailed, message)
        with
    {
        Certainty = EffectCertainty.NotStarted
    });

    private async Task<RestackOutcome> CompleteRecoveryAsync(JournalEntry entry, JournalPhase phase, string message)
    {
        using var completion = new CancellationTokenSource(GitRunner.RecoveryTimeout);
        try
        {
            entry = (await journal.CloseAsync(entry, phase, message: message, ct: completion.Token)).OrThrow();
        }
        catch (Exception ex)
        {
            var stored = (await journal.ByIdAsync(entry.Repository, entry.Id, completion.Token)).OrThrow();
            if (stored is null)
                throw;
            if (stored.Phase != phase)
                return git.Refused($"{message} Could not finish recording recovery. Retry recovery. {ex.Message}", stored);
            entry = stored;
        }
        return new(RestackStatus.RolledBack, null, [], message, entry);
    }
}
