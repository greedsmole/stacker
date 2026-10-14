namespace Stacker.Core.Framework;

/// <summary>Writes a file atomically: unique temporary name, atomic move, verification, no stale leftovers.</summary>
public interface IAtomicFile
{
    Task<Result<Unit>> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
    /// <summary>The file's bytes, or null when it does not exist; IO failures come back as problems.</summary>
    Result<byte[]?> Read(string path);
    /// <summary>Removes temporary files a killed process left behind next to this path.</summary>
    void SweepTemporaries(string path);
}

/// <summary>A held exclusive lock; the operating system releases it when the process dies.</summary>
public interface IFileLock : IAsyncDisposable
{
    string Path { get; }
}

/// <summary>Creates exclusive locks outside the working tree, keyed by identity.</summary>
public interface IFileLockFactory
{
    /// <summary>Takes the lock or fails with <see cref="ProblemCodes.StoreBusy"/>; never leaves a marker file behind.</summary>
    Task<Result<IFileLock>> TryAcquireAsync(string identity, CancellationToken ct = default);
}

/// <summary>Reads and writes one JSON document per identity without ever throwing at the caller.</summary>
public interface IStateStore<T> where T : class
{
    /// <summary>Returns the stored value, or null when absent; a corrupt document is quarantined, not thrown.</summary>
    Result<T?> Read(string identity);
    Task<Result<Unit>> WriteAsync(string identity, T value, CancellationToken ct = default);
    /// <summary>Moves a damaged document aside so the user can inspect it, and reports where it went.</summary>
    Result<string> Quarantine(string identity);
}

/// <summary>Detects a second instance of the application so app-data writers never interleave.</summary>
public interface ISingleInstance
{
    /// <summary>True when this process owns the application; false when another instance already runs.</summary>
    bool TryAcquire();
}
