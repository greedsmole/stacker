using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>History pins and analysis use the explicitly selected local repository without checkout or index writes.</summary>
/// <remarks>Every method takes the repository it must run in, so no call can silently substitute a private clone or another
/// worktree. Captured commits are pinned as application refs under the history namespace, which keeps them alive for analysis
/// and for the historical views while the user's branches remain untouched. Analysis is textual: external diff helpers,
/// textconv and configured merge drivers are disabled, so a result that would require running user code is reported as
/// unavailable instead of executing it. A read lease is held for the duration of a comparison and defers pin removal.</remarks>
public interface IHistoryGit
{
    /// <summary>Reports whether this scope's pinned commits are present in the opened repository.</summary>
    /// <param name="repository">Local repository that must own the objects.</param>
    /// <param name="scope">History scope whose pins are checked.</param>
    /// <param name="ct">Cancellation for the check.</param>
    /// <returns><see cref="ContentAvailability.Available"/> when pins exist and every commit is present; removed when the user
    /// explicitly removed the history; needs-download when there are no pins and this repository was never recorded as having
    /// the content; unavailable when pins exist but referenced objects are missing, or the content was recorded while the pins
    /// are gone.</returns>
    Task<Result<ContentAvailability>> AvailabilityAsync(RepositoryIdentity repository, string scope, CancellationToken ct = default);
    /// <summary>Verifies that the commits exist locally and pins them for this scope.</summary>
    /// <remarks>Capture never downloads: a commit that is not already in the opened repository fails the call, and the
    /// message tells the user to load the current pull request changes first. Pinning is what allows a later force-push of
    /// the source branch not to destroy the analysed history.</remarks>
    /// <param name="repository">Local repository that owns the objects.</param>
    /// <param name="scope">History scope the pins belong to.</param>
    /// <param name="commits">Commit SHAs to retain, typically the base and head of one layer.</param>
    /// <param name="ct">Cancellation for the operation.</param>
    Task<Result<Unit>> CaptureAsync(RepositoryIdentity repository, string scope, IReadOnlyList<string> commits, CancellationToken ct = default);
    /// <summary>Takes a read lease over the scope's history objects, failing unless the content is available.</summary>
    /// <remarks>The lease's root is the working root to pass to Git reads. It must be disposed — while it is held, removal of
    /// the pins is deferred — and acquiring it fails with a user-facing problem when the content was removed or is missing.</remarks>
    /// <param name="repository">Local repository that owns the objects.</param>
    /// <param name="scope">History scope to lease.</param>
    /// <param name="ct">Cancellation for the acquisition.</param>
    /// <returns>A lease that releases the read when disposed.</returns>
    Task<Result<ObjectSnapshotLease>> AcquireAsync(RepositoryIdentity repository, string scope, CancellationToken ct = default);
    /// <summary>Removes this scope's history pins; Git may collect the commits later.</summary>
    /// <remarks>Versions, review metadata and drafts stay, and the content is recorded as explicitly removed so a later
    /// migration does not silently restore it. Repository directories and objects are never deleted here — disk management
    /// belongs to Git.</remarks>
    /// <param name="repository">Local repository that owns the pins.</param>
    /// <param name="scope">History scope whose pins are removed.</param>
    /// <param name="ct">Cancellation for the operation.</param>
    Task<Result<Unit>> ForgetContentsAsync(RepositoryIdentity repository, string scope, CancellationToken ct = default);
    /// <summary>Replays an old patch onto a new base and returns the resulting tree, or <see langword="null"/> on conflict.</summary>
    /// <remarks>The replay merges the old head as if it had been based on the new base, using the old base as the merge base,
    /// and returns a tree object — no commit, ref or index is written. A textual conflict is reported as
    /// <see langword="null"/> so the caller can mark the comparison unavailable; any other failure is a failed result.</remarks>
    /// <param name="repository">Local repository that owns the objects.</param>
    /// <param name="oldBase">Base the old patch was originally taken from.</param>
    /// <param name="oldHead">Head holding the old patch.</param>
    /// <param name="newBase">New base the patch is replayed onto.</param>
    /// <param name="ct">Cancellation for the analysis.</param>
    /// <returns>The normalized tree, or <see langword="null"/> when the patch does not apply cleanly.</returns>
    Task<Result<string?>> TransplantAsync(RepositoryIdentity repository, string oldBase, string oldHead, string newBase, CancellationToken ct = default);
    /// <summary>Trial-merges two commits and returns the paths that conflict, or an empty list for a clean merge.</summary>
    /// <remarks>A textual preview only: nothing is committed, no ref or index is written, and external merge drivers are
    /// disabled. A conflict set therefore does not predict exactly what a restack will produce, and a merge that would need a
    /// configured driver is reported as a failure instead of running user code.</remarks>
    /// <param name="repository">Local repository that owns the objects.</param>
    /// <param name="parent">Current parent head; the prospective base.</param>
    /// <param name="head">Child head that would be rebased.</param>
    /// <param name="ct">Cancellation for the analysis.</param>
    /// <returns>Conflicting paths, empty when the trial merge is clean.</returns>
    Task<Result<IReadOnlyList<string>>> MergeAsync(RepositoryIdentity repository, string parent, string head, CancellationToken ct = default);
}
