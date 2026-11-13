using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class HistorySafetyTests
{
    private sealed class ClosedReader : IHistoryRemoteReader
    {
        public int Reads
        {
            get; private set;
        }
        public Task<Result<PullRequest>> HistoricalPullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(Result<PullRequest>.Ok(DiscoveryTests.Pr(number, "main", "A") with
            {
                State = "Merged"
            }));
        }
    }
}
