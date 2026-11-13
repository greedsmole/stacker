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
    internal static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

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
    /// The effect list is rebuilt from steps in <see cref="StepState.Done"/>. A rejected or unstarted command
    /// with no earlier effects closes as <see cref="JournalPhase.Dismissed"/>. Other failures go through recovery.
    /// A returned outcome closes an unfinished entry with the matching phase, carrying forward its stash and pending
    /// step. A failed close is resolved from the phase that actually persisted.
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
        CommandOutcome? returned = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var plan = await command.PlanAsync(ct);
            if (!plan.CanRun)
                return CommandOutcome.Refused(string.Join(" ", plan.Blockers));
            ct.ThrowIfCancellationRequested();
            entry = (await journal.OpenAsync(repository, command.Kind, plan.Effects, ct)).OrThrow();
            returned = ct.IsCancellationRequested ? CommandOutcome.CancelledBeforeDispatch()
                : await command.ExecuteAsync(new(entry, journal, progress), ct);
            using var completion = new CancellationTokenSource(CompletionTimeout);
            var known = await ReadRecordedAsync(entry, completion.Token);
            var outcome = returned with
            {
                Effects = CompletedEffects(known)
            };
            returned = outcome;
            var phase = outcome.Status switch
            {
                CommandStatus.Applied => JournalPhase.Applied,
                CommandStatus.RolledBack => JournalPhase.RolledBack,
                CommandStatus.NeedsUser when outcome.HasSeparateRecoveryEntry => JournalPhase.Dismissed,
                CommandStatus.NeedsUser or CommandStatus.PartiallyApplied => JournalPhase.NeedsUser,
                _ => JournalPhase.Dismissed
            };
            if (outcome.Status == CommandStatus.Cancelled && outcome.Certainty != EffectCertainty.NotStarted ||
                outcome.Status == CommandStatus.Failed &&
                (outcome.Certainty is not (EffectCertainty.Rejected or EffectCertainty.NotStarted) ||
                 known.Steps.Any(step => step.State is StepState.Started or StepState.Done or StepState.Unknown)))
                return await ReverseAsync(command, known, outcome);
            if (known.Phase != phase)
                (await journal.CloseAsync(known, phase, pendingStepId: phase == JournalPhase.NeedsUser ? known.PendingStepId : null,
                    message: outcome.Message, ct: completion.Token)).OrThrow();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            if (entry is null)
                return CommandOutcome.CancelledBeforeDispatch();
            return await ReverseAsync(command, entry, new(CommandStatus.Cancelled, "The action was cancelled.", null), returned);
        }
        catch (Exception ex)
        {
            var problem = Boundary.Problem(ex, ProblemCodes.Unknown);
            return entry is null ? CommandOutcome.Failed(problem with
            {
                Certainty = EffectCertainty.NotStarted
            })
                : await ReverseAsync(command, entry, CommandOutcome.Failed(problem), returned);
        }
    }

    /// <summary>Preserves a durable result or compensates an unfinished command with a fresh time budget.</summary>
    private async Task<CommandOutcome> ReverseAsync(IStackCommand command, JournalEntry entry, CommandOutcome failure, CommandOutcome? returned = null)
    {
        using var recovery = new CancellationTokenSource(CompletionTimeout);
        try
        {
            var known = await ReadRecordedAsync(entry, recovery.Token);
            if (TerminalOutcome(known, failure, returned) is { } terminal)
                return terminal;
            if (returned is { Status: CommandStatus.Cancelled, Certainty: EffectCertainty.NotStarted } ||
                failure.Status == CommandStatus.Cancelled && known.Steps.Count > 0 && known.Steps.All(step => step.State == StepState.Planned))
            {
                var cancelled = CommandOutcome.CancelledBeforeDispatch();
                (await journal.CloseAsync(known, JournalPhase.Dismissed, message: cancelled.Message, ct: recovery.Token)).OrThrow();
                return cancelled;
            }
            if (command is IUndoableCommand undoable)
            {
                var outcome = await undoable.UndoAsync(known, recovery.Token);
                if (outcome.RolledBack || outcome.Ok)
                {
                    known = await ReadRecordedAsync(entry, recovery.Token);
                    if (TerminalOutcome(known, failure) is { } restored)
                        return restored;
                    (await journal.CloseAsync(known, JournalPhase.RolledBack, message: failure.Message, ct: recovery.Token)).OrThrow();
                    return AfterRollback(failure, outcome.Message);
                }
            }
            (await journal.CloseAsync(known, JournalPhase.NeedsUser, known.StashRef, known.PendingStepId, failure.Message, recovery.Token)).OrThrow();
            var effects = CompletedEffects(known);
            return new CommandOutcome(effects.Length > 0 ? CommandStatus.PartiallyApplied : CommandStatus.NeedsUser, failure.Message, failure.Problem)
            {
                Effects = effects,
                Certainty = failure.Certainty
            };
        }
        catch (Exception ex)
        {
            try
            {
                if (!recovery.IsCancellationRequested && TerminalOutcome(await ReadRecordedAsync(entry, recovery.Token), failure, returned) is { } terminal)
                    return terminal;
            }
            catch (Exception) { /* The outcome could not be confirmed from storage. */ }
            return CommandOutcome.Attention($"{failure.Message} Recovery could not be confirmed: {ex.Message}") with
            {
                Problem = failure.Problem
            };
        }
    }

    private async Task<JournalEntry> ReadRecordedAsync(JournalEntry entry, CancellationToken ct) =>
        (await journal.ByIdAsync(entry.Repository, entry.Id, ct)).OrThrow()
        ?? throw new StackerException(Problem.Of(ProblemCodes.StateInvalid, "The command journal entry is missing."));

    private static CommandOutcome? TerminalOutcome(JournalEntry entry, CommandOutcome failure, CommandOutcome? returned = null) => entry.Phase switch
    {
        JournalPhase.Applied => CommandOutcome.Done(entry.Summary) with { Effects = CompletedEffects(entry) },
        JournalPhase.RolledBack or JournalPhase.Undone => AfterRollback(failure, entry.Summary),
        JournalPhase.Dismissed => returned ?? failure,
        _ => null
    };

    /// <summary>Reports cancellation after a successful restore, or reports the restore after any other failure.</summary>
    private static CommandOutcome AfterRollback(CommandOutcome failure, string message) => failure.Status == CommandStatus.Cancelled
        ? failure with
        {
            Message = failure.Message + " " + message
        }
        : CommandOutcome.Restored(message, failure.Problem);

    /// <summary>Lists completed effects using the stored steps.</summary>
    private static string[] CompletedEffects(JournalEntry entry) => entry.Steps.Where(step => step.State == StepState.Done)
        .Select(step => step.Kind + ": " + step.Target).ToArray();
}
