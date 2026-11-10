using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed partial class GitHubCli : IHistoryRemoteReader
{
    /// <summary>Loads a pull-request snapshot from GitHub for historical review context.</summary>
    /// <param name="context">Repository and host for the API request.</param><param name="number">Pull request number.</param><param name="ct">Request cancellation token.</param>
    /// <returns>The parsed pull request or a classified GitHub problem.</returns>
    public Task<Result<PullRequest>> HistoricalPullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default) =>
        Boundary.CaptureAsync(async () => ParsePr(await Api(context.Host, $"{Repo(context)}/pulls/{number}", ct)), ProblemCodes.GitHubRequest);
}
