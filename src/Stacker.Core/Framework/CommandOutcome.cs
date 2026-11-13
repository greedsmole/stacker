using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>A single, non-contradictory status with the known effects and optional warnings.</summary>
/// <remarks>The status, message and problem are chosen together; <see cref="Ok"/>, <see cref="NeedsUser"/> and
/// <see cref="RolledBack"/> derive from the status so callers never interpret two flags. <see cref="Certainty"/>
/// defaults to <see cref="EffectCertainty.Unknown"/> and the factories set only what they can prove, which is how
/// "safe to retry" is decided. The runner fills <see cref="Effects"/> from durable steps; the command bus adds
/// <see cref="OperationId"/> and <see cref="Scope"/>.</remarks>
/// <param name="Status">The single final status.</param>
/// <param name="Message">Display-ready text for the status line and operations list.</param>
/// <param name="Problem">The catalogued failure when one exists; <see langword="null"/> for a success or a plain
/// refusal.</param>
public sealed record CommandOutcome(CommandStatus Status, string Message, Problem? Problem)
{
    /// <summary>Correlation id assigned by the command bus from the ambient operation; the same id names the
    /// journal entry and the diagnostic record.</summary>
    public string? OperationId
    {
        get; init;
    }
    /// <summary>Repository scope the operation belongs to (a local scope or a GitHub context identity), used to
    /// group the operations list.</summary>
    public string? Scope
    {
        get; init;
    }
    /// <summary>What is known about effects that were attempted; defaults to
    /// <see cref="EffectCertainty.Unknown"/> because a stop without proof must not be presented as safe to
    /// retry.</summary>
    public EffectCertainty Certainty { get; init; } = EffectCertainty.Unknown;
    /// <summary>True only for <see cref="CommandStatus.Applied"/>.</summary>
    public bool Ok => Status == CommandStatus.Applied;
    /// <summary>True for <see cref="CommandStatus.NeedsUser"/> and <see cref="CommandStatus.PartiallyApplied"/>;
    /// both require the user's attention.</summary>
    public bool NeedsUser => Status is CommandStatus.NeedsUser or CommandStatus.PartiallyApplied;
    /// <summary>True when the status is <see cref="CommandStatus.RolledBack"/>.</summary>
    public bool RolledBack => Status == CommandStatus.RolledBack;
    /// <summary>Human-readable descriptions of effects that completed, filled by the runner from durable steps;
    /// empty for a refusal.</summary>
    public IReadOnlyList<string> Effects { get; init; } = [];
    /// <summary>Whether an interrupted operation already has a different journal entry for recovery.</summary>
    internal bool HasSeparateRecoveryEntry
    {
        get; init;
    }
    /// <summary>A successful outcome; certainty is confirmed because the command returned after its effects.</summary>
    /// <param name="message">Display-ready success text.</param>
    /// <returns>An applied outcome with no problem.</returns>
    public static CommandOutcome Done(string message) => new(CommandStatus.Applied, message, null) { Certainty = EffectCertainty.Confirmed };
    /// <summary>Stops for a decision; the journal entry becomes needs-user. Certainty stays at the default
    /// <see cref="EffectCertainty.Unknown"/> unless the caller overrides it.</summary>
    /// <param name="message">Display-ready text explaining what the user must decide.</param>
    /// <returns>A needs-user outcome.</returns>
    public static CommandOutcome Attention(string message) => new(CommandStatus.NeedsUser, message, null);
    /// <summary>A precondition refusal; certainty is not-started because nothing ran.</summary>
    /// <param name="message">The blockers, joined for display.</param>
    /// <returns>A refused outcome with no problem.</returns>
    public static CommandOutcome Refused(string message) => new(CommandStatus.Refused, message, null) { Certainty = EffectCertainty.NotStarted };
    internal static CommandOutcome CancelledBeforeDispatch() => new(CommandStatus.Cancelled, "The action was cancelled before execution.", null)
    {
        Certainty = EffectCertainty.NotStarted
    };
    /// <summary>A failure carrying its problem; certainty is copied from the problem so callers can tell a
    /// definite refusal from an unknown remote result.</summary>
    /// <param name="problem">The catalogued failure.</param>
    /// <returns>A failed outcome whose message is the problem message.</returns>
    public static CommandOutcome Failed(Problem problem) => new(CommandStatus.Failed, problem.Message, problem) { Certainty = problem.Certainty };
    /// <summary>A rollback with an optional underlying problem. The status is rolled back; certainty is left at
    /// the default unless the caller overrides it.</summary>
    /// <param name="message">Display-ready text describing what was restored.</param>
    /// <param name="problem">The failure that caused the rollback, when one exists.</param>
    /// <returns>A rolled-back outcome.</returns>
    public static CommandOutcome Restored(string message, Problem? problem = null) => new(CommandStatus.RolledBack, message, problem);
}
