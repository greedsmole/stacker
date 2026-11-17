using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class FoundationConcurrencyTests
{
    private sealed class UnpumpedContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
    }
}
