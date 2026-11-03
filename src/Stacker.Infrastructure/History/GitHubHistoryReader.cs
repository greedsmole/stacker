using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed partial class GitHubCli : IHistoryRemoteReader
{
    public Task<Result<PullRequest>> HistoricalPullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default) =>
        Boundary.CaptureAsync(async () => ParsePr(await Api(context.Host, $"{Repo(context)}/pulls/{number}", ct)), ProblemCodes.GitHubRequest);
}
