using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>How a command ended; <see cref="JournalPhase.Running"/> means it never closed and needs recovery.</summary>
/// <remarks><see cref="JournalPhase.Running"/> is written by <see cref="IJournal.OpenAsync"/> and every other phase
/// by <see cref="IJournal.CloseAsync"/>. The command runner closes normal outcomes, recovery closes after restoring
/// or dismissing, and the remote journal maps a confirmed publication to <see cref="JournalPhase.Applied"/>. Only
/// <see cref="JournalPhase.Running"/> and <see cref="JournalPhase.NeedsUser"/> require the user's attention;
/// <see cref="JournalPhase.Running"/>, <see cref="JournalPhase.Applied"/> and
/// <see cref="JournalPhase.NeedsUser"/> are the phases that still offer an undo. The numeric values are part of the
/// stored format, like <see cref="StepState"/>, so the order is fixed.</remarks>
public enum JournalPhase
{
    /// <summary>Opened by <see cref="IJournal.OpenAsync"/> and not yet closed; an effect may be in flight. A later
    /// start that reads this phase must treat the command as interrupted.</summary>
    Running,
    /// <summary>Every planned effect completed and the command closed normally; a confirmed remote publication is
    /// mapped here as well. The entry can still be undone.</summary>
    Applied,
    /// <summary>The command stopped for a decision it cannot make itself: a conflict, a parent that moved outside
    /// the operation, or an effect whose outcome is unknown. Only an explicit resume, undo or dismiss changes it.</summary>
    NeedsUser,
    /// <summary>The recorded effects were reversed, either by the command's compensation in the runner or by
    /// recovery restoring every branch after a failed restack. Nothing further is offered.</summary>
    RolledBack,
    /// <summary>The user explicitly undid an earlier applied restack, restoring the commits recorded before it.
    /// Kept distinct from <see cref="JournalPhase.RolledBack"/>, which is automatic recovery rather than a deliberate
    /// undo.</summary>
    Undone,
    /// <summary>The record was closed without reversing anything: a command that failed before moving a ref, an
    /// explicit dismiss of effects that cannot be proven, or a publication GitHub did not confirm.</summary>
    Dismissed
}
