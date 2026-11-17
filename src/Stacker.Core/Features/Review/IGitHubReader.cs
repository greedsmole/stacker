using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Read-only GitHub operations; all methods must use the context's explicit host and repository.</summary>
/// <remarks>
/// Failures are returned as <see cref="Result{T}"/> problems rather than thrown across the port, so the review view can
/// report a refusal such as a closed pull request or a changed account. Nothing here writes to GitHub.
/// </remarks>
public interface IGitHubReader
{
    /// <summary>Resolves the origin (or the explicit override), validates the active account, and verifies repository access.</summary>
    /// <remarks>A missing origin, an unparseable remote, a mismatched gh account or a repository id mismatch each fail with a
    /// message that tells the user what to fix; local stacks stay available when no origin exists.</remarks>
    /// <param name="root">Working tree whose origin remote is consulted when no override is supplied.</param>
    /// <param name="repositoryOverride">Explicit host/owner/repo, or null to resolve the origin remote.</param>
    /// <param name="ct">Cancels the identity and access reads.</param>
    /// <returns>The resolved context, or a failed result describing what the user must correct.</returns>
    Task<Result<GitHubRepositoryContext>> ConnectAsync(string root, string? repositoryOverride, CancellationToken ct = default);
    /// <summary>Reads every open pull request across all pages, retaining drafts because they can still form a stack.</summary>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="ct">Cancels the paged read.</param>
    /// <returns>The page-complete open set; a repository without pull requests is an empty list, not a failure.</returns>
    Task<Result<GitHubSnapshot>> SnapshotAsync(GitHubRepositoryContext context, CancellationToken ct = default);
    /// <summary>Reads one current pull request and refuses it when it is no longer open.</summary>
    /// <remarks>Used by the stale-snapshot check, so a merged or closed pull request cannot receive a review built for an
    /// older state.</remarks>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="number">Pull request number inside that repository.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The current pull request; a closed or merged one comes back as a failed result, which is how the stale check
    /// notices that publishing must stop.</returns>
    Task<Result<PullRequest>> PullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default);
    /// <summary>Loads conversation comments, review decisions, and code threads as separate collections.</summary>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="pr">Snapshot whose number is queried and whose base/head SHAs fill anchors for threads GitHub reports
    /// without a commit.</param>
    /// <param name="ct">Cancels the paged reads.</param>
    Task<Result<ReviewDiscussion>> DiscussionAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default);
    /// <summary>Verifies that every anchor still matches the live pull request and exists on its side of the server diff.</summary>
    /// <remarks>This is the last guard before a comment or review is posted: it rejects a moved head/base and a range whose
    /// lines GitHub no longer reports, which would otherwise attach the text to a different line or fail the request
    /// after the mutation was already dispatched.</remarks>
    /// <param name="context">Pinned host, repository and account to verify against.</param>
    /// <param name="pr">The live pull request every anchor must still match.</param>
    /// <param name="anchors">Anchors to verify; an empty list is accepted and checks nothing, which lets a resolve or
    /// conversation comment reuse the same validation call.</param>
    /// <param name="ct">Cancels the diff read.</param>
    /// <returns><see cref="Unit"/> when every anchor is publishable; otherwise the first anchor that is not.</returns>
    Task<Result<Unit>> ValidateAnchorsAsync(GitHubRepositoryContext context, PullRequest pr, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct = default);
    /// <summary>Lists the paths of the files GitHub currently shows in the pull request diff.</summary>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="pr">Pull request whose current diff is listed.</param>
    /// <param name="ct">Cancels the paged read.</param>
    /// <returns>Distinct repository-relative paths; a file comment is only offered for one of them.</returns>
    Task<Result<IReadOnlyList<string>>> ChangedFilePathsAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default);
    /// <summary>Requires a path to be part of the current server-side diff before a file comment is created.</summary>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="pr">Pull request whose current diff must contain the path.</param>
    /// <param name="path">Repository-relative file path to check.</param>
    /// <param name="ct">Cancels the diff read.</param>
    Task<Result<Unit>> ValidateFileAsync(GitHubRepositoryContext context, PullRequest pr, string path, CancellationToken ct = default);
    /// <summary>Revalidates the active account and the numeric repository id immediately before a sensitive action.</summary>
    /// <param name="context">The context whose account and repository id are rechecked.</param>
    /// <param name="ct">Cancels the two verification reads.</param>
    Task<Result<Unit>> VerifyAccessAsync(GitHubRepositoryContext context, CancellationToken ct = default);
}
