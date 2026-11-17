using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One timestamped page-complete set of open pull requests used for stack discovery.</summary>
/// <param name="Context">The account and repository the pages were read with, so results are never reused across contexts.</param>
/// <param name="PullRequests">Every open pull request across all pages, including drafts.</param>
/// <param name="UpdatedAt">When the read completed: shown as the snapshot's freshness and recorded as the observation time
/// of the history versions that were just observed.</param>
public sealed record GitHubSnapshot(GitHubRepositoryContext Context, IReadOnlyList<PullRequest> PullRequests, DateTimeOffset UpdatedAt);
