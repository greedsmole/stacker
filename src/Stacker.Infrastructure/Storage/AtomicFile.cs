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
            if (current.IsFailed)
                return Result<Unit>.Fail(current.Problem!);
            if ((current.Value is null) != (expected is null) || (expected is not null && !expected.AsSpan().SequenceEqual(current.Value)))
                return Result<Unit>.Fail(ProblemCodes.StoreStaleRevision, "The file changed before saving. Reload it first.");
            var written = await WriteAsync(temporary, bytes, ct);
            if (written.IsFailed)
                return written;
            ct.ThrowIfCancellationRequested();
            if (expected is null)
                File.Move(temporary, path, overwrite: false);
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
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
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
        try
        {
            return Result<byte[]?>.Ok(File.Exists(path) ? File.ReadAllBytes(path) : null);
        }
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
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;
        foreach (var stale in Directory.EnumerateFiles(directory, name + ".*.tmp"))
        {
            // Only files that are not being written right now: anything with a live handle stays.
            try
            {
                using var probe = new FileStream(stale, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
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
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <inheritdoc cref="IAtomicFile.SweepTemporaries"/>
    /// <remarks>Runs the public sweep and converts an unhandled exception to a catalogued
    /// <see cref="ProblemCodes.StoreWrite"/> problem, so a caller using the port never sees an IO exception from
    /// cleanup.</remarks>
    Result<Unit> IAtomicFile.SweepTemporaries(string path) => Boundary.Capture(() => SweepTemporaries(path), ProblemCodes.StoreWrite);
}
