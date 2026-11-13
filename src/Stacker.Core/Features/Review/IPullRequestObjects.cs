using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Ensures selected PR objects exist in the opened repository using its configured Git transport.</summary>
public interface IPullRequestObjects
{
    /// <summary>Downloads the commits of the selected pull requests that are missing, then verifies and pins them.</summary>
    /// <remarks>Uses the repository's configured origin, so SSH, credentials, proxy and TLS settings behave as they do for
    /// the user's own Git. A pull request whose head or base moved while downloading fails instead of being compared
    /// against a mix of versions; the caller must refresh GitHub and try again.</remarks>
    /// <param name="repository">Local repository identity whose object database receives the commits.</param>
    /// <param name="context">GitHub context used to validate the origin before downloading.</param>
    /// <param name="prs">Selected pull request snapshots whose base and head commits must exist locally.</param>
    /// <param name="ct">Cancels the download.</param>
    Task<Result<Unit>> EnsureAsync(RepositoryIdentity repository, GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default);
    /// <summary>Acquires a shared read lease so display reads can finish before object references are removed.</summary>
    /// <param name="repository">Local repository identity whose objects are read.</param>
    /// <param name="ct">Cancels the wait and releases the lease request.</param>
    /// <returns>A lease held until it is disposed; clearing object references waits for every outstanding lease.</returns>
    Task<Result<ObjectSnapshotLease>> AcquireAsync(RepositoryIdentity repository, CancellationToken ct = default);
    /// <summary>Deletes only the application-owned refs that keep the selected pull request objects reachable.</summary>
    /// <remarks>Removal waits for outstanding read leases and never deletes the repository directory, object files, user
    /// branches, remote-tracking branches or recovery refs, and never forces a garbage collection; an interrupted clear
    /// is retried by running it again.</remarks>
    /// <param name="repository">Local repository identity whose refs are cleared.</param>
    /// <param name="context">GitHub context that scopes which application refs belong to this download.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<Result<Unit>> ClearAsync(RepositoryIdentity repository, GitHubRepositoryContext context, CancellationToken ct = default);
    /// <summary>Reports the shared Git directory and the object bytes Git currently accounts for.</summary>
    /// <param name="repository">Local repository identity whose storage is inspected.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The shared Git directory and the object bytes Git accounts for.</returns>
    Task<Result<RepositoryObjectStorage>> InspectAsync(RepositoryIdentity repository, CancellationToken ct = default);
}
