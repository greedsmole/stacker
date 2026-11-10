namespace Stacker.Core.Framework;

/// <summary>Owns execution and the durable outcome; compensates only commands that support it.</summary>
/// <param name="journal">Write-ahead journal that makes an interrupted effect recoverable.</param>
/// <remarks>The runner owns the journal lifecycle for every command it runs: a command plans read-only, the runner
/// records that plan as <see cref="JournalPhase.Running"/>, executes, re-reads the durable entry to learn what the
/// adapter recorded, and closes it with the phase that matches the outcome. Adapters may advance step state through
/// the same journal while the effect runs. Returned failures, thrown exceptions and cancellation all take the same
/// route while the process is alive, so a command that ends is normally closed; a process death or a failed recovery
/// write still leaves Running for restart recovery. Compensation is limited to <see cref="IUndoableCommand"/> and to
/// an undo that succeeds; adapters that already restored their own effects report
/// <see cref="RestackStatus.RolledBack"/> instead. Remote publication is never retried and is never described as
/// rolled back.</remarks>
public sealed class CommandRunner(IJournal journal)
{
    /// <summary>Reads the newest journal entries for a scope.</summary>
    /// <param name="scope">The repository root or <c>github:</c> identity used when the entries were opened.</param>
    /// <returns>At most 20 entries, newest first, or a failed result when the journal cannot be read.</returns>
    /// <remarks>Read-only: it neither opens nor closes an entry and can run while a command is executing.</remarks>
    public Result<IReadOnlyList<JournalEntry>> Recent(string scope) => journal.Recent(scope);
    /// <summary>Plans, journals, executes and closes one command; every exit path becomes a
    /// <see cref="CommandOutcome"/>.</summary>
    /// <param name="repository">The journal scope key: a canonical repository root for local work, or a
    /// <c>github:</c> identity for remote publication.</param>
    /// <param name="command">The mutation to plan and execute.</param>
    /// <param name="progress">Optional per-step progress messages passed through to the command context.</param>
    /// <param name="ct">The bus-linked token; cancelling it starts the cancellation path below.</param>
    /// <returns>The outcome describing what happened, which effects are known done, and what still needs the user.
    /// The typed value stays on the command instance, not here.</returns>
    /// <remarks>The plan is read-only: when <see cref="CommandPlan.CanRun"/> is false the blockers are joined with
    /// spaces, returned as <see cref="CommandStatus.Refused"/>, and no journal entry is written. Otherwise the
    /// planned effects are recorded as a <see cref="JournalPhase.Running"/> entry before the command can touch
    /// anything, which is what makes a crash mid-effect recoverable. After execution the entry is re-read by id:
    /// adapters write step state through the journal themselves and the copy the command held may be stale, so the
    /// durable entry is the only trustworthy source for the reported effects, the saved stash and the pending step.
    /// The effect list is rebuilt from steps in <see cref="StepState.Done"/> as "kind: target", replacing whatever
    /// the command returned. A failed command with <see cref="EffectCertainty.Rejected"/> or
    /// <see cref="EffectCertainty.NotStarted"/> proves nothing ran, so its entry closes as
    /// <see cref="JournalPhase.Dismissed"/> without compensation. Every other failure, including
    /// <see cref="EffectCertainty.Unknown"/> where the boundary cannot tell whether a remote side accepted the write,
    /// goes to <see cref="ReverseAsync"/> and is preserved for the user or undone, never retried. A non-failed outcome
    /// closes as NeedsUser, RolledBack, Applied or Dismissed from its own flags, carrying forward the stash and
    /// pending step the adapter recorded. If the close write itself fails, the exception path takes over and the
    /// entry is driven through recovery rather than being reported as applied without durable evidence.
    /// A cancellation before the runner has an entry, during <see cref="IStackCommand.PlanAsync"/> or while the
    /// journal open is still in flight, reports Cancelled with certainty NotStarted and attempts no compensation:
    /// nothing had run. Once an entry exists, cancellation compensates on a fresh budget; a successful undo rewrites
    /// the status to Cancelled and prefixes the message, while an outcome that still needs the user is returned so a
    /// cancellation is never reported as clean. A thrown exception is mapped through <see cref="Boundary.Problem"/>
    /// with <see cref="ProblemCodes.Unknown"/>: before the entry it becomes Failed with certainty NotStarted because
    /// nothing executed, and after it the recovery path runs with the exception message as the reason. The original
    /// problem survives on the returned outcome, so the caller still sees a catalogued code.</remarks>
    public async Task<CommandOutcome> RunAsync(string repository, IStackCommand command, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        JournalEntry? entry = null;
        try
        {
            var plan = await command.PlanAsync(ct);
            if (!plan.CanRun) return CommandOutcome.Refused(string.Join(" ", plan.Blockers));
            entry = (await journal.OpenAsync(repository, command.Kind, plan.Effects, ct)).OrThrow();
            var outcome = await command.ExecuteAsync(new(entry, journal, progress), ct);
            var known = (await journal.ByIdAsync(repository, entry.Id, ct)).OrThrow() ?? entry;
            outcome = outcome with { Effects = known.Steps.Where(step => step.State == StepState.Done).Select(step => step.Kind + ": " + step.Target).ToArray() };
            if (outcome.Status == CommandStatus.Failed && outcome.Certainty is EffectCertainty.Rejected or EffectCertainty.NotStarted)
            { (await journal.CloseAsync(known, JournalPhase.Dismissed, message: outcome.Message, ct: ct)).OrThrow(); return outcome; }
            if (outcome.Status == CommandStatus.Failed)
                return await ReverseAsync(command, known, outcome.Message, outcome.Problem);
            var phase = outcome.NeedsUser ? JournalPhase.NeedsUser : outcome.RolledBack ? JournalPhase.RolledBack
                : outcome.Ok ? JournalPhase.Applied : JournalPhase.Dismissed;
            (await journal.CloseAsync(known, phase, known.StashRef, known.PendingStepId, outcome.Message, ct)).OrThrow();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            if (entry is null) return new(CommandStatus.Cancelled, "The action was cancelled before execution.", null) { Certainty = EffectCertainty.NotStarted };
            var recovered = await ReverseAsync(command, entry, "The action was cancelled", null);
            return recovered.RolledBack ? recovered with { Status = CommandStatus.Cancelled, Message = "The action was cancelled. " + recovered.Message } : recovered;
        }
        catch (Exception ex)
        {
            var problem = Boundary.Problem(ex, ProblemCodes.Unknown);
            return entry is null ? CommandOutcome.Failed(problem with { Certainty = EffectCertainty.NotStarted }) : await ReverseAsync(command, entry, ex.Message, problem);
        }
    }

    /// <summary>Compensates a command whose effects may have landed, then closes the entry with what is known.</summary>
    /// <param name="command">The failed command; only an <see cref="IUndoableCommand"/> is offered an undo.</param>
    /// <param name="entry">The entry the runner is holding, used to look up the durable version.</param>
    /// <param name="reason">The user-facing reason, usually the failure message or "The action was cancelled".</param>
    /// <param name="problem">The original problem preserved on the returned outcome; null for a cancellation.</param>
    /// <returns>RolledBack when compensation succeeded, PartiallyApplied when some effects are known done, or
    /// NeedsUser when nothing could be undone. A recovery failure returns NeedsUser with a message saying the
    /// outcome could not be confirmed.</returns>
    /// <remarks>Recovery gets its own 30-second cancellation source because the caller's token is usually already
    /// cancelled: cleanup may run for at most 30 seconds, and the timeout value has no recorded rationale beyond
    /// bounding how long the bus can spend on an undo. If that budget expires inside a journal write or the undo, the
    /// catch below reports the operation as needing the user instead of letting an unhandled cancellation escape.
    /// The entry is re-read first, so steps recorded after the runner's snapshot are included. Only an undo reporting
    /// <see cref="CommandStatus.RolledBack"/> or <see cref="CommandStatus.Applied"/> counts as success. The runner
    /// closes <see cref="JournalPhase.RolledBack"/> and reports RolledBack only after that write returns, so a
    /// rollback claim always has durable evidence. A command that is not undoable, or an undo that refused or failed,
    /// leaves the entry in <see cref="JournalPhase.NeedsUser"/> with the saved stash and pending step intact and
    /// reports the completed effects: this is why an unknown remote outcome becomes a user decision rather than a
    /// retry. The undo's own message is used only on success; otherwise the original reason stays the message. Any
    /// exception, including the recovery timeout, returns <see cref="CommandOutcome.Attention"/> with the original
    /// reason and problem, and the entry keeps whatever phase it had, which still needs attention.</remarks>
    private async Task<CommandOutcome> ReverseAsync(IStackCommand command, JournalEntry entry, string reason, Problem? problem)
    {
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var known = (await journal.ByIdAsync(entry.Repository, entry.Id, recovery.Token)).OrThrow() ?? entry;
            if (command is IUndoableCommand undoable)
            {
                var outcome = await undoable.UndoAsync(known, recovery.Token);
                if (outcome.RolledBack || outcome.Ok)
                {
                    (await journal.CloseAsync(known, JournalPhase.RolledBack, message: reason, ct: recovery.Token)).OrThrow();
                    return CommandOutcome.Restored(outcome.Message, problem);
                }
            }
            (await journal.CloseAsync(known, JournalPhase.NeedsUser, known.StashRef, known.PendingStepId, reason, recovery.Token)).OrThrow();
            var effects = known.Steps.Where(step => step.State == StepState.Done).Select(step => step.Kind + ": " + step.Target).ToArray();
            return new CommandOutcome(effects.Length > 0 ? CommandStatus.PartiallyApplied : CommandStatus.NeedsUser, reason, problem) { Effects = effects };
        }
        catch (Exception ex)
        {
            return CommandOutcome.Attention($"{reason} Recovery could not be confirmed: {ex.Message}") with { Problem = problem };
        }
    }
}
