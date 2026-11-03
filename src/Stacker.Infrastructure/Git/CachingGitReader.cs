using Stacker.Core.Framework;
// Purpose: Keeps recently parsed patches so a repeated search or a revisited file does not re-run git diff.
using Stacker.Core;
namespace Stacker.Infrastructure;

/// <summary>A bounded, byte-limited cache in front of the reader; every other call is passed straight through.</summary>
/// <remarks>Bounded by both entry count and total patch size, so a large comparison cannot grow without limit.</remarks>
public sealed class CachingGitReader(IGitRepositoryReader inner, int capacity = 32, long maxBytes = 48L * 1024 * 1024, IDiagnosticSink? diagnostics = null, IObjectStore? objects = null, IApplicationState? state = null) : IGitRepositoryReader, IBlobReader, IDemoSnapshotReader, IDisposable
{
    public Task<GitHubSnapshot?> ReadDemoSnapshotAsync(string root, CancellationToken ct = default) =>
        inner is IDemoSnapshotReader demo ? Boundary.UnwrapAsync(demo.ReadDemoSnapshotAsync(root, ct)) : Task.FromResult<GitHubSnapshot?>(null);
    private readonly Dictionary<string, FileDiff> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _bytes;
    private readonly QueryCoordinator _queries = new();
    private readonly ReadScheduler _scheduler = new();

    public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.OpenAsync(path, ct));
    public Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default) => Boundary.UnwrapAsync(WithLeaseAsync(root,t=>inner.MergeBasesAsync(root,left,right,t),ct));
    public Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default) => Boundary.UnwrapAsync(WithLeaseAsync(root,t=>inner.CountsAsync(root,parent,head,t),ct));
    public Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default) => Boundary.UnwrapAsync(WithLeaseAsync(root,t=>inner.FilesAsync(root,@base,head,t),ct));
    public Task<BlobContent?> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default) =>
        inner is IBlobReader reader ? Boundary.UnwrapAsync(WithLeaseAsync(root,t=>reader.ReadBlobAsync(root,commit,path,t),ct)) : Task.FromResult<BlobContent?>(null);
    private async Task<Result<T>> WithLeaseAsync<T>(string root,Func<CancellationToken,Task<Result<T>>> read,CancellationToken ct)
    {
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        var privateRoot=state is not null&&new[]{"objects","history-objects"}.Any(folder=>Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.Combine(state.Root,folder))+Path.DirectorySeparatorChar,comparison));
        if(objects is null||!privateRoot)return await read(ct);
        var lease=await objects.AcquireAsync(root,false,ct);if(lease.IsFailed)return Result<T>.Fail(lease.Problem!);
        using(lease.Value!)return await read(ct);
    }

    /// <summary>Returns a parsed patch, running git only on a miss.</summary>
    public async Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default)
    {
        var key = StableIdentity.For(root,@base,head,file.OldPath,file.NewPath);
        await _gate.WaitAsync(ct);
        try { if (_cache.TryGetValue(key, out var hit)) return hit; }
        finally { _gate.Release(); }
        var diff = await _queries.RunAsync(key, token => _scheduler.RunAsync(ReadWork.Diff, async readToken =>
        {
            var started=TimeProvider.System.GetTimestamp();
            var result=await WithLeaseAsync(root,t=>inner.PatchAsync(root,@base,head,file,t),readToken);
            diagnostics?.Record(new(OperationCorrelation.Current??Guid.NewGuid().ToString("N"),"GitDiff",result.Problem?.Code,TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,result.IsOk?Size(result.Value!):0));
            return result.OrThrow();
        }, token), ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var raced)) return raced;
            var size = Size(diff);
            if (size > maxBytes || capacity <= 0) return diff;
            _cache[key] = diff; _order.Enqueue(key); _bytes += size;
            while (_order.Count > capacity || _bytes > maxBytes)
            {
                var oldest = _order.Dequeue();
                if (_cache.Remove(oldest, out var evicted)) _bytes -= Size(evicted);
            }
            return diff;
        }
        finally { _gate.Release(); }
    }

    private static long Size(FileDiff diff) => 2L * (diff.Patch.Length + diff.Lines.Sum(line => (long)line.Text.Length))
        + 64L * diff.Lines.Count + 64L * diff.Hunks.Count;
    public void Dispose()=>_queries.Dispose();

    Task<Result<RepositorySnapshot>> IGitRepositoryReader.OpenAsync(string path,CancellationToken ct) => Boundary.CaptureAsync(() => OpenAsync(path, ct), ProblemCodes.GitFailed);

    Task<Result<IReadOnlyList<string>>> IGitRepositoryReader.MergeBasesAsync(string root,string left,string right,CancellationToken ct) => Boundary.CaptureAsync(() => MergeBasesAsync(root, left, right, ct), ProblemCodes.GitFailed);

    Task<Result<(int Ahead, int Behind)>> IGitRepositoryReader.CountsAsync(string root,string parent,string head,CancellationToken ct) => Boundary.CaptureAsync(() => CountsAsync(root, parent, head, ct), ProblemCodes.GitFailed);

    Task<Result<IReadOnlyList<FileChange>>> IGitRepositoryReader.FilesAsync(string root,string @base,string head,CancellationToken ct) => Boundary.CaptureAsync(() => FilesAsync(root, @base, head, ct), ProblemCodes.GitFailed);

    Task<Result<FileDiff>> IGitRepositoryReader.PatchAsync(string root,string @base,string head,FileChange file,CancellationToken ct) => Boundary.CaptureAsync(() => PatchAsync(root, @base, head, file, ct), ProblemCodes.GitFailed);

    Task<Result<BlobContent?>> IBlobReader.ReadBlobAsync(string root,string commit,string path,CancellationToken ct) => Boundary.CaptureAsync(() => ReadBlobAsync(root, commit, path, ct), ProblemCodes.GitFailed);

    Task<Result<GitHubSnapshot?>> IDemoSnapshotReader.ReadDemoSnapshotAsync(string root,CancellationToken ct) => Boundary.CaptureAsync(() => ReadDemoSnapshotAsync(root, ct), ProblemCodes.GitFailed);
}
