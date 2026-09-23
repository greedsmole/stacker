using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed class GitObjectCache(IProcessRunner runner, GitExecutable git, GhExecutable gh, ApplicationStore store, IGitHubReader? reader = null) : IGitObjectCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string DirectoryFor(GitHubRepositoryContext context) => Path.Combine(store.Root, "objects", ApplicationStore.Key(context.Identity));
    public async Task<RepositorySnapshot> PrepareAsync(GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var root = DirectoryFor(context); Directory.CreateDirectory(root);
            if (!File.Exists(Path.Combine(root, "HEAD"))) await Run(root, ["init", "--bare"], ct);
            var refs = new List<BranchRef>();
            foreach (var pr in prs)
            {
                ct.ThrowIfCancellationRequested();
                if (!await Exists(root, pr.HeadSha, ct) || !await Exists(root, pr.BaseSha, ct))
                {
                    if (reader is not null) await reader.VerifyAccessAsync(context, ct);
                    if (!Uri.TryCreate(context.CloneUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.Equals(uri.Host, context.Host, StringComparison.OrdinalIgnoreCase))
                        throw new StackerException("GitHub returned an unexpected clone URL.");
                    var headRef = $"refs/stacker/pr/{pr.Number}"; var baseRef = $"refs/stacker/base/{pr.Number}";
                    // Only the fixed helper program is interpreted by Git. Paths are supplied via a quoted environment variable.
                    await Run(root, ["-c", "credential.helper=", "-c", "credential.helper=!\"$STACKER_GH\" auth git-credential", "-c", "credential.interactive=false",
                        "fetch", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", context.CloneUrl,
                        $"+refs/pull/{pr.Number}/head:{headRef}", $"+refs/heads/{pr.BaseRef}:{baseRef}"], ct, TimeSpan.FromMinutes(3));
                    var head = await Run(root, ["rev-parse", headRef], ct); var @base = await Run(root, ["rev-parse", baseRef], ct);
                    if (head.StdOut.Trim() != pr.HeadSha || @base.StdOut.Trim() != pr.BaseSha)
                        throw new StackerException($"PR #{pr.Number} changed while downloading. Refresh GitHub before comparing.");
                }
                refs.Add(new(HeadRef(pr), pr.HeadSha)); refs.Add(new(BaseRef(pr), pr.BaseSha));
            }
            return new(root, refs.DistinctBy(r => r.Name).ToArray(), 0);
        }
        finally { _gate.Release(); }
    }
    public static string HeadRef(PullRequest pr) => $"refs/heads/__pr_{pr.Number}";
    public static string BaseRef(PullRequest pr) => $"refs/heads/__base_{pr.Number}";
    public async Task ClearAsync(GitHubRepositoryContext context, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { var path = DirectoryFor(context); if (Directory.Exists(path)) Directory.Delete(path, true); }
        finally { _gate.Release(); }
    }
    private async Task<bool> Exists(string root, string sha, CancellationToken ct)
    {
        if (sha.Length is not (40 or 64) || !sha.All(Uri.IsHexDigit)) throw new StackerException("Invalid GitHub commit SHA.");
        var result = await runner.RunAsync(new(git.Resolve(), ["cat-file", "-e", sha + "^{commit}"], root), ct);
        return result.ExitCode == 0;
    }
    private async Task<ProcessResult> Run(string root, string[] args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var response = await runner.RunAsync(new(git.Resolve(), ["-c", "core.hooksPath=/dev/null", .. args], root, timeout,
            Environment: new Dictionary<string, string> { ["STACKER_GH"] = gh.Resolve(), ["GIT_TERMINAL_PROMPT"] = "0", ["GH_PROMPT_DISABLED"] = "1", ["GH_DEBUG"] = "" }), ct);
        if (response.ExitCode != 0) throw new StackerException("Git cache: " + response.StdErr.Trim());
        return response;
    }
}
