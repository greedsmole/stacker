// Purpose: The only file writers in the application: atomic replacement, held locks, and JSON documents that never throw.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>Writes through a unique temporary file and verifies what landed, so no writer can interleave with another.</summary>
/// <remarks>Every write gets a GUID-based sibling name because a fixed temporary name would let two windows write into
/// the same file and move each other's half-finished bytes into place. The replacement is atomic from a reader's point
/// of view, but it is not a filesystem-wide transaction: a cancellation that arrives after the replace leaves the new
/// bytes behind, and a guarded write that loses a race leaves a <c>.conflict</c> copy for the user to inspect. A
/// temporary is removed in a finally block, so a failed or cancelled write before the move leaves nothing behind.
/// </remarks>
public sealed class AtomicFile : IAtomicFile
{
    /// <inheritdoc/>
    /// <remarks>The expected bytes are checked twice: before writing, which refuses a stale save without touching the
    /// target, and again through the <c>.conflict</c> preimage captured by the atomic replace. The second check is what
    /// turns an external edit that raced the save into evidence instead of a silent overwrite: the submitted bytes stay
    /// at the target and the competing version is preserved in the backup named in the failure message. A
    /// <see langword="null"/> <paramref name="expected"/> means the file must not exist, so the move refuses a target
    /// that appeared in the meantime instead of replacing it. The backup is deleted only after its content matched;
    /// every other path keeps it for inspection. Cancellation is rethrown and the temporary is removed, but a
    /// cancellation after the replace cannot undo the replacement.</remarks>
    public async Task<Result<Unit>> WriteIfUnchangedAsync(string path, ReadOnlyMemory<byte> bytes, byte[]? expected, CancellationToken ct = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = path + "." + Guid.NewGuid().ToString("N") + ".conflict";
        try
        {
            var current = Read(path);
            if (current.IsFailed) return Result<Unit>.Fail(current.Problem!);
            if ((current.Value is null) != (expected is null) || (expected is not null && !expected.AsSpan().SequenceEqual(current.Value)))
                return Result<Unit>.Fail(ProblemCodes.StoreStaleRevision, "The file changed before saving. Reload it first.");
            var written = await WriteAsync(temporary, bytes, ct);
            if (written.IsFailed) return written;
            ct.ThrowIfCancellationRequested();
            if (expected is null) File.Move(temporary, path, overwrite: false);
            else
            {
                // The backup captures the actual preimage at the replacement, including a racing external edit.
                File.Replace(temporary, path, backup);
                var preimage = await File.ReadAllBytesAsync(backup, ct);
                if (!expected.AsSpan().SequenceEqual(preimage))
                    return Result<Unit>.Fail(ProblemCodes.StoreStaleRevision,
                        $"An external edit raced with saving. The submitted version is at {path}; the external version was preserved at {backup}. Review both before saving again.");
                Delete(backup);
            }
            var landed = await File.ReadAllBytesAsync(path, ct);
            return bytes.Span.SequenceEqual(landed) ? Result.Ok
                : Result<Unit>.Fail(ProblemCodes.StoreStaleRevision, "The saved file changed again. Reload before continuing.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<Unit>.Fail(ProblemCodes.StoreWrite, ex.Message); }
        finally { Delete(temporary); }
    }

    /// <inheritdoc/>
    /// <remarks>Writes to a GUID-named sibling, flushes the stream to the operating system and the disk, moves it over the
    /// target, then reads the target back and compares it. The 4096-byte stream buffer is the staging size for one
    /// small document copy; it does not cap the document size, and the code does not record why that size was chosen.
    /// A mismatch after the move means another program changed the target between the move and the verification and is
    /// reported as a write problem; the previous document is already gone at that point. A cancellation is rethrown;
    /// every other failure becomes <see cref="ProblemCodes.StoreWrite"/>, and the temporary is removed in the finally
    /// block.</remarks>
    public async Task<Result<Unit>> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
            var landed = await File.ReadAllBytesAsync(path, ct);
            if (!landed.AsSpan().SequenceEqual(bytes.Span))
                return Result<Unit>.Fail(ProblemCodes.StoreWrite, $"{Path.GetFileName(path)} was changed by another program while saving.");
            return Result.Ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<Unit>.Fail(ProblemCodes.StoreWrite, $"Cannot write {Path.GetFileName(path)}: {ex.Message}"); }
        finally { Delete(temporary); }
    }

    /// <inheritdoc/>
    /// <remarks>Absence is a successful <see langword="null"/>, while an unreadable existing file is
    /// <see cref="ProblemCodes.StoreRead"/>. That distinction matters to a revision check, which must tell "not created
    /// yet" from "created and empty". The existence check happens before the read, so a path that disappears between
    /// the two arrives as a read problem rather than as absence.</remarks>
    public Result<byte[]?> Read(string path)
    {
        try { return Result<byte[]?>.Ok(File.Exists(path) ? File.ReadAllBytes(path) : null); }
        catch (Exception ex) { return Result<byte[]?>.Fail(ProblemCodes.StoreRead, $"Cannot read {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>Removes temporaries a killed process left next to <paramref name="path"/> on a best-effort basis.</summary>
    /// <remarks>Only names matching <c>path + ".*.tmp"</c> are considered, so an unrelated file is never touched. Each
    /// candidate is first probed with an exclusive open: a live writer holds its temporary with the same sharing mode,
    /// so the probe fails and the file is skipped, while a dead writer's file opens and is then deleted. A missing
    /// directory is not an error. This overload returns nothing; the port wrapper turns an unhandled IO or access
    /// failure into <see cref="ProblemCodes.StoreWrite"/>.</remarks>
    /// <param name="path">Target file whose stale sibling temporaries should be removed.</param>
    public void SweepTemporaries(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        foreach (var stale in Directory.EnumerateFiles(directory, name + ".*.tmp"))
        {
            // Only files that are not being written right now: anything with a live handle stays.
            try { using var probe = new FileStream(stale, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { continue; }
            Delete(stale);
        }
    }

    /// <summary>Deletes a temporary or conflict file best-effort, ignoring cleanup failures.</summary>
    /// <remarks>Cleanup must never mask the write outcome: the caller already has a result or is about to report one,
    /// so a missing or locked file is simply left for <see cref="SweepTemporaries"/> on a later run.</remarks>
    /// <param name="path">File to remove.</param>
    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <inheritdoc cref="IAtomicFile.SweepTemporaries"/>
    /// <remarks>Runs the public sweep and converts an unhandled exception to a catalogued
    /// <see cref="ProblemCodes.StoreWrite"/> problem, so a caller using the port never sees an IO exception from
    /// cleanup.</remarks>
    Result<Unit> IAtomicFile.SweepTemporaries(string path) => Boundary.Capture(() => SweepTemporaries(path), ProblemCodes.StoreWrite);
}

/// <summary>An exclusive lock that the operating system releases when the process ends.</summary>
/// <param name="path">Lock file path retained for diagnostics; the file's presence does not prove ownership.</param>
/// <param name="stream">The open handle whose sharing mode is the lock; disposing it releases the lock.</param>
/// <remarks>The handle, not a marker file, is the lock. A crash closes the handle, so the next start can take the
/// lock even though the file remains; a clean disposal leaves that file behind as well. A leftover lock file therefore
/// never blocks a future run, and only a held handle refuses a second writer.</remarks>
public sealed class HeldFileLock(string path, FileStream stream) : IFileLock
{
    /// <inheritdoc/>
    public string Path { get; } = path;
    /// <summary>Disposes the held stream, which releases the operating-system lock.</summary>
    /// <returns>A completed task; disposal itself is synchronous.</returns>
    public ValueTask DisposeAsync() { stream.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Takes locks in application data, so the user's working tree never contains Stacker's bookkeeping.</summary>
/// <param name="directory">Folder that holds one hashed <c>.lock</c> file per identity; normally under application
/// data.</param>
/// <remarks>The open handle is the lock, never the file's existence. The file is created with
/// <c>OpenOrCreate</c> and never deleted, which keeps lock handling free of cleanup races; a crashed process leaves
/// the file but not the handle, so the next start succeeds. The identity is hashed to a fixed-length name, so an
/// arbitrary repository path is safe as one file name and does not appear in the application-data listing.</remarks>
public sealed class FileLockFactory(string directory) : IFileLockFactory
{
    /// <inheritdoc/>
    /// <remarks>Makes one immediate attempt and, on an <c>IOException</c>, retries every 50 ms for at most 40 attempts.
    /// The last attempt does not wait, so the total sleep is 39 × 50 ms = 1.95 s: about two seconds of bounded wait
    /// before a second window is told to retry instead of hanging. Why 40 attempts and 50 ms were chosen is not recorded.
    /// A failure to create the data folder is <see cref="ProblemCodes.StoreWrite"/>; a lock that stays held is
    /// <see cref="ProblemCodes.StoreBusy"/>. Cancellation while waiting propagates. An exception other than a retryable
    /// <c>IOException</c> is treated as busy immediately, and the last attempt's <c>IOException</c> also lands in that
    /// catch-all, so the trailing busy result is effectively unreachable with the current control flow.</remarks>
    public async Task<Result<IFileLock>> TryAcquireAsync(string identity, CancellationToken ct = default)
    {
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) { return Result<IFileLock>.Fail(ProblemCodes.StoreWrite, $"Cannot use the Stacker data folder: {ex.Message}"); }
        var path = System.IO.Path.Combine(directory, Hash(identity) + ".lock");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                // OpenOrCreate + FileShare.None: the handle is the lock, and it disappears with the process.
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return Result<IFileLock>.Ok(new HeldFileLock(path, stream));
            }
            catch (IOException) when (attempt < 39) { await Task.Delay(50, ct); }
            catch (Exception ex) { return Result<IFileLock>.Fail(ProblemCodes.StoreBusy, $"Another Stacker window is using this repository: {ex.Message}"); }
        }
        return Result<IFileLock>.Fail(ProblemCodes.StoreBusy, "Another Stacker window is saving this repository. Retry in a moment.");
    }

    /// <summary>Hashes an identity to the lower-case SHA-256 hex name shared by lock files and JSON documents.</summary>
    /// <remarks>A fixed 64-character name makes every identity safe as one file name and hides the original path from
    /// the directory listing. Lower-casing normalizes the upper-case output of <c>Convert.ToHexString</c>; identities
    /// that differ only by case still hash differently because their UTF-8 bytes differ. The same function is exposed
    /// as <see cref="ApplicationStore.Key"/>, so object and history paths use the same naming.</remarks>
    /// <param name="identity">The resource identity, normally a repository root.</param>
    /// <returns>The 64-character lower-case hexadecimal digest.</returns>
    internal static string Hash(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
}

/// <summary>One JSON document per identity; a damaged document is moved aside instead of thrown at the caller.</summary>
public sealed class JsonStateStore<T>(IAtomicFile files, string directory, bool quarantineCorrupt = true) : IStateStore<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    public string PathFor(string identity) => Path.Combine(directory, FileLockFactory.Hash(identity) + ".json");

    public Result<T?> Read(string identity)
    {
        var path = PathFor(identity);
        var bytes = files.Read(path);
        if (bytes.IsFailed) return Result<T?>.Fail(bytes.Problem!);
        if (bytes.Value is null) return Result<T?>.Ok(null);
        try { return Result<T?>.Ok(JsonSerializer.Deserialize<T>(bytes.Value)); }
        catch (JsonException ex)
        {
            if (!quarantineCorrupt) return Result<T?>.Fail(ProblemCodes.StoreCorrupt,
                $"The recovery journal is damaged. Its original file was preserved at {path}. Resolve it before changing Git. ({ex.Message})");
            var moved = Quarantine(identity);
            var where = moved.IsOk ? $" A copy was kept at {System.IO.Path.GetFileName(moved.Value)}." : "";
            return Result<T?>.Fail(ProblemCodes.StoreCorrupt, $"A Stacker file was damaged and has been reset.{where} ({ex.Message})");
        }
    }

    public async Task<Result<Unit>> WriteAsync(string identity, T value, CancellationToken ct = default)
    {
        try { return await files.WriteAsync(PathFor(identity), JsonSerializer.SerializeToUtf8Bytes(value, Options), ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<Unit>.Fail(ProblemCodes.StoreWrite, ex.Message); }
    }

    public Result<string> Quarantine(string identity)
    {
        var path = PathFor(identity);
        try
        {
            if (!File.Exists(path)) return Result<string>.Ok("");
            var moved = path + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + ".bad";
            File.Move(path, moved, true);
            return Result<string>.Ok(moved);
        }
        catch (Exception ex) { return Result<string>.Fail(ProblemCodes.StoreWrite, ex.Message); }
    }
}

/// <summary>The clock commands and stores share.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

/// <summary>One application instance per user: a second launch is refused rather than sharing app data.</summary>
public sealed class SingleInstanceLock(string directory) : ISingleInstance, IDisposable
{
    private FileStream? _stream;
    public bool TryAcquire()
    {
        if (_stream is not null) return true;
        try
        {
            Directory.CreateDirectory(directory);
            _stream = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public void Dispose() { _stream?.Dispose(); _stream = null; }
}
