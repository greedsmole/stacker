using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Creates and retargets the pull requests a stack layer needs; every call is explicit and revalidated by the caller.</summary>
/// <remarks>
/// Branches are sent by name because that is what GitHub knows: a remote-tracking parent is normalized before the call,
/// never passed as <c>origin/name</c>. The caller re-derives the plan from a live snapshot, so these calls are not
/// idempotent by themselves and are never retried automatically.
/// </remarks>
public interface IGitHubPullRequestWriter
{
    /// <summary>Opens a pull request for one layer, with the layer's parent as its base branch.</summary>
    /// <param name="context">Pinned host, repository and account to create the pull request with.</param>
    /// <param name="headBranch">Branch name GitHub knows for the layer head, without a refs prefix.</param>
    /// <param name="baseBranch">Branch name GitHub knows for the layer parent, without a refs prefix or remote name.</param>
    /// <param name="title">Pull request title; the caller derives it from the layer.</param>
    /// <param name="body">Initial description; empty when the caller has none.</param>
    /// <param name="draft">Whether GitHub should mark the new pull request as a draft.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The created pull request as GitHub reports it, with its assigned number.</returns>
    Task<Result<PullRequest>> CreatePullRequestAsync(GitHubRepositoryContext context, string headBranch, string baseBranch,
        string title, string body, bool draft, CancellationToken ct = default);
    /// <summary>Points an existing pull request at a different base branch after a layer's parent changed.</summary>
    /// <param name="context">Pinned host, repository and account to write with.</param>
    /// <param name="number">Pull request number to retarget.</param>
    /// <param name="baseBranch">New base branch name GitHub knows, without a refs prefix.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The updated pull request as GitHub reports it.</returns>
    Task<Result<PullRequest>> UpdatePullRequestBaseAsync(GitHubRepositoryContext context, long number, string baseBranch, CancellationToken ct = default);
}
