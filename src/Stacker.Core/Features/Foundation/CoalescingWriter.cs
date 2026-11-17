namespace Stacker.Core;

/// <summary>One in-flight write and one latest pending snapshot. Flush bypasses the debounce delay.</summary>
/// <typeparam name="T">Snapshot value handed to the write delegate. The writer stores the value itself, not a copy, so
/// callers pass a detached snapshot they will not mutate afterwards.</typeparam>
/// <param name="write">Persists one snapshot. The loop serialises calls, so at most one write is in flight.</param>
/// <param name="delay">Pacing interval between two write passes; when omitted the interval is 300 ms.</param>
/// <remarks>This is the autosave buffer behind the review draft editor. A burst of edits collapses into a single
/// pending snapshot: every <see cref="Queue"/> replaces the waiting value, so intermediate states are dropped on
/// purpose and only the newest state is persisted. The interval is a fixed pacing between passes, not a trailing
/// timer restarted by each edit — a snapshot queued while the loop is waiting is written when that wait expires, so
/// a save happens at most once per interval and a queued snapshot waits at most one interval plus the writes already
/// ahead of it.
/// <para>The drain loop runs on the thread pool rather than on the caller's synchronization context, so queueing an
/// autosave from the UI thread never depends on that thread pumping messages. All state transitions happen under one
/// lock, which is why a queue arriving while the loop decides to stop cannot be lost: either the loop still sees the
/// pending value, or the queue sees a stopped loop and starts a new one.</para>
/// <para><see cref="FlushAsync"/> interrupts the interval and awaits the loop, so a caller about to close the window
/// can be sure everything queued before the flush was attempted. A failing write ends the loop, clears the running
/// flag and faults the drain task; the delegate is expected to record its own error, and the next
/// <see cref="Queue"/> starts a fresh loop with only the newest snapshot. The failed snapshot is not retried by the
/// writer.</para></remarks>
public sealed class CoalescingWriter<T>(Func<T, Task> write, TimeSpan? delay = null)
{
    /// <summary>Single gate for every mutable field below, so a queued snapshot and a stopping loop cannot race.</summary>
    private readonly object _sync = new();
    /// <summary>One-shot wake signal for the drain. A count of 0 means "wait for the interval", a count of 1 means "a
    /// flush is already pending"; the maximum count of one turns a repeated flush into a no-op instead of an
    /// over-release.</summary>
    private readonly SemaphoreSlim _flush = new(0, 1);
    /// <summary>The newest snapshot that has not been written yet; each <see cref="Queue"/> replaces it.</summary>
    private T? _pending;
    /// <summary>Separates "nothing queued" from a queued default value, which <see cref="_pending"/> alone cannot express.</summary>
    private bool _hasPending;
    /// <summary>True while a drain loop owns <see cref="_drain"/>; guarantees at most one loop and therefore one write at a time.</summary>
    private bool _running;
    /// <summary>The running drain loop, or an already-completed task when idle. Returned to producers, awaited by
    /// <see cref="FlushAsync"/>, and used to carry a write failure to both.</summary>
    private Task _drain = Task.CompletedTask;
    /// <summary>Snapshots waiting to be written after coalescing: 0 or 1, never the number of queue calls.</summary>
    public int PendingCount
    {
        get
        {
            lock (_sync)
                return _hasPending ? 1 : 0;
        }
    }
    /// <summary>Publishes a snapshot, replacing any snapshot that has not been taken yet, and starts the drain when idle.</summary>
    /// <param name="snapshot">Newest state to persist, handed over as an immutable snapshot.</param>
    /// <returns>The running drain loop, which completes after every snapshot coalesced into that loop was written; a
    /// write failure surfaces here as a faulted task.</returns>
    /// <remarks>A call made while a write is running only stores the value, so the producer never waits for its own
    /// write to start. Callers that must wait for durability await <see cref="FlushAsync"/> instead.</remarks>
    public Task Queue(T snapshot)
    {
        lock (_sync)
        {
            _pending = snapshot;
            _hasPending = true;
            if (!_running)
            {
                _running = true;
                _drain = Task.Run(DrainAsync);
            }
            return _drain;
        }
    }
    /// <summary>Writes the pending snapshot immediately and waits for the drain loop to finish.</summary>
    /// <remarks>Releasing the signal while the loop is busy is still correct: the count stays set and the next wait
    /// returns at once, so a flush never waits out the interval. When nothing is queued and no loop is running this
    /// returns an already-completed task, and a snapshot queued after the flush call is handled by a new loop that the
    /// flush does not wait for.</remarks>
    public async Task FlushAsync()
    {
        Task drain;
        lock (_sync)
        {
            if (_flush.CurrentCount == 0)
                _flush.Release();
            if (!_running && _hasPending)
            {
                _running = true;
                _drain = Task.Run(DrainAsync);
            }
            drain = _drain;
        }
        await drain.ConfigureAwait(false);
    }
    /// <summary>Runs the write loop until no snapshot is pending.</summary>
    /// <remarks>Each pass waits for the interval or for a flush signal, takes the newest snapshot under the lock,
    /// clears the pending slot and writes it outside the lock so producers are never blocked by I/O. The wait result is
    /// ignored because the timeout itself is the trigger. After a write it checks again: a snapshot that arrived during
    /// the write continues the loop after another interval, otherwise the loop clears the running flag and exits. The
    /// catch clears the flag before rethrowing, so the next queue starts a new loop instead of finding a stale
    /// "running" state.</remarks>
    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                await _flush.WaitAsync(delay ?? TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
                T snapshot;
                lock (_sync)
                {
                    if (!_hasPending)
                    {
                        _running = false;
                        return;
                    }
                    snapshot = _pending!;
                    _pending = default;
                    _hasPending = false;
                }
                await write(snapshot).ConfigureAwait(false);
                lock (_sync)
                {
                    if (!_hasPending)
                    {
                        _running = false;
                        return;
                    }
                }
            }
        }
        catch { lock (_sync) _running = false; throw; }
    }
}
