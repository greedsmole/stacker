namespace Stacker.Core;

/// <summary>Shares immutable reads. A consumer only cancels its subscription; the last consumer cancels the work.</summary>
/// <remarks>Callers with the same result type and key await one underlying read instead of starting their own. Two
/// properties make that safe for patch reads: cancellation is per subscriber, so closing one view never aborts a read
/// another view still displays; and the entry disappears when its last subscriber leaves, so this is single-flight
/// sharing rather than a result cache — the next request reads again. The delegate runs on the thread pool and receives
/// the entry's token, never a caller's token, because no single caller owns the work.
/// <para>If the read throws, every current subscriber observes the same exception and the last one to leave disposes the
/// cancellation source. No lock is held while the delegate runs, so a slow read never blocks other keys.</para></remarks>
public sealed partial class QueryCoordinator : IDisposable
{
    /// <summary>Single gate for the dictionary, the entries and the disposed flag, so joining and starting one read is atomic.</summary>
    private readonly object _sync = new();
    /// <summary>In-flight reads only, keyed by result type and caller key; the dictionary is empty between bursts.</summary>
    private readonly Dictionary<(Type, string), Entry> _entries = [];
    /// <summary>Set by <see cref="Dispose"/>; a later run is rejected instead of starting work on a dead coordinator.</summary>
    private bool _disposed;
    /// <summary>Runs or joins the shared read for <paramref name="key"/> and returns its result.</summary>
    /// <typeparam name="T">Result type. Together with the key it forms the sharing identity, so the same key with a
    /// different result type is a different read and cannot be cast to the wrong type.</typeparam>
    /// <param name="key">Opaque key, such as a content-addressed patch hash; equal keys share one read.</param>
    /// <param name="read">Produces the value. It may be canceled by the entry token when the last subscriber leaves.</param>
    /// <param name="ct">Cancels this subscriber only; the shared read continues for the others.</param>
    /// <returns>The value produced by the shared read.</returns>
    /// <remarks>Order: an already-cancelled caller is rejected before touching any state, so it neither creates nor
    /// joins work; the entry is created and its task started inside the lock, so two callers cannot race into two reads;
    /// the subscriber count is incremented before the lock is released; and the task is awaited with
    /// <see cref="Task.WaitAsync(CancellationToken)"/>, which is what lets this caller's token detach it. The finally
    /// block decrements the count, and at zero it removes the entry, cancels work that is still running and schedules a
    /// continuation that observes the task's exception and disposes the source. A failing read therefore reaches every
    /// subscriber, and the last one to leave is the one that observes it.</remarks>
    public async Task<T> RunAsync<T>(string key, Func<CancellationToken, Task<T>> read, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Entry entry;
        var identity = (typeof(T), key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(identity, out entry!))
            {
                entry = new();
                var created = entry;
                entry.Task = Task.Run<object?>(async () => await read(created.Cancellation.Token), CancellationToken.None);
                _entries[identity] = entry;
            }
            entry.Readers++;
        }
        try
        {
            return (T)(await entry.Task.WaitAsync(ct))!;
        }
        finally
        {
            lock (_sync)
            {
                if (--entry.Readers == 0)
                {
                    _entries.Remove(identity);
                    if (!entry.Task.IsCompleted)
                        entry.Cancellation.Cancel();
                    _ = entry.Task.ContinueWith(t => { _ = t.Exception; entry.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
            }
        }
    }
    /// <summary>Cancels every in-flight read and refuses new work.</summary>
    /// <remarks>Idempotent. It does not wait for the reads to finish: their subscribers observe cancellation, and each
    /// entry's continuation disposes the cancellation source when that read ends. A later run throws
    /// <see cref="ObjectDisposedException"/>. A read that ignores cancellation keeps running, and its source stays alive,
    /// because there is no forced teardown.</remarks>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var entry in _entries.Values)
                entry.Cancellation.Cancel();
            _entries.Clear();
        }
    }
}
