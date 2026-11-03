using Stacker.Core.Framework;
// Purpose: Fetches selected PR commits into an application-owned bare cache so remote review does not alter the user checkout.
using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed class GitObjectCache(IProcessRunner runner, GitExecutable git, GhExecutable gh, ApplicationStore store, IGitHubReader? reader = null, IObjectStore? objects = null) : IGitObjectCache
{
    private readonly IObjectStore _objects = objects ?? new ObjectStore(store);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string DirectoryFor(GitHubRepositoryContext context) => Path.Combine(store.Root, "objects", ApplicationStore.Key(context.Identity));
    public async Task<RepositorySnapshot> PrepareAsync(GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var root = DirectoryFor(context); using var lease=(await _objects.AcquireAsync(root,true,ct)).OrThrow();
            if (!File.Exists(Path.Combine(root, "HEAD"))) await Run(root, ["init", "--bare"], ct);
            var requested = prs.DistinctBy(pr => pr.Number).ToArray();
            var missing = new List<PullRequest>();
            foreach (var pr in requested)
            {
                ct.ThrowIfCancellationRequested();
                if (!await Exists(root, pr.HeadSha, ct) || !await Exists(root, pr.BaseSha, ct))
                    missing.Add(pr);
            }
            if (missing.Count > 0)
            {
                if (reader is not null) (await reader.VerifyAccessAsync(context, ct)).OrThrow();
                if (!Uri.TryCreate(context.CloneUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.Equals(uri.Host, context.Host, StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0)
                    throw new StackerException("GitHub returned an unexpected repository URL.");
                // One fetch for all refs required by this comparison. This remains in the
                // application-owned bare cache; no clone or user-repository refs are changed.
                var refspecs = missing.SelectMany(pr => new[]
                {
                    $"+refs/pull/{pr.Number}/head:refs/stacker/pr/{pr.Number}",
                    $"+refs/heads/{pr.BaseRef}:refs/stacker/base/{pr.Number}"
                }).ToArray();
                // Only the fixed helper program is interpreted by Git. Paths are supplied via a quoted environment variable.
                await Run(root, ["-c", "credential.helper=", "-c", "credential.helper=!\"$STACKER_GH\" auth git-credential", "-c", "credential.interactive=false",
                    "fetch", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", context.CloneUrl, .. refspecs], ct, TimeSpan.FromMinutes(3), isDownload: true);
                foreach (var pr in missing)
                {
                    var head = await Run(root, ["rev-parse", $"refs/stacker/pr/{pr.Number}"], ct);
                    var @base = await Run(root, ["rev-parse", $"refs/stacker/base/{pr.Number}"], ct);
                    if (head.StdOut.Trim() != pr.HeadSha || @base.StdOut.Trim() != pr.BaseSha)
                        throw new StackerException($"PR #{pr.Number} changed while downloading. Refresh GitHub before comparing.");
                }
            }
            var refs = requested.SelectMany(pr => new[] { new BranchRef(HeadRef(pr), pr.HeadSha), new BranchRef(BaseRef(pr), pr.BaseSha) }).ToArray();
            (await _objects.TrimAsync(ct)).OrThrow();
            return new(root, refs.DistinctBy(r => r.Name).ToArray(), 0);
        }
        finally { _gate.Release(); }
    }
    public static string HeadRef(PullRequest pr) => $"refs/heads/__pr_{pr.Number}";
    public static string BaseRef(PullRequest pr) => $"refs/heads/__base_{pr.Number}";
    public async Task ClearAsync(GitHubRepositoryContext context,CancellationToken ct=default)
    { (await _objects.RemoveAsync(DirectoryFor(context),ct)).OrThrow(); }
    private async Task<bool> Exists(string root, string sha, CancellationToken ct)
    {
        if (sha.Length is not (40 or 64) || !sha.All(Uri.IsHexDigit)) throw new StackerException("Invalid GitHub commit SHA.");
        var result = (await runner.RunAsync(new(git.Resolve(), ["cat-file", "-e", sha + "^{commit}"], root), ct)).OrThrow();
        return result.ExitCode == 0;
    }
    private async Task<ProcessResult> Run(string root, string[] args, CancellationToken ct, TimeSpan? timeout = null, bool isDownload = false)
    {
        // Git for Windows can otherwise use an OpenSSL bundle that lacks corporate CAs
        // trusted by Windows (and gh). Scope the backend to this process, not Git config files.
        string[] tlsOptions = isDownload && OperatingSystem.IsWindows() ? ["-c", "http.sslBackend=schannel"] : [];
        var response = (await runner.RunAsync(new(git.Resolve(), ["-c", "core.hooksPath=" + Framework.Neutral.HooksPath, .. tlsOptions, .. args], root, timeout,
            Environment: new Dictionary<string, string> { ["STACKER_GH"] = gh.Resolve(), ["GIT_TERMINAL_PROMPT"] = "0", ["GH_PROMPT_DISABLED"] = "1", ["GH_DEBUG"] = "" }, UnsetEnvironment: gh.UnsetEnvironment), ct)).OrThrow();
        if (response.ExitCode != 0)
        {
            var detail = response.StdErr.Trim();
            if (isDownload && IsCertificateTrustError(detail))
            {
                var trustStore = OperatingSystem.IsWindows() ? "Windows certificate store" : "Git certificate trust store";
                throw new StackerException($"Git cache: the HTTPS certificate chain is not trusted. Ask your administrator to check the server's certificate chain and install the corporate root/intermediate CA in the {trustStore}. " +
                    "Signing in to gh again will not fix certificate trust.\n\n" + detail);
            }
            throw new StackerException("Git cache: " + detail);
        }
        return response;
    }

    private static bool IsCertificateTrustError(string error) =>
        new[] { "unable to get local issuer certificate", "self-signed certificate", "SEC_E_UNTRUSTED_ROOT", "CERT_E_UNTRUSTEDROOT", "CERT_TRUST_IS_UNTRUSTED_ROOT", "CERT_TRUST_IS_PARTIAL_CHAIN" }
            .Any(message => error.Contains(message, StringComparison.OrdinalIgnoreCase));

    Task<Result<RepositorySnapshot>> IGitObjectCache.PrepareAsync(GitHubRepositoryContext context,IReadOnlyList<PullRequest> prs,CancellationToken ct) => Boundary.CaptureAsync(() => PrepareAsync(context, prs, ct), ProblemCodes.GitFailed);

    Task<Result<Unit>> IGitObjectCache.ClearAsync(GitHubRepositoryContext context,CancellationToken ct) => Boundary.CaptureAsync(() => ClearAsync(context, ct), ProblemCodes.GitFailed);
}
