namespace Stacker.Core;

/// <summary>Shares immutable reads. A consumer only cancels its subscription; the last consumer cancels the work.</summary>
/// <remarks>Callers with the same result type and key await one underlying read instead of starting their own. Two
/// properties make that safe for patch reads: cancellation is per subscriber, so closing one view never aborts a read
/// another view still displays; and the entry disappears when its last subscriber leaves, so this is single-flight
/// sharing rather than a result cache — the next request reads again. The delegate runs on the thread pool and receives
/// the entry's token, never a caller's token, because no single caller owns the work.
/// <para>If the read throws, every current subscriber observes the same exception and the last one to leave disposes the
/// cancellation source. No lock is held while the delegate runs, so a slow read never blocks other keys.</para></remarks>
public sealed class QueryCoordinator : IDisposable
{
    /// <summary>One shared read: its cancellation source, its task and how many subscribers still await it.</summary>
    private sealed class Entry
    {
        /// <summary>Cancels the shared read when the last subscriber leaves; it is not linked to any caller's token.</summary>
        public readonly CancellationTokenSource Cancellation = new();
        /// <summary>The shared read, boxed so one entry can serve any result type of the same identity. It is assigned
        /// while the entry is still private to the lock, which is why the null-forgiving initialiser is safe.</summary>
        public Task<object?> Task = null!;
        /// <summary>Subscribers that have entered and not yet left; the entry is removed when this reaches zero.</summary>
        public int Readers;
    }
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
        Entry entry; var identity = (typeof(T), key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(identity, out entry!))
            {
                entry = new(); var created = entry;
                entry.Task = Task.Run<object?>(async () => await read(created.Cancellation.Token), CancellationToken.None);
                _entries[identity] = entry;
            }
            entry.Readers++;
        }
        try { return (T)(await entry.Task.WaitAsync(ct))!; }
        finally
        {
            lock (_sync)
            {
                if (--entry.Readers == 0)
                {
                    _entries.Remove(identity);
                    if (!entry.Task.IsCompleted) entry.Cancellation.Cancel();
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
        lock (_sync) { if (_disposed) return; _disposed = true; foreach (var entry in _entries.Values) entry.Cancellation.Cancel(); _entries.Clear(); }
    }
}
/// <summary>Work classes that each have their own read concurrency gate, in the order of the scheduler's slot array.</summary>
/// <remarks>The numeric value is the index into <see cref="ReadScheduler"/>'s gate array, so reordering or inserting a
/// member silently moves every later class to a different limit. No code in <c>src</c> currently selects the syntax or
/// refresh slot; they are reserved for the highlighting and background-refresh paths that the architecture record
/// assigns one slot each.</remarks>
public enum ReadWork
{
    /// <summary>GitHub metadata and API reads; at most 4 run at once.</summary>
    Metadata,
    /// <summary>Patch reads from the repository; at most 2 run at once.</summary>
    Diff,
    /// <summary>Syntax highlighting work; a single read at a time.</summary>
    Syntax,
    /// <summary>Background refresh work; a single read at a time.</summary>
    Refresh
}
/// <summary>Bounds concurrent reads per work class with one gate per class, so a burst in one class cannot starve another.</summary>
/// <remarks>The limits are 4 for metadata, 2 for diff and 1 each for syntax and refresh, so at most eight reads run at
/// once across all classes and the rest queue. The limits are recorded in the architecture note and the diff limit is
/// asserted by the concurrency tests; why those exact numbers were chosen is not recorded. Waiting honours the caller's
/// token, and the same token is passed to the delegate, so a read that observes it is aborted as well.
/// Gates are plain semaphores: no fairness beyond their queueing order and no timeout, so a caller waits until a slot
/// frees or its token fires.</remarks>
public sealed class ReadScheduler : IDisposable
{
    /// <summary>One gate per <see cref="ReadWork"/> value, in enum order; each starts and caps at its class limit.</summary>
    private readonly SemaphoreSlim[] _slots = [new(4, 4), new(2, 2), new(1, 1), new(1, 1)];
    /// <summary>Runs <paramref name="read"/> under the gate for its work class.</summary>
    /// <typeparam name="T">Value produced by the read.</typeparam>
    /// <param name="work">Work class that selects the gate and therefore the concurrency limit.</param>
    /// <param name="read">The read to run; it receives <paramref name="ct"/> and is canceled by it.</param>
    /// <param name="ct">Cancels while waiting for a slot and during the read itself.</param>
    /// <returns>The value produced by <paramref name="read"/>.</returns>
    /// <remarks>The slot is released in a finally, so a throwing read does not leak it. An out-of-range work value
    /// indexes past the array and throws <see cref="IndexOutOfRangeException"/>; only the defined members are valid.
    /// There is no timeout: a queued caller waits until a slot frees or its token fires.</remarks>
    public async Task<T> RunAsync<T>(ReadWork work, Func<CancellationToken, Task<T>> read, CancellationToken ct = default)
    { var gate = _slots[(int)work]; await gate.WaitAsync(ct); try { return await read(ct); } finally { gate.Release(); } }
    /// <summary>Disposes every gate.</summary>
    /// <remarks>Dispose only when no read is in flight: a read that finishes afterwards releases a disposed gate and
    /// throws <see cref="ObjectDisposedException"/> from its finally, masking the read's own result.</remarks>
    public void Dispose() { foreach (var gate in _slots) gate.Dispose(); }
}
