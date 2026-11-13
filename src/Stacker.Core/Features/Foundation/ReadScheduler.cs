namespace Stacker.Core;

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
    {
        var gate = _slots[(int)work];
        await gate.WaitAsync(ct);
        try
        {
            return await read(ct);
        }
        finally { gate.Release(); }
    }
    /// <summary>Disposes every gate.</summary>
    /// <remarks>Dispose only when no read is in flight: a read that finishes afterwards releases a disposed gate and
    /// throws <see cref="ObjectDisposedException"/> from its finally, masking the read's own result.</remarks>
    public void Dispose()
    {
        foreach (var gate in _slots)
            gate.Dispose();
    }
}
