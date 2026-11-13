using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Pins historical commits and performs textual analysis in the opened local repository.</summary>
public sealed class HistoryGit(IProcessRunner runner, GitExecutable executable, IApplicationState state, RepositoryGit? repositoryGit = null, SqliteHistoryStore? database = null, LegacyObjectImporter? legacy = null) : IHistoryGit
{
    private readonly RepositoryGit _git = repositoryGit ?? new(runner, executable);
    private readonly SqliteHistoryStore _database = database ?? new(state.Root);
    private LegacyObjectImporter Importer => legacy ?? new(_git, state, _database);
    private string Scope(string scope) => _database.ObjectScope(scope);
    public Task<Result<ContentAvailability>> AvailabilityAsync(RepositoryIdentity repository, string scope, CancellationToken ct = default) => Boundary.CaptureAsync(async () =>
    {
        using var gate = await _git.LockAsync(repository, ct);
        var canonical = Scope(scope);
        if (_database.HistoryRemoved(repository, canonical))
            return ContentAvailability.Removed;
        await Importer.ImportAsync(repository, canonical, ct);
        await MoveAliasedPinsAsync(repository, canonical, ct);
        var refs = await _git.RefsAsync(repository, RepositoryGit.Prefix(repository, "history", canonical), ct);
        if (refs.Count == 0)
            return _database.RepositoryContentStatus(repository, canonical) == ContentAvailability.Available ? ContentAvailability.Unavailable : ContentAvailability.NeedsDownload;
        return (await _git.MissingAsync(repository, refs.Values, ct)).Count == 0 ? ContentAvailability.Available : ContentAvailability.Unavailable;
    }, ProblemCodes.GitFailed);
    public Task<Result<Unit>> CaptureAsync(RepositoryIdentity repository, string scope, IReadOnlyList<string> commits, CancellationToken ct = default) => Boundary.CaptureAsync(async () =>
    {
        using var gate = await _git.LockAsync(repository, ct);
        var canonical = Scope(scope);
        var prefix = RepositoryGit.Prefix(repository, "history", canonical);
        _git.CheckRemoval(repository, prefix);
        if (!_database.HistoryRemoved(repository, canonical))
        {
            await Importer.ImportAsync(repository, canonical, ct);
            await MoveAliasedPinsAsync(repository, canonical, ct);
        }
        else
            foreach (var source in _database.LegacySources(repository, canonical, state.Root))
                if (Directory.Exists(source) || File.Exists(source))
                    _database.MarkImported(repository, source);
        if ((await _git.MissingAsync(repository, commits, ct)).Count > 0)
            throw new StackerException("Historical commits are missing from the opened repository. Load the current PR changes first.");
        await _git.PinAsync(repository, prefix, commits, ct);
        _database.RecordRepositoryContents(repository, canonical, ContentAvailability.Available);
    }, ProblemCodes.GitFailed);
    public async Task<Result<ObjectSnapshotLease>> AcquireAsync(RepositoryIdentity repository, string scope, CancellationToken ct = default)
    {
        var status = await AvailabilityAsync(repository, scope, ct);
        if (status.IsFailed)
            return Result<ObjectSnapshotLease>.Fail(status.Problem!);
        if (status.Value != ContentAvailability.Available)
            return Result<ObjectSnapshotLease>.Fail(ProblemCodes.GitRefMissing, status.Value switch
            {
                ContentAvailability.Removed => "History pins were explicitly removed. Metadata and drafts remain.",
                ContentAvailability.Unavailable => "Historical commits are unavailable in this local repository.",
                _ => "Open the layer to retain its historical commits in this repository."
            });
        return Boundary.Capture(() => { _git.CheckRemoval(repository, RepositoryGit.Prefix(repository, "history", Scope(scope))); return _git.AcquireRead(repository); }, ProblemCodes.GitFailed);
    }
    public Task<Result<Unit>> ForgetContentsAsync(RepositoryIdentity repository, string scope, CancellationToken ct = default) => Boundary.CaptureAsync(async () =>
    {
        var canonical = Scope(scope);
        foreach (var related in _database.ObjectScopes(repository, canonical))
        {
            await _git.RemoveAsync(repository, RepositoryGit.Prefix(repository, "history", related), ct);
            _database.RecordRepositoryContents(repository, related, ContentAvailability.Removed);
        }
    }, ProblemCodes.GitFailed);
    private async Task MoveAliasedPinsAsync(RepositoryIdentity repository, string canonical, CancellationToken ct)
    {
        foreach (var related in _database.ObjectScopes(repository, canonical).Where(s => s != canonical))
        {
            if (_database.HistoryRemoved(repository, related))
                continue;
            var prefix = RepositoryGit.Prefix(repository, "history", related);
            _git.CheckRemoval(repository, prefix);
            var refs = await _git.RefsAsync(repository, prefix, ct);
            if (refs.Count == 0)
                continue;
            await _git.PinAsync(repository, RepositoryGit.Prefix(repository, "history", canonical), refs.Values, ct);
            await _git.DeleteRefsAsync(repository, prefix, ct);
        }
    }
    public Task<Result<string?>> TransplantAsync(RepositoryIdentity repository, string oldBase, string oldHead, string newBase, CancellationToken ct = default) => Boundary.CaptureAsync(async () =>
    {
        foreach (var sha in new[] { oldBase, oldHead, newBase })
            RepositoryGit.Sha(sha);
        using var lease = _git.AcquireRead(repository);
        var result = await _git.AnalysisAsync(repository, ["merge-tree", "--write-tree", "--merge-base=" + oldBase, oldHead, newBase], ct);
        if (result.ExitCode == 1)
            return (string?)null;
        if (result.ExitCode != 0)
            throw new StackerException("Git cannot normalize this version: " + result.StdErr.Trim());
        var tree = result.StdOut.Split('\n')[0].Trim();
        RepositoryGit.Sha(tree);
        return tree;
    }, ProblemCodes.GitFailed);
    public Task<Result<IReadOnlyList<string>>> MergeAsync(RepositoryIdentity repository, string parent, string head, CancellationToken ct = default) => Boundary.CaptureAsync<IReadOnlyList<string>>(async () =>
    {
        RepositoryGit.Sha(parent);
        RepositoryGit.Sha(head);
        using var lease = _git.AcquireRead(repository);
        var result = await _git.AnalysisAsync(repository, ["merge-tree", "--write-tree", "--name-only", "-z", parent, head], ct);
        if (result.ExitCode == 0)
            return [];
        if (result.ExitCode != 1)
            throw new StackerException("Merge preview unavailable: " + result.StdErr.Trim());
        return result.StdOut.Split('\0').Skip(1).TakeWhile(s => s.Length > 0).ToArray();
    }, ProblemCodes.GitFailed);
}
