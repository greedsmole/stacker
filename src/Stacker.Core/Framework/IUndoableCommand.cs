using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>A command whose effects can be reversed from its journal entry alone.</summary>
/// <remarks>The runner calls <see cref="UndoAsync"/> after a failure or cancellation, using a fresh 30-second cleanup
/// budget instead of the cancelled token. An implementation must verify that refs still hold the recorded commits
/// before moving them; a rollback that cannot be proven leaves the entry in the needs-user phase rather than
/// guessing. Returning rolled-back or success lets the runner close the entry as rolled back.</remarks>
public interface IUndoableCommand : IStackCommand
{
    /// <summary>Reverses the recorded effects and reports whether restoration was proven.</summary>
    /// <param name="entry">The durable entry that describes what was done.</param>
    /// <param name="ct">Cancellation for the cleanup, bounded by the runner's cleanup budget.</param>
    /// <returns>A rolled-back or successful outcome when restoration is proven; any other outcome makes the runner
    /// leave the entry for the user.</returns>
    Task<CommandOutcome> UndoAsync(JournalEntry entry, CancellationToken ct = default);
}
