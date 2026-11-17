using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Directory-based compatibility object store: leases, deferred removal and a byte budget for disposable downloads.</summary>
/// <remarks>Retained from the earlier download cache and deliberately not passed to the active repository readers or
/// cleanup, which now work through the opened Git repository; see <c>docs/ARCHITECTURE.md</c>. It remains available as a
/// utility for application-owned object directories. Only disposable downloads are evicted; retained history is
/// measured but never removed. Every method reports an operating-system failure as a failed result with a catalogued
/// code, and cancellation is the only expected exception.</remarks>
public interface IObjectStore
{
    /// <summary>Replaces the byte budget that trimming targets.</summary>
    /// <param name="bytes">New cap in bytes. The implementation rejects anything below 1 MiB (1,048,576 bytes) with the
    /// <see cref="ProblemCodes.StateInvalid"/> code.</param>
    /// <returns>A successful empty result; the new budget applies at the next trim and nothing is evicted immediately.</returns>
    Result<Unit> SetBudget(long bytes);
    /// <summary>Takes a read lease on one object directory, optionally creating the directory first.</summary>
    /// <param name="root">Object directory inside the application's data root; the store rejects a path outside it.</param>
    /// <param name="create">Creates the directory when it is absent, for a first download.</param>
    /// <param name="ct">Cancels before the lease is taken.</param>
    /// <returns>The held lease; disposing it is what allows cleanup to proceed.</returns>
    /// <remarks>Fails when the directory does not exist and <paramref name="create"/> is false, and when removal is
    /// already pending for it, so a new reader cannot keep a directory alive that is scheduled to go away.
    /// Taking a lease also marks the directory as recently used, which keeps it out of the oldest-first eviction order.
    /// Cancellation surfaces as an <see cref="OperationCanceledException"/> rather than as a failed result.</remarks>
    Task<Result<ObjectSnapshotLease>> AcquireAsync(string root, bool create = false, CancellationToken ct = default);
    /// <summary>Requests removal of one object directory.</summary>
    /// <param name="root">Object directory to remove.</param>
    /// <param name="ct">Cancels before removal is attempted.</param>
    /// <returns><see langword="true"/> when the contents were deleted immediately; <see langword="false"/> when active
    /// leases deferred the removal.</returns>
    /// <remarks>Returning false is not an error: the directory stays readable, the last lease release schedules the
    /// deletion and a later trim finishes it. A directory that is already absent counts as removed.</remarks>
    Task<Result<bool>> RemoveAsync(string root, CancellationToken ct = default);
    /// <summary>Applies pending removals, then evicts the least recently used disposable directories until the budget is met.</summary>
    /// <param name="ct">Cancels between evictions; directories deleted before the cancellation stay deleted.</param>
    /// <returns>The measurement taken after trimming.</returns>
    /// <remarks>The disposable area is walked oldest-first, skipping any directory with active leases, so the budget is a
    /// soft limit while readers hold objects. Retained history objects are measured but never deleted. A failure while
    /// finishing a pending removal is swallowed and retried by a later trim, while a failure during eviction is reported
    /// as a failed result; either way the already deleted directories stay deleted.</remarks>
    Task<Result<ObjectStoreStatus>> TrimAsync(CancellationToken ct = default);
}
