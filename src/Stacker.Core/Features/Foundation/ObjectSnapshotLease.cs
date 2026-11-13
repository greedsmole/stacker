using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Read lease on one object directory; the store will not delete the directory while the lease is held.</summary>
/// <param name="root">Absolute directory the holder reads from, already validated by the store.</param>
/// <param name="release">Callback that returns the lease to the store exactly once, allowing a deferred removal.</param>
/// <remarks>A lease is a lifetime guard, not an open handle: it states that a reader is still using those files.
/// Removal requested while leases are held is deferred, the last release schedules the deletion and a later trim
/// finishes it. Disposing twice is harmless because the first dispose swaps the callback to null.</remarks>
public sealed class ObjectSnapshotLease(string root, Action release) : IDisposable
{
    /// <summary>The release callback, cleared on first dispose so an atomic swap makes disposal idempotent.</summary>
    private Action? _release = release;
    /// <summary>Absolute object directory this lease keeps alive.</summary>
    public string Root { get; } = root;
    /// <summary>Returns the lease and lets a deferred removal proceed; safe to call more than once.</summary>
    /// <remarks>The callback runs synchronously on the disposing thread and only decrements the store's counter, so a
    /// caller may dispose on the UI thread; any deletion is scheduled by the store, not performed here.</remarks>
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
