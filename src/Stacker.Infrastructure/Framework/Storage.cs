// Purpose: The only file writers in the application: atomic replacement, held locks, and JSON documents that never throw.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>Writes through a unique temporary file and verifies what landed, so no writer can interleave with another.</summary>
public sealed class AtomicFile : IAtomicFile
{
    public async Task<Result<Unit>> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(temporary, bytes.ToArray(), ct);
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

    public Result<byte[]?> Read(string path)
    {
        try { return Result<byte[]?>.Ok(File.Exists(path) ? File.ReadAllBytes(path) : null); }
        catch (Exception ex) { return Result<byte[]?>.Fail(ProblemCodes.StoreRead, $"Cannot read {Path.GetFileName(path)}: {ex.Message}"); }
    }

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

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>An exclusive lock that the operating system releases when the process ends.</summary>
public sealed class HeldFileLock(string path, FileStream stream) : IFileLock
{
    public string Path { get; } = path;
    public ValueTask DisposeAsync() { stream.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Takes locks in application data, so the user's working tree never contains Stacker's bookkeeping.</summary>
public sealed class FileLockFactory(string directory) : IFileLockFactory
{
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

    internal static string Hash(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
}

/// <summary>One JSON document per identity; a damaged document is moved aside instead of thrown at the caller.</summary>
public sealed class JsonStateStore<T>(IAtomicFile files, string directory) : IStateStore<T> where T : class
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
