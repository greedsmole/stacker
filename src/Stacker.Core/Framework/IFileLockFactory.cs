using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>Creates exclusive locks outside the working tree, keyed by identity.</summary>
/// <remarks>The identity is hashed to a stable file name so an arbitrary scope string becomes a safe name. The
/// concrete adapter retries 40 times, 50 ms apart (about two seconds), and then reports the lock as busy instead of
/// waiting indefinitely. The lock file itself may remain on disk after release; only a held handle proves ownership,
/// so a leftover file never blocks the next start.</remarks>
public interface IFileLockFactory
{
    /// <summary>Takes the lock or fails with <see cref="ProblemCodes.StoreBusy"/>; a released lock leaves no marker
    /// that would block the next start.</summary>
    /// <param name="identity">The value that identifies the resource being protected; it is hashed into the lock
    /// file name.</param>
    /// <param name="ct">Cancellation while waiting for a held lock.</param>
    /// <returns>A held lock that must be disposed, a busy problem when another writer holds it, or a storage problem
    /// when the lock directory cannot be used.</returns>
    Task<Result<IFileLock>> TryAcquireAsync(string identity, CancellationToken ct = default);
}
