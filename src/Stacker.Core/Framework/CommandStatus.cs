using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>The single final status of a command, chosen so the UI never has to reconcile two contradictory
/// flags.</summary>
/// <remarks><c>CommandRunner</c> maps a successful outcome to <see cref="CommandStatus.Applied"/>, a needs-user
/// outcome to <see cref="CommandStatus.NeedsUser"/>, a rolled-back one to
/// <see cref="CommandStatus.RolledBack"/>, and any other non-success to
/// <see cref="JournalPhase.Dismissed"/> in the journal. <see cref="CommandStatus.Refused"/> is returned before an
/// entry exists; <see cref="CommandStatus.Cancelled"/> only after cancellation;
/// <see cref="CommandStatus.PartiallyApplied"/> when some effects landed and could not be reversed.</remarks>
public enum CommandStatus
{
    /// <summary>Every effect the command intended completed.</summary>
    Applied,
    /// <summary>A precondition blocked the command; nothing ran and no journal entry was opened.</summary>
    Refused,
    /// <summary>An effect failed. The runner closes the entry as dismissed when the certainty proves that nothing
    /// happened; otherwise it reports the partial or needs-user result instead of a plain failure.</summary>
    Failed,
    /// <summary>The caller or shutdown cancelled the command; the runner attempted compensation with a short
    /// cleanup budget and reports whatever it could restore.</summary>
    Cancelled,
    /// <summary>Execution cannot proceed, or cannot prove the outcome, without a user decision. The journal entry
    /// stays in the needs-user phase.</summary>
    NeedsUser,
    /// <summary>Some effects landed and were not reversed, for example a multi-branch push that stopped midway; the
    /// message lists them.</summary>
    PartiallyApplied,
    /// <summary>The recorded effects were reversed after failure or cancellation.</summary>
    RolledBack
}
