using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>How far one journaled effect got.</summary>
public enum StepState { Planned, Done, Failed, Started, Unknown }

/// <summary>One effect inside a command: a branch move, a stash, a commit, a remote write.</summary>
public sealed record JournalStep(string Id, string Kind, string Target, string? OldSha, string? NewSha, StepState State)
{
    public RestackPlan? RestackPlan { get; init; }
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
    public const int CurrentSchema = 3;
    public RestackPlan? RestackPlan { get; init; }
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
    public IReadOnlyList<JournalStep> Restorable => Steps.Where(step => step.OldSha is not null && step.Kind == "rebase"
        && step.State is StepState.Done or StepState.Started or StepState.Unknown).ToArray();
    public JournalStep? Pending => PendingStepId is null ? null : Steps.FirstOrDefault(step => step.Id == PendingStepId);
    public string Summary => Message ?? (Moved.Count == 0 ? Kind : $"{Kind} · {Moved.Count} effect(s)");
}

/// <summary>Stores journal entries; implementations must write before the effect they describe.</summary>
public sealed record SavedStash(string Sha, string EntryId, DateTimeOffset CreatedAt, string Description)
{
    public string Label => $"{CreatedAt:g} · {Description} · {Sha[..Math.Min(7, Sha.Length)]}";
}

public interface IJournal
{
    /// <summary>Writes the entry in the <see cref="JournalPhase.Running"/> phase, before any effect runs.</summary>
    Task<Result<JournalEntry>> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default);
    /// <summary>Records one finished effect; called as soon as the effect is known to have happened.</summary>
    Task<Result<JournalEntry>> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default);
    /// <summary>Closes the entry with its outcome and any pending decision.</summary>
    Task<Result<JournalEntry>> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default);
    /// <summary>Sets or clears the stash this entry is holding, without closing it.</summary>
    Task<Result<JournalEntry>> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default);
    /// <summary>The newest entry for this repository that still needs attention, if any.</summary>
    Task<Result<JournalEntry?>> ActiveAsync(string repository, CancellationToken ct = default);
    /// <summary>The newest closed entry that can still be undone, if any.</summary>
    Task<Result<JournalEntry?>> UndoableAsync(string repository, CancellationToken ct = default);
    Task<Result<JournalEntry?>> ByIdAsync(string repository, string id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<SavedStash>>> StashesAsync(string repository, CancellationToken ct = default)
    {
        var entries = Recent(repository, int.MaxValue);
        return Task.FromResult(entries.Map<IReadOnlyList<SavedStash>>(items => items.Where(e => e.StashRef is not null)
            .Select(e => new SavedStash(e.StashRef!, e.Id, e.StartedAt, e.Kind)).ToArray()));
    }
    Result<IReadOnlyList<JournalEntry>> Recent(string repository, int count = 20);
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

public enum CommandStatus { Applied, Refused, Failed, Cancelled, NeedsUser, PartiallyApplied, RolledBack }

/// <summary>A single, non-contradictory status with the known effects and optional warnings.</summary>
public sealed record CommandOutcome(CommandStatus Status, string Message, Problem? Problem)
{
    public bool Ok => Status == CommandStatus.Applied;
    public bool NeedsUser => Status is CommandStatus.NeedsUser or CommandStatus.PartiallyApplied;
    public bool RolledBack => Status == CommandStatus.RolledBack;
    public IReadOnlyList<string> Effects { get; init; } = [];
    public static CommandOutcome Done(string message) => new(CommandStatus.Applied, message, null);
    public static CommandOutcome Attention(string message) => new(CommandStatus.NeedsUser, message, null);
    public static CommandOutcome Refused(string message) => new(CommandStatus.Refused, message, null);
    public static CommandOutcome Failed(Problem problem) => new(CommandStatus.Failed, problem.Message, problem);
    public static CommandOutcome Restored(string message, Problem? problem = null) => new(CommandStatus.RolledBack, message, problem);
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
