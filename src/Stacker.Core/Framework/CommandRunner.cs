namespace Stacker.Core.Framework;

/// <summary>Owns execution and the durable outcome; compensates only commands that support it.</summary>
public sealed class CommandRunner(IJournal journal)
{
    public Result<IReadOnlyList<JournalEntry>> Recent(string scope)=>journal.Recent(scope);
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
            if (entry is null) return new(CommandStatus.Cancelled, "The action was cancelled before execution.", null){Certainty=EffectCertainty.NotStarted};
            var recovered = await ReverseAsync(command, entry, "The action was cancelled", null);
            return recovered.RolledBack ? recovered with { Status = CommandStatus.Cancelled, Message = "The action was cancelled. " + recovered.Message } : recovered;
        }
        catch (Exception ex)
        {
            var problem = Boundary.Problem(ex, ProblemCodes.Unknown);
            return entry is null ? CommandOutcome.Failed(problem with{Certainty=EffectCertainty.NotStarted}) : await ReverseAsync(command, entry, ex.Message, problem);
        }
    }

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
