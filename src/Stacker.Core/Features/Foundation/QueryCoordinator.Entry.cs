namespace Stacker.Core;

public sealed partial class QueryCoordinator
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
}
