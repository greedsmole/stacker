namespace Stacker.Core.Framework;

/// <summary>How far one journaled effect got.</summary>
public enum StepState { Planned, Done, Failed }

/// <summary>One effect inside a command: a branch move, a stash, a commit, a remote write.</summary>
public sealed record JournalStep(string Id, string Kind, string Target, string? OldSha, string? NewSha, StepState State)
{
    public static JournalStep Planned(string id, string kind, string target) => new(id, kind, target, null, null, StepState.Planned);
    public JournalStep Finished(string? oldSha, string? newSha, StepState state) => this with { OldSha = oldSha, NewSha = newSha, State = state };
}

/// <summary>How a command ended; <see cref="Running"/> means it never closed and needs recovery.</summary>
public enum JournalPhase { Running, Applied, NeedsUser, RolledBack, Undone, Dismissed }

/// <summary>What the user chose for a journal entry that still needs attention.</summary>
public enum RecoveryChoice { Resume, Undo, Dismiss }

/// <summary>Write-ahead record of one command; the only thing that makes an effect recoverable.</summary>
public sealed record JournalEntry(int SchemaVersion, string Id, string Repository, string Kind,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, JournalPhase Phase, IReadOnlyList<JournalStep> Steps,
    string? StashRef, string? PendingStepId, string? Message)
{
    public const int CurrentSchema = 2;
    /// <summary>An entry the user must deal with: still running, or stopped for a decision.</summary>
    public bool NeedsAttention => Phase is JournalPhase.Running or JournalPhase.NeedsUser;
    /// <summary>Whether the effects recorded here can still be reversed.</summary>
    public bool CanUndo => Phase is JournalPhase.Running or JournalPhase.Applied or JournalPhase.NeedsUser;
    /// <summary>Effects that actually moved something and must be reversed by an undo.</summary>
    public IReadOnlyList<JournalStep> Moved => Steps.Where(step => step.State == StepState.Done && step.OldSha is not null).ToArray();
    /// <summary>
    /// Every intended effect whose original commit is known, including one whose record never landed.
    /// Recovery reverses these, which is what makes a crash between an effect and its record recoverable.
    /// </summary>
    public IReadOnlyList<JournalStep> Restorable => Steps.Where(step => step.OldSha is not null && step.Kind != "original").ToArray();
    public JournalStep? Pending => PendingStepId is null ? null : Steps.FirstOrDefault(step => step.Id == PendingStepId);
    public string Summary => Message ?? (Moved.Count == 0 ? Kind : $"{Kind} · {Moved.Count} effect(s)");
}

/// <summary>Stores journal entries; implementations must write before the effect they describe.</summary>
public interface IJournal
{
    /// <summary>Writes the entry in the <see cref="JournalPhase.Running"/> phase, before any effect runs.</summary>
    Task<JournalEntry> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default);
    /// <summary>Records one finished effect; called as soon as the effect is known to have happened.</summary>
    Task<JournalEntry> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default);
    /// <summary>Closes the entry with its outcome and any pending decision.</summary>
    Task<JournalEntry> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default);
    /// <summary>Sets or clears the stash this entry is holding, without closing it.</summary>
    Task<JournalEntry> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default);
    /// <summary>The newest entry for this repository that still needs attention, if any.</summary>
    Task<JournalEntry?> ActiveAsync(string repository, CancellationToken ct = default);
    /// <summary>The newest closed entry that can still be undone, if any.</summary>
    Task<JournalEntry?> UndoableAsync(string repository, CancellationToken ct = default);
    Task<JournalEntry?> ByIdAsync(string repository, string id, CancellationToken ct = default);
    IReadOnlyList<JournalEntry> Recent(string repository, int count = 20);
}

/// <summary>Reads time so commands and tests share one clock.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

/// <summary>What a command decided before running: the blockers and the effects it would cause.</summary>
public sealed record CommandPlan(bool CanRun, IReadOnlyList<string> Blockers, IReadOnlyList<JournalStep> Effects)
{
    public static CommandPlan Blocked(params string[] blockers) => new(false, blockers, []);
    public static CommandPlan Ready(IReadOnlyList<JournalStep> effects) => new(true, [], effects);
    public static CommandPlan Ready() => new(true, [], []);
}

/// <summary>The outcome the UI shows; nothing else reports results.</summary>
public sealed record CommandOutcome(bool Ok, bool NeedsUser, bool RolledBack, string Message, Problem? Problem)
{
    public static CommandOutcome Done(string message) => new(true, false, false, message, null);
    /// <summary>The command stopped and the user must act (a conflict to resolve).</summary>
    public static CommandOutcome Attention(string message) => new(false, true, false, message, null);
    public static CommandOutcome Refused(string message) => new(false, false, false, message, null);
    public static CommandOutcome Failed(Problem problem) => new(false, false, false, problem.Message, problem);
    public static CommandOutcome Restored(string message, Problem? problem = null) => new(false, false, true, message, problem);
}

/// <summary>Everything a command may use while it runs; effects go through the journal.</summary>
public sealed record CommandContext(JournalEntry Entry, IJournal Journal, IProgress<string>? Progress);

/// <summary>A user-visible mutation; the only code allowed to change git, disk or GitHub state.</summary>
public interface IStackCommand
{
    string Kind { get; }
    string Describe();
    /// <summary>Read-only preconditions; a plan that cannot run never opens a journal entry.</summary>
    Task<CommandPlan> PlanAsync(CancellationToken ct = default);
    Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default);
}

/// <summary>A command whose effects can be reversed from its journal entry alone.</summary>
public interface IUndoableCommand : IStackCommand
{
    Task<CommandOutcome> UndoAsync(JournalEntry entry, CancellationToken ct = default);
}

/// <summary>Runs commands under one journal entry each, so every effect is recoverable.</summary>
public sealed class CommandRunner(IJournal journal)
{
    /// <summary>Plans, journals, executes, and reverses on any failure; never throws for a normal failure.</summary>
    public async Task<CommandOutcome> RunAsync(string repository, IStackCommand command, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        CommandPlan plan;
        try { plan = await command.PlanAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return CommandOutcome.Failed(Problem.Of(ProblemCodes.Unknown, ex.Message)); }
        if (!plan.CanRun) return CommandOutcome.Refused(string.Join(" ", plan.Blockers));
        var entry = await journal.OpenAsync(repository, command.Kind, plan.Effects, ct);
        try
        {
            var outcome = await command.ExecuteAsync(new(entry, journal, progress), ct);
            var phase = outcome.NeedsUser ? JournalPhase.NeedsUser : outcome.Ok ? JournalPhase.Applied : JournalPhase.RolledBack;
            await journal.CloseAsync(entry, phase, message: outcome.Message, ct: ct);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            await ReverseAsync(repository, command, entry, "The action was cancelled", ct);
            throw;
        }
        catch (Exception ex)
        {
            var problem = Problem.Of(ProblemCodes.Unknown, ex.Message);
            return await ReverseAsync(repository, command, entry, ex.Message, ct, problem);
        }
    }

    /// <summary>Reverses a partially applied command so the repository is never left half-changed.</summary>
    private async Task<CommandOutcome> ReverseAsync(string repository, IStackCommand command, JournalEntry entry, string reason, CancellationToken ct, Problem? problem = null)
    {
        var known = await journal.ByIdAsync(repository, entry.Id, ct) ?? entry;
        if (command is IUndoableCommand undoable)
        {
            try
            {
                var undone = await undoable.UndoAsync(known, ct);
                await journal.CloseAsync(known, JournalPhase.RolledBack, message: reason, ct: ct);
                return CommandOutcome.Restored(undone.Message, problem);
            }
            catch (Exception undoFailure)
            {
                await journal.CloseAsync(known, JournalPhase.NeedsUser, message: reason, ct: ct);
                return CommandOutcome.Failed(Problem.Of(ProblemCodes.Unknown, $"{reason} Automatic restore also failed: {undoFailure.Message}"));
            }
        }
        await journal.CloseAsync(known, JournalPhase.NeedsUser, message: reason, ct: ct);
        return CommandOutcome.Failed(problem ?? Problem.Of(ProblemCodes.Unknown, reason));
    }
}
