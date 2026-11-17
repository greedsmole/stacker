using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Server snapshot of a pull request's base/head refs and immutable commit IDs.</summary>
/// <remarks>
/// The two SHAs are the version identity of everything review-related: anchors, drafts and review submissions carry
/// them, and a publication is refused when the live pull request no longer matches. The repository ids keep a pull
/// request from a fork from being matched to a local layer that merely shares a branch name.
/// </remarks>
/// <param name="Number">The pull request number inside its repository, unique for the life of the repository.</param>
/// <param name="Title">Current server title, used as a layer and stack label.</param>
/// <param name="BaseRepositoryId">Numeric id of the repository the base branch lives in, which may differ for a fork.</param>
/// <param name="BaseRef">Base branch name as GitHub reports it, without a refs prefix or remote name.</param>
/// <param name="BaseSha">Commit id of the base branch at snapshot time.</param>
/// <param name="HeadRepositoryId">Numeric id of the repository the head branch lives in, matched against the opened repository.</param>
/// <param name="HeadRef">Head branch name as GitHub reports it.</param>
/// <param name="HeadSha">Commit id of the pull request head at snapshot time, the version a draft is anchored to.</param>
/// <param name="IsDraft">Whether GitHub marks the pull request as a draft; drafts are still included in snapshots because
/// they can form a stack.</param>
/// <param name="Url">Server page URL, shown in the review and discussion UI.</param>
public sealed record PullRequest(long Number, string Title, long BaseRepositoryId, string BaseRef, string BaseSha,
    long HeadRepositoryId, string HeadRef, string HeadSha, bool IsDraft, string Url)
{
    /// <summary>Lifecycle state of the pull request, defaulting to <c>Open</c> for freshly parsed snapshots.</summary>
    /// <remarks>A snapshot only returns open pull requests, so parsed snapshot entries are <c>Open</c>; the history graph sets
    /// <c>Closed</c> and <c>Merged</c> when it reads a historical pull request or retains a node that already had a terminal
    /// state, which keeps merged nodes readable after they leave the open list.</remarks>
    public string State { get; init; } = "Open";
    /// <summary>Short label combining the number and the current title.</summary>
    public string Label => $"#{Number} · {Title}";
}
