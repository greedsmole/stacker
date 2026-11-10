using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Whether the historical commits of one scope can be read from the opened repository right now.</summary>
/// <remarks>Stored as its integer value in the history database, and the "explicitly removed" lookup compares against
/// the literal 2, so the order of these members is a persistence contract and must not be rearranged.</remarks>
public enum ContentAvailability
{
    /// <summary>Every pinned commit of the scope is present in the opened repository, so history can be read offline.</summary>
    Available,
    /// <summary>No history pins exist for the scope yet; opening or loading the layer is what pins them.</summary>
    NeedsDownload,
    /// <summary>The user explicitly released the history pins for this scope.</summary>
    /// <remarks>Metadata, checkpoints and drafts remain, and a later import does not restore the scope silently.
    /// Callers surface this as an explanation rather than as an empty history.</remarks>
    Removed,
    /// <summary>Pins or objects that were recorded are no longer available in this repository, so history cannot be read.</summary>
    /// <remarks>Also reported when a scope was previously marked available but its pins have disappeared, which keeps
    /// "nothing downloaded yet" distinguishable from "the objects are gone".</remarks>
    Unavailable
}
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
/// <summary>One bounded diagnostic record describing a completed operation stage.</summary>
/// <param name="OperationId">Correlation id shared by the operation's bus run, journal entry and diagnostics, so one
/// user action can be followed across all three.</param>
/// <param name="Stage">Stage name, for example the command outcome or the Git read that produced it.</param>
/// <param name="ProblemCode">Catalogued problem code when the stage failed; <see langword="null"/> on success.</param>
/// <param name="DurationMs">Wall-clock duration of the stage in milliseconds.</param>
/// <param name="Bytes">Bytes read or produced, when the stage measures them; 0 otherwise.</param>
/// <remarks>The record is deliberately limited to identifiers, stage names, durations, byte counts and problem codes:
/// it carries no file contents, prompts or credentials, so an exported diagnostics log can be shared without leaking
/// the code under review.</remarks>
public sealed record DiagnosticEvent(string OperationId, string Stage, string? ProblemCode, double DurationMs, long Bytes = 0);
/// <summary>Appends bounded operation metadata and exports the retained records.</summary>
/// <remarks>Recording sits on the operation path, so it must be cheap and must never change the operation's outcome:
/// failures come back as problems instead of exceptions.</remarks>
public interface IDiagnosticSink
{
    /// <summary>Appends one record to the retained metadata log.</summary>
    /// <param name="entry">Record to append; it should carry identifiers and measurements only.</param>
    /// <returns>A successful empty result, or a failed result carrying the store's write code.</returns>
    Result<Unit> Record(DiagnosticEvent entry);
    /// <summary>Copies the retained log files into <paramref name="destination"/> so they can be handed to support.</summary>
    /// <param name="destination">Folder that receives the copies; the retained logs themselves are left in place.</param>
    /// <param name="ct">Cancels the copy.</param>
    /// <returns>The destination folder path.</returns>
    Task<Result<string>> ExportAsync(string destination, CancellationToken ct = default);
}
/// <summary>Ambient operation id that ties one user action's bus run, journal entry and diagnostics together.</summary>
/// <remarks>The value lives in an <see cref="AsyncLocal{T}"/>, so it flows into awaited continuations and thread-pool
/// work started inside the scope while two parallel operations keep separate ids. A caller enters a scope with a known
/// id when one is already persisted — a prepared publication payload keeps its id through the send — and lets the
/// first participant mint one when none exists.</remarks>
public static class OperationCorrelation
{
    /// <summary>The ambient value, tracked per async flow; null when no operation scope is active.</summary>
    private static readonly AsyncLocal<string?> Active = new();
    /// <summary>The id of the innermost active operation, or <see langword="null"/> outside one.</summary>
    public static string? Current => Active.Value;
    /// <summary>Makes <paramref name="id"/> the current operation id until the returned scope is disposed.</summary>
    /// <param name="id">Id to use. Passing the id that is already current is harmless and keeps it on exit.</param>
    /// <returns>A scope that restores the previous id, which makes nesting and reuse safe.</returns>
    /// <remarks>Consumers enter it with a <c>using</c> declaration. Because the id is ambient instead of a parameter, a
    /// Git read or journal write deep in the call chain can stamp the same id without changing its signature.</remarks>
    public static IDisposable Enter(string id) { var previous = Active.Value; Active.Value = id; return new Scope(() => Active.Value = previous); }
    /// <summary>Restores the previous ambient id when the operation scope ends.</summary>
    /// <param name="end">Callback that writes the id captured by <see cref="Enter"/> back into the ambient slot.</param>
    private sealed class Scope(Action end) : IDisposable
    {
        /// <summary>Restores the previous id; every exit path from the using scope runs it, including exceptions, so an
        /// id never leaks into the caller's flow.</summary>
        public void Dispose() => end();
    }
}
