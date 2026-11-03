using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
namespace Stacker.Infrastructure;

public sealed class HistoryGit(IProcessRunner runner, GitExecutable git, IApplicationState state) : IHistoryGit
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Root(string scope) => Path.Combine(state.Root, "history-objects", ApplicationStore.Key(scope));
    private static void Sha(string value)
    {
        if (value.Length is not (40 or 64) || !value.All(Uri.IsHexDigit)) throw new StackerException("Invalid history object ID.");
    }
    private async Task<ProcessResult> Run(string root, string[] args, CancellationToken ct, bool allowFailure = false)
    {
        var result = (await runner.RunAsync(new(git.Resolve(), ["-c", "core.hooksPath=" + Neutral.HooksPath,
            "-c", "protocol.file.allow=always", "-c", "gc.auto=0", .. args], root, TimeSpan.FromMinutes(2),
            Environment: new Dictionary<string, string> { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Neutral.EmptyConfigFile,
                ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_NO_LAZY_FETCH"] = "1", ["LC_ALL"] = "C" }), ct)).OrThrow();
        if (!allowFailure && result.ExitCode != 0) throw new StackerException("History objects unavailable: " + result.StdErr.Trim());
        return result;
    }
    public Task<Result<string>> CaptureAsync(string scope, string source, IReadOnlyList<string> commits, CancellationToken ct = default) =>
        Boundary.CaptureAsync(async () =>
        {
            foreach (var sha in commits) Sha(sha);
            if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source)) throw new StackerException("History import requires a local repository.");
            await _gate.WaitAsync(ct);
            try
            {
                var root = Root(scope); Directory.CreateDirectory(root);
                if (!File.Exists(Path.Combine(root, "HEAD"))) await Run(root, ["init", "--bare"], ct);
                var missing = new List<string>();
                foreach (var sha in commits.Distinct()) if ((await Run(root, ["cat-file", "-e", sha + "^{commit}"], ct, true)).ExitCode != 0) missing.Add(sha);
                if (missing.Count > 0) await Run(root, ["fetch", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", "--", source, .. missing], ct);
                foreach (var sha in commits.Distinct()) await Run(root, ["update-ref", "refs/stacker/history/" + sha, sha], ct);
                return root;
            }
            finally { _gate.Release(); }
        }, ProblemCodes.GitFailed);
    public Task<Result<string>> RootAsync(string scope, CancellationToken ct = default) => Task.FromResult(
        Directory.Exists(Root(scope)) ? Result<string>.Ok(Root(scope)) : Result<string>.Fail(ProblemCodes.GitRefMissing, "Historical objects have not been downloaded."));
    public Task<Result<string?>> TransplantAsync(string root, string oldBase, string oldHead, string newBase, CancellationToken ct = default) =>
        Boundary.CaptureAsync(async () =>
        {
            Sha(oldBase); Sha(oldHead); Sha(newBase); CheckRoot(root);
            var result = await Run(root, ["merge-tree", "--write-tree", "--merge-base=" + oldBase, oldHead, newBase], ct, true);
            if (result.ExitCode == 1) return null;
            if (result.ExitCode != 0) throw new StackerException("Git cannot normalize this version: " + result.StdErr.Trim());
            var tree = result.StdOut.Split('\n')[0].Trim(); Sha(tree); return (string?)tree;
        }, ProblemCodes.GitFailed);
    public Task<Result<IReadOnlyList<string>>> MergeAsync(string root, string parent, string head, CancellationToken ct = default) =>
        Boundary.CaptureAsync<IReadOnlyList<string>>(async () =>
        {
            Sha(parent); Sha(head); CheckRoot(root);
            var result = await Run(root, ["merge-tree", "--write-tree", "--name-only", "-z", parent, head], ct, true);
            if (result.ExitCode == 0) return [];
            if (result.ExitCode != 1) throw new StackerException("Merge preview unavailable: " + result.StdErr.Trim());
            return result.StdOut.Split('\0').Skip(1).TakeWhile(s => s.Length > 0).ToArray();
        }, ProblemCodes.GitFailed);
    public Task<Result<Unit>> ForgetContentsAsync(string scope, CancellationToken ct = default) => Boundary.CaptureAsync(async () =>
    {
        await _gate.WaitAsync(ct);
        try
        {
            var root = Root(scope);
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            { ct.ThrowIfCancellationRequested(); File.SetAttributes(file, FileAttributes.Normal); }
            Directory.Delete(root, true);
        }
        finally { _gate.Release(); }
    }, ProblemCodes.StoreWrite);
    private void CheckRoot(string root)
    {
        var owner = Path.GetFullPath(Path.Combine(state.Root, "history-objects")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(owner, StringComparison.Ordinal) || !File.Exists(Path.Combine(root, "HEAD")))
            throw new StackerException("Merge simulation requires a private history cache.");
    }
}
