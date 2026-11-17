using Microsoft.CodeAnalysis.MSBuild;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

public sealed class RoslynGraphReader(GitRepositoryReader git, RepositoryGit objects, IProcessRunner runner) : ICSharpGraphReader
{
    private const int CachedRevisions = 8;
    private const int CachedGraphUnits = 200_000;
    private readonly SemaphoreSlim _worker = new(1, 1);
    // Only the worker reads and updates the cache.
    private readonly LinkedList<(CSharpGraphRequest Request, CSharpGraph Graph, int Units)> _cache = new();
    private int _cachedUnits;

    public Task<Result<IReadOnlyList<string>>> EntriesAsync(RepositoryIdentity repository, string commit, CancellationToken ct) =>
        Boundary.CaptureAsync<IReadOnlyList<string>>(async () =>
        {
            using var lease = objects.AcquireRead(repository);
            await RequireCommitAsync(repository, commit, ct);
            var paths = await git.TrackedPathsAsync(repository.Root, commit, ct);
            return paths.Where(RoslynWorkspace.IsEntry).OrderByDescending(RoslynWorkspace.IsSolution)
                .ThenBy(path => path, StringComparer.Ordinal).ToArray();
        }, ProblemCodes.GitFailed);

    public Task<Result<CSharpGraph>> ReadAsync(CSharpGraphRequest request, CancellationToken ct) =>
        Boundary.CaptureAsync(() => Task.Run(() => ReadCoreAsync(request, ct), ct), ProblemCodes.StateInvalid);

    private async Task<CSharpGraph> ReadCoreAsync(CSharpGraphRequest request, CancellationToken ct)
    {
        await _worker.WaitAsync(ct);
        try
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Request != request) continue;
                _cache.Remove(node);
                _cache.AddFirst(node);
                return node.Value.Graph;
            }
            var snapshot = await BuildAsync(request, ct);
            ct.ThrowIfCancellationRequested();
            var units = snapshot.Relationships.Count + snapshot.Types.Sum(type => 1 + type.Members.Count);
            if (snapshot.IsComplete && units <= CachedGraphUnits)
            {
                _cache.AddFirst((request, snapshot, units));
                _cachedUnits += units;
                while (_cache.Count > CachedRevisions || _cachedUnits > CachedGraphUnits)
                {
                    _cachedUnits -= _cache.Last!.Value.Units;
                    _cache.RemoveLast();
                }
            }
            return snapshot;
        }
        finally { _worker.Release(); }
    }

    private async Task<CSharpGraph> BuildAsync(CSharpGraphRequest request, CancellationToken ct)
    {
        var (repository, commit, entry) = request;
        using var lease = objects.AcquireRead(repository);
        await RequireCommitAsync(repository, commit, ct);
        var paths = (await git.TrackedPathsAsync(repository.Root, commit, ct)).ToHashSet(StringComparer.Ordinal);
        if (!RoslynWorkspace.IsEntry(entry))
            throw new StackerException("Select a C# solution or project.");
        if (!paths.Contains(entry))
        {
            if (!paths.Any(RoslynWorkspace.IsProject))
                return new(commit, [], [], [], []);
            return new(commit, [], [], [], [$"{entry} is absent at {commit[..8]}. Choose an entry present at both revisions for a complete comparison."]);
        }

        var checkout = Directory.CreateTempSubdirectory("stacker-csharp-").FullName;
        try
        {
            await objects.RunAsync(repository, ["-c", "core.sparseCheckout=false", "-c", "submodule.recurse=false",
                "worktree", "add", "--detach", checkout, commit], ct);
            // Align MSBuild's file and analyzer-config paths on systems whose temp path is a symlink.
            var root = (await objects.RunAsync(repository, ["-C", checkout, "rev-parse", "--show-toplevel"], ct)).StdOut.TrimEnd('\r', '\n');
            var projectPath = Path.GetFullPath(entry, root);
            var link = new FileInfo(projectPath);
            if (link.LinkTarget is not null)
                throw new StackerException("Select a C# solution or project that is stored directly in the revision.");
            var restore = (await runner.RunAsync(new("dotnet",
                ["restore", projectPath, "--disable-build-servers", "-p:NuGetAudit=false", "-nr:false"], root,
                TimeSpan.FromMinutes(2)), ct)).OrThrow();
            if (restore.ExitCode != 0)
                throw new StackerException($"Cannot restore {entry} at {commit[..8]}. Check the .NET SDK and package sources.\n{restore.StdOut.Trim()}\n{restore.StdErr.Trim()}".TrimEnd());

            using var workspace = MSBuildWorkspace.Create();
            await RoslynWorkspace.OpenAsync(workspace, root, entry, ct);
            return await RoslynGraphExtractor.ExtractAsync(workspace, root, commit, paths, ct);
        }
        finally
        {
            var removal = await objects.RunAsync(repository, ["worktree", "remove", "--force", checkout], CancellationToken.None,
                allowFailure: true, timeout: TimeSpan.FromSeconds(20));
            // A cancelled worktree add can fail before registering the directory.
            if (removal.ExitCode != 0 && Directory.Exists(checkout))
            {
                var worktrees = await objects.RunAsync(repository, ["worktree", "list", "--porcelain", "-z"], CancellationToken.None);
                if (worktrees.StdOut.Split('\0').Contains("worktree " + checkout, StringComparer.Ordinal))
                    throw new StackerException("Cannot remove the temporary C# checkout: " + removal.StdErr.Trim());
                Directory.Delete(checkout, recursive: true);
            }
        }
    }

    private async Task RequireCommitAsync(RepositoryIdentity repository, string commit, CancellationToken ct)
    {
        if ((await objects.MissingAsync(repository, [commit], ct)).Count > 0)
            throw new StackerException($"Commit {commit[..8]} is unavailable. Refresh the branch or PR and retry.");
    }
}
