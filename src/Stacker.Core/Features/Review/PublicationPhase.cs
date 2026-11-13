using System.Text.Json;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Lifecycle of one publication, from the persisted payload to the final recorded outcome.</summary>
/// <remarks>
/// The numeric value is part of the stored format: the draft store treats <c>Prepared</c>, <c>Sending</c> and
/// <c>Uncertain</c> as unfinished, keeps <c>Confirmed</c> sticky, and closes the matching remote journal entry from the
/// terminal value. Reordering or renumbering these members would change the meaning of existing evidence, including
/// operations that were interrupted before an upgrade.
/// </remarks>
public enum PublicationPhase
{
    /// <summary>Payload and operation are persisted but nothing was dispatched; a restart safely rejects it and allows a retry.</summary>
    Prepared,
    /// <summary>The command reached the bus and the process is waiting for GitHub; an exception or restart from this phase is uncertain, not failed.</summary>
    Sending,
    /// <summary>GitHub accepted the mutation. Terminal, and never downgraded by a later save.</summary>
    Confirmed,
    /// <summary>The outcome is unknown because the response was lost or the app stopped mid-send; sending stays blocked and nothing is retried automatically.</summary>
    Uncertain,
    /// <summary>The mutation certainly did not happen, or the user resolved uncertainty after checking GitHub. Terminal, and the unsent text stays available.</summary>
    Rejected
}
