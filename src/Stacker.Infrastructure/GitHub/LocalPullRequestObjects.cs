using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Downloads only missing PR objects into the selected repository using its configured origin.</summary>
public sealed class LocalPullRequestObjects(RepositoryGit git, IGitHubReader? reader = null, LegacyObjectImporter? legacy = null) : IPullRequestObjects
{
    public Task<Result<Unit>> EnsureAsync(RepositoryIdentity repository, GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default) => Boundary.CaptureAsync(async () =>
    {
        using var gate = await git.LockAsync(repository, ct);
        var requested = prs.DistinctBy(pr => pr.Number).ToArray(); var commits = requested.SelectMany(pr => new[] { pr.BaseSha, pr.HeadSha }).Distinct().ToArray();
        if (legacy is not null) await legacy.ImportAsync(repository, context.Identity, ct);
        var missing = await git.MissingAsync(repository, commits, ct);
        var prefix = RepositoryGit.Prefix(repository, "pr", context.Identity);
        git.CheckRemoval(repository, prefix);
        if (missing.Count > 0)
        {
            if (reader is not null) (await reader.VerifyAccessAsync(context, ct)).OrThrow();
            var origin = (await git.RunAsync(repository, ["remote", "get-url", "origin"], ct)).StdOut.Trim();
            ValidateOrigin(origin, context);
            var wanted = requested.Where(pr => missing.Contains(pr.BaseSha) || missing.Contains(pr.HeadSha)).ToArray();
            var refspecs = wanted.SelectMany(pr => new[]{
                $"+refs/pull/{pr.Number}/head:{prefix}{pr.Number}/head",$"+refs/heads/{pr.BaseRef}:{prefix}{pr.Number}/base"}).ToArray();
            var download = await git.RunAsync(repository, ["fetch", "--atomic", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", "--no-auto-maintenance", "--stdin", "origin"], ct, true, input: string.Join('\n', refspecs) + "\n", timeout: TimeSpan.FromMinutes(3));
            if (download.ExitCode != 0) throw new StackerException("Git fetch in the opened repository failed: " + download.StdErr.Trim());
            foreach (var pr in wanted)
            {
                var head = (await git.RunAsync(repository, ["rev-parse", prefix + pr.Number + "/head"], ct)).StdOut.Trim();
                var basis = (await git.RunAsync(repository, ["rev-parse", prefix + pr.Number + "/base"], ct)).StdOut.Trim();
                if (head != pr.HeadSha || basis != pr.BaseSha) throw new StackerException($"PR #{pr.Number} changed while downloading. Refresh GitHub before comparing.");
            }
            if ((await git.MissingAsync(repository, commits, ct)).Count > 0) throw new StackerException("The selected PR snapshot is unavailable in the opened repository. Refresh GitHub.");
        }
        await git.PinAsync(repository, prefix + "versions/", commits, ct);
    }, ProblemCodes.GitFailed);
    private static void ValidateOrigin(string origin, GitHubRepositoryContext context)
    {
        // File remotes support local mirrors; SSH aliases retain the user's native SSH configuration.
        if (Path.IsPathFullyQualified(origin) || origin.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return;
        var parsed = GitHubCli.ParseOrigin(origin);
        if (!parsed.Owner.Equals(context.Owner, StringComparison.OrdinalIgnoreCase) || !parsed.Name.Equals(context.Name, StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !parsed.Host.Equals(context.Host, StringComparison.OrdinalIgnoreCase))
            throw new StackerException("origin does not match the selected GitHub repository. Check the connection before downloading.");
    }
    public Task<Result<ObjectSnapshotLease>> AcquireAsync(RepositoryIdentity repository, CancellationToken ct = default) => Task.FromResult(Boundary.Capture(() =>
    { ct.ThrowIfCancellationRequested(); return git.AcquireRead(repository); }, ProblemCodes.GitFailed));
    public Task<Result<Unit>> ClearAsync(RepositoryIdentity repository, GitHubRepositoryContext context, CancellationToken ct = default) => Boundary.CaptureAsync(() =>
        git.RemoveAsync(repository, RepositoryGit.Prefix(repository, "pr", context.Identity), ct), ProblemCodes.GitFailed);
    public Task<Result<RepositoryObjectStorage>> InspectAsync(RepositoryIdentity repository, CancellationToken ct = default) => Boundary.CaptureAsync(async () =>
    {
        var response = await git.RunAsync(repository, ["count-objects", "-v"], ct);
        var values = response.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(": ", 2)).Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => parts[1]);
        long Kilobytes(string key) => values.TryGetValue(key, out var value) ? long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return new RepositoryObjectStorage(repository.CommonDirectory, checked((Kilobytes("size") + Kilobytes("size-pack") + Kilobytes("size-garbage")) * 1024));
    }, ProblemCodes.GitFailed);

}
