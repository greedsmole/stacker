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
        if (current.IsFailed)
            return Result<Unit>.Fail(current.Problem!);
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
