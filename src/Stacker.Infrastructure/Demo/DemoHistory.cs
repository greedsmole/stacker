using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed partial class DemoRepositoryGenerator
{
    private async Task SeedHistoryAsync(string root, GitHubSnapshot snapshot, Func<string[], Task<string>> git, CancellationToken ct)
    {
        var pr = snapshot.PullRequests.Single(p => p.Number == 102);
        var previousHead = await git(["rev-parse", "auth/api^"]);
        var canonicalRoot = await git(["rev-parse", "--show-toplevel"]);
        var repository = identities is null ? new RepositoryIdentity(new(StableIdentity.For(await git(["rev-parse", "--path-format=absolute", "--git-common-dir"]))[..32]), new(StableIdentity.For(canonicalRoot)[..32]), canonicalRoot, await git(["rev-parse", "--path-format=absolute", "--git-common-dir"])) : (await identities.RegisterAsync(canonicalRoot, ct)).OrThrow();
        foreach (var scope in new[] { "local:" + canonicalRoot, snapshot.Context.Identity })
        {
            var remote = scope == snapshot.Context.Identity;
            var node = (await history!.ResolveNodeAsync(scope, remote ? pr.HeadRef : "refs/heads/" + pr.HeadRef, pr.Title, remote ? pr.Number : null, ct)).OrThrow();
            (await historyObjects!.CaptureAsync(repository, scope, [pr.BaseSha, previousHead, pr.HeadSha], ct)).OrThrow();
            var previous = (await history.ObserveAsync(new("", node.Id, pr.BaseSha, previousHead, pr.BaseSha, DateTimeOffset.UtcNow.AddDays(-1)), ct)).OrThrow();
            (await history.SetCheckpointAsync(new(node.Id, "Auth.cs", previous.Id, DateTimeOffset.UtcNow.AddDays(-1)), false, ct)).OrThrow();
            (await history.ObserveAsync(new("", node.Id, pr.BaseSha, pr.HeadSha, pr.BaseSha, DateTimeOffset.UtcNow), ct)).OrThrow();
        }
    }
}
