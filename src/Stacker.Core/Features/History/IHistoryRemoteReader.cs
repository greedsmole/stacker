using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Reads pull request metadata that the current open snapshot no longer contains.</summary>
/// <remarks>Used to settle the fate of a retained graph node: a change missing from the open page set is usually merged or
/// closed, and that cannot be inferred from absence. One request is made per node, and only while the recorded state is not
/// already final.</remarks>
public interface IHistoryRemoteReader
{
    /// <summary>Reads one pull request by number, including its state, for a node retained from earlier revisions.</summary>
    /// <param name="context">GitHub identity — host, repository and signed-in account — the number has to be read in.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="ct">Cancellation for the request.</param>
    /// <returns>The pull request, or a failure that the caller records as an unknown state rather than dropping the node.</returns>
    Task<Result<PullRequest>> HistoricalPullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default);
}
