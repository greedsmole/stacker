using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>What the user chose for a journal entry that still needs attention.</summary>
/// <remarks>The three answers a recovery prompt can offer: continue the plan, reverse what is recorded, or accept
/// the effects and close the record. The enum names the choices; each caller maps them to recovery operations.</remarks>
public enum RecoveryChoice
{
    /// <summary>Continue the remaining planned effects from the durable entry.</summary>
    Resume,
    /// <summary>Reverse the recorded effects and close the entry, either as rolled back or as undone.</summary>
    Undo,
    /// <summary>Close the entry without changing any ref; the effects stay as they are.</summary>
    Dismiss
}
