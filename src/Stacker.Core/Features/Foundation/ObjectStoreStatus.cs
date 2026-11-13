using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One measurement of the object store: disposable bytes, retained history bytes, the budget and live leases.</summary>
/// <param name="DisposableBytes">Total bytes held by the disposable download area at the end of the trim.</param>
/// <param name="HistoryBytes">Total bytes held by the retained history object area.</param>
/// <param name="DisposableBudget">The configured cap, in bytes, that trimming aims to bring
/// <paramref name="DisposableBytes"/> down to. The implementation starts at 2 GiB (2,147,483,648 bytes) and refuses a
/// cap below 1 MiB.</param>
/// <param name="ActiveLeases">Leases currently held through the store, counted across all object roots the store knows
/// about.</param>
/// <remarks>Bytes are measured by walking the directories during a trim, so they describe that moment rather than a
/// live counter. Disposable bytes may exceed the budget: leases take precedence and only the least recently used
/// unleased directories are evicted. History bytes are never evicted automatically, which is why they are reported
/// separately and drive <see cref="HistoryWarning"/>.</remarks>
public sealed record ObjectStoreStatus(long DisposableBytes, long HistoryBytes, long DisposableBudget, int ActiveLeases)
{
    /// <summary>True when retained history has reached 5 GiB (5,368,709,120 bytes).</summary>
    /// <remarks>The comparison is inclusive, so exactly 5 GiB already warns. It is a reporting hint, not an eviction
    /// trigger: nothing is deleted when the flag turns true, and no code in <c>src</c> currently reads it. Why 5 GiB was
    /// chosen as the threshold is not recorded.</remarks>
    public bool HistoryWarning => HistoryBytes >= 5L * 1024 * 1024 * 1024;
}
