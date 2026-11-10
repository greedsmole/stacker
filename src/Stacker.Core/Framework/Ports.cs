using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>Writes a file atomically: unique temporary name, atomic move, verification, no stale leftovers.</summary>
/// <remarks>Readers must never observe a partially written document. The contract is: write the complete bytes to a
/// fresh sibling path, flush them to disk, move that path over the target in one filesystem operation, then read the
/// target back and compare. Temporary and backup names must be unique per write, because a fixed name would let two
/// windows overwrite each other's in-progress data. Cleanup belongs in a finally block so a failed or cancelled write
/// leaves nothing behind. The implementation uses a GUID suffix for its temporary and conflict files.</remarks>
public interface IAtomicFile
{
    /// <summary>Replaces the target with the given bytes atomically and verifies what landed.</summary>
    /// <remarks>The target directory is created when missing. The bytes are flushed to disk before the move, and the
    /// file is read back afterwards; a mismatch fails with <see cref="ProblemCodes.StoreWrite"/> because another
    /// program changed it. A cancellation that arrives after the move cannot undo the replacement, so the target may
    /// already contain the new bytes even though the call reports cancellation.</remarks>
    /// <param name="path">The target file path.</param>
    /// <param name="bytes">The complete document to store.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>Success when the target holds exactly these bytes; otherwise a catalogued problem.</returns>
    Task<Result<Unit>> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
    /// <summary>Writes only when the file still matches the caller's expected content revision.</summary>
    /// <remarks>The default implementation reads the current bytes, compares them with <paramref name="expected"/>
    /// (including the absent-versus-present distinction) and then delegates to <see cref="WriteAsync"/>. A mismatch
    /// returns <see cref="ProblemCodes.StoreStaleRevision"/> instead of overwriting someone else's edit. This narrows
    /// the race but is not a cross-process compare-and-swap: the concrete adapter additionally captures the preimage
    /// at replacement time, verifies it was the expected one, and preserves a racing external edit as a conflict
    /// copy for the user to inspect.</remarks>
    /// <param name="path">The target file path.</param>
    /// <param name="bytes">The complete document to store.</param>
    /// <param name="expected">The bytes the caller last read, or <see langword="null"/> when the file must not exist
    /// yet.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>Success when the replacement happened; otherwise stale-revision or write problems.</returns>
    async Task<Result<Unit>> WriteIfUnchangedAsync(string path, ReadOnlyMemory<byte> bytes, byte[]? expected, CancellationToken ct = default)
    {
        var current = Read(path);
        if (current.IsFailed) return Result<Unit>.Fail(current.Problem!);
        if ((current.Value is null) != (expected is null) || (expected is not null && !expected.AsSpan().SequenceEqual(current.Value)))
            return Result<Unit>.Fail(ProblemCodes.StoreStaleRevision, "The file changed before saving. Reload it first.");
        return await WriteAsync(path, bytes, ct);
    }
    /// <summary>The file's bytes, or null when it does not exist; IO failures come back as problems.</summary>
    /// <remarks>Absence and an empty file are different values, because a revision check must be able to tell "not
    /// created yet" from "created and empty".</remarks>
    /// <param name="path">The file path to read.</param>
    /// <returns>The bytes, <see langword="null"/> when the file is absent, or
    /// <see cref="ProblemCodes.StoreRead"/> on an IO failure.</returns>
    Result<byte[]?> Read(string path);
    /// <summary>Removes temporary files a killed process left behind next to this path.</summary>
    /// <remarks>Only siblings matching this target's temporary pattern are considered. A file that a live writer
    /// still holds open is skipped, so sweeping cannot delete the in-progress write of another window.</remarks>
    /// <param name="path">The target whose stale temporaries should be removed.</param>
    /// <returns>Success even when there was nothing to remove; a catalogued problem when the sweep itself
    /// failed.</returns>
    Result<Unit> SweepTemporaries(string path);
}

/// <summary>A held exclusive lock; the operating system releases it when the process dies.</summary>
/// <remarks>The open handle is the lock, not the file: a crash closes the handle, so no stale marker can block the
/// next start, and the file's continued existence after a clean release means nothing. Dispose the value to release
/// the lock. Locks live in application data rather than the working tree, so a repository never carries Stacker's
/// bookkeeping.</remarks>
public interface IFileLock : IAsyncDisposable
{
    /// <summary>The lock file path, exposed for diagnostics. The path identifies the lock, but its presence does not
    /// prove that the lock is held.</summary>
    string Path { get; }
}

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

/// <summary>Reads and writes one JSON document per identity without ever throwing at the caller.</summary>
/// <remarks>Reads never throw: IO failures are problems, absent documents are <see langword="null"/>, and damaged
/// JSON is reported as <see cref="ProblemCodes.StoreCorrupt"/>. The file-backed store names each document by the
/// hashed identity and writes through <see cref="IAtomicFile"/>; it can also quarantine a damaged document so the user
/// can inspect it. The journal store disables quarantine on purpose, because a damaged recovery record must stay in
/// place and block writes instead of being reset to an empty history.</remarks>
/// <typeparam name="T">The document type. A reference type because absence is represented by
/// <see langword="null"/>.</typeparam>
public interface IStateStore<T> where T : class
{
    /// <summary>Returns the stored value, or null when absent; a corrupt document is quarantined, not thrown.</summary>
    /// <param name="identity">The resource identity used to derive the document path.</param>
    /// <returns>The value, <see langword="null"/> when no document exists, or a read/corrupt problem.</returns>
    Result<T?> Read(string identity);
    /// <summary>Serializes the value and stores it atomically.</summary>
    /// <param name="identity">The resource identity used to derive the document path.</param>
    /// <param name="value">The document to persist.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>Success when the document landed; otherwise <see cref="ProblemCodes.StoreWrite"/>.</returns>
    Task<Result<Unit>> WriteAsync(string identity, T value, CancellationToken ct = default);
    /// <summary>Moves a damaged document aside so the user can inspect it, and reports where it went.</summary>
    /// <remarks>The file-backed store appends a UTC timestamp and a <c>.bad</c> suffix, so repeated damage never
    /// overwrites an earlier preserved copy. An implementation responsible for critical recovery evidence may refuse
    /// instead of moving it, so that the damaged document stays in place and keeps blocking writes.</remarks>
    /// <param name="identity">The resource identity whose document should be moved.</param>
    /// <returns>The new path, an empty string when there was no document, or a problem when the move was refused or
    /// failed.</returns>
    Result<string> Quarantine(string identity);
}

/// <summary>Detects a second instance of the application so app-data writers never interleave.</summary>
/// <remarks>The application assumes a single writer for its profile: two windows sharing the journal and settings
/// would race even with atomic files. The implementation holds an OS handle for the lifetime of the process, so a
/// crash releases it automatically; the second launch is refused rather than allowed to share state.</remarks>
public interface ISingleInstance
{
    /// <summary>True when this process owns the application; false when another instance already runs.</summary>
    /// <remarks>Repeated calls in the owning process keep returning true; the handle is acquired once and released
    /// when the implementation is disposed.</remarks>
    /// <returns>True when the profile is owned by this process.</returns>
    bool TryAcquire();
}
