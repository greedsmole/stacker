// Purpose: Keeps recently parsed patches so a repeated search or a revisited file does not re-run git diff.
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>A bounded, byte-limited cache in front of the reader; every other call is passed straight through.</summary>
/// <remarks>Bounded by both entry count and total patch size, so a large comparison cannot grow without limit. The defaults are 32 entries
/// and 48 MiB (<c>48 * 1024 * 1024</c> bytes) of estimated retained patch memory; a patch larger than the byte budget is returned but never
/// stored, and a non-positive capacity stores nothing, so one huge comparison cannot evict the whole cache. A key is the SHA-256 identity of
/// root, base, head and both paths, so a revisited file or a repeated search reuses a parsed patch instead of running Git again. Eviction is
/// FIFO by insertion order - a hit does not refresh an entry's position - and size is an estimate over retained text, not a heap measurement.
/// Identical concurrent misses share one Git read through <see cref="QueryCoordinator"/>, and those reads run through
/// <see cref="ReadScheduler"/> at the diff class's limit of two. Repository and object-store leases keep the paths being read alive while
/// cleanup may remove them. Only patches are cached; every other read is delegated.</remarks>
/// <param name="inner">Reader that actually runs Git; its failures pass through unchanged.</param>
/// <param name="capacity">Maximum number of cached patches; 32 by default, and a non-positive value disables storing.</param>
/// <param name="maxBytes">Maximum estimated retained bytes; 48 MiB by default.</param>
/// <param name="diagnostics">Optional sink that receives one <c>GitDiff</c> event per executed read.</param>
/// <param name="objects">Optional object store whose lease protects a read inside an application-owned object folder.</param>
/// <param name="state">Application data root used to decide whether a read targets a legacy object folder.</param>
/// <param name="repositoryGit">Optional repository registry whose read lease protects the opened repository's object database.</param>
public sealed class CachingGitReader(IGitRepositoryReader inner, int capacity = 32, long maxBytes = 48L * 1024 * 1024, IDiagnosticSink? diagnostics = null, IObjectStore? objects = null, IApplicationState? state = null, RepositoryGit? repositoryGit = null) : IGitRepositoryReader, IBlobReader, IDemoSnapshotReader, IDisposable
{
    /// <summary>Passes a demo snapshot read through when the inner reader supports it, and otherwise reports that this reader has no demo data.</summary>
    /// <param name="root">Root of the demo repository.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The recorded snapshot, or null when the inner reader cannot supply demo data.</returns>
    /// <remarks>Not cached and not leased: it is a single document read whose result describes the demo selected at open time. A failure from
    /// the inner reader is rethrown here and becomes a failed result at the explicit port member.</remarks>
    public Task<GitHubSnapshot?> ReadDemoSnapshotAsync(string root, CancellationToken ct = default) =>
        inner is IDemoSnapshotReader demo ? Boundary.UnwrapAsync(demo.ReadDemoSnapshotAsync(root, ct)) : Task.FromResult<GitHubSnapshot?>(null);
    /// <summary>Parsed patches keyed by <see cref="StableIdentity"/> over root, both commit ids and both paths; ordinal because the key is hex.</summary>
    private readonly Dictionary<string, FileDiff> _cache = new(StringComparer.Ordinal);
    /// <summary>Insertion order of the cache keys, used for FIFO eviction; a cache hit does not move a key to the back.</summary>
    private readonly Queue<string> _order = new();
    /// <summary>Guards the dictionary, the queue and the byte counter, so a lookup, an insert and an eviction cannot interleave.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Estimated bytes currently retained, maintained as the sum of <see cref="Size"/> over the cached entries.</summary>
    private long _bytes;
    /// <summary>Shares one in-flight patch read between callers with the same identity; the last subscriber leaving cancels it.</summary>
    private readonly QueryCoordinator _queries = new();
    /// <summary>Caps concurrent diff reads at two (<see cref="ReadWork.Diff"/>), so a burst of comparisons cannot start unlimited Git processes.</summary>
    private readonly ReadScheduler _scheduler = new();

    /// <summary>Opens the repository through the inner reader without caching or leasing, because a stale ref list would poison every later read.</summary>
    /// <param name="path">Folder inside the working tree to open.</param>
    /// <param name="ct">Cancels the inner reads.</param>
    /// <returns>The fresh snapshot; opening is never served from the cache.</returns>
    /// <remarks>Nothing is stored, so two opens of the same path always observe the repository as it is now. A failure is rethrown here and
    /// captured at the explicit port member.</remarks>
    public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default) => Boundary.UnwrapAsync(inner.OpenAsync(path, ct));

    /// <summary>Reads the merge bases through the lease path; the result is not cached.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="left">A commit id in the repository.</param>
    /// <param name="right">Another commit id in the repository.</param>
    /// <param name="ct">Cancels the lease acquisition and the read.</param>
    /// <returns>Every best common ancestor, in Git's order.</returns>
    /// <remarks>This answer depends on the repository's current state and is cheap to re-read, so it always reaches the inner reader. The
    /// lease taken by <see cref="WithLeaseAsync"/> keeps the object database alive for the duration of the read.</remarks>
    public Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default) => Boundary.UnwrapAsync(WithLeaseAsync(root, t => inner.MergeBasesAsync(root, left, right, t), ct));

    /// <summary>Reads the parent/head divergence counts through the lease path; the result is not cached.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="parent">The parent-side commit id.</param>
    /// <param name="head">The layer-side commit id.</param>
    /// <param name="ct">Cancels the lease acquisition and the read.</param>
    /// <returns>The head's own commit count and the parent's unmerged commit count.</returns>
    /// <remarks>Timing-sensitive planning input, so it is never served from a cache; only the parsed patch working set is cached.</remarks>
    public Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default) => Boundary.UnwrapAsync(WithLeaseAsync(root, t => inner.CountsAsync(root, parent, head, t), ct));

    /// <summary>Reads the changed-file metadata through the lease path; the result is not cached.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="base">The base-side commit id.</param>
    /// <param name="head">The head-side commit id.</param>
    /// <param name="ct">Cancels the lease acquisition and the read.</param>
    /// <returns>One entry per changed path, in Git's report order.</returns>
    /// <remarks>This is the cheap call that fills a comparison's file list before any patch is loaded, so it is delegated rather than cached;
    /// the expensive part, the patches, is what the cache exists for.</remarks>
    public Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default) => Boundary.UnwrapAsync(WithLeaseAsync(root, t => inner.FilesAsync(root, @base, head, t), ct));

    /// <summary>Reads immutable blob text through the lease path when the inner reader supports blobs, and otherwise reports an absent blob.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="commit">Commit whose tree is read.</param>
    /// <param name="path">Repository-relative path of the blob at that commit.</param>
    /// <param name="ct">Cancels the lease acquisition and the read.</param>
    /// <returns>The blob, or null when the inner reader cannot read blobs or the blob is absent or over its limit.</returns>
    /// <remarks>Highlighted content has its own cache, so this layer only forwards the read. Reporting null instead of failing keeps a reader that
    /// does not support blobs from breaking a comparison; the comparison renders that side unhighlighted.</remarks>
    public Task<BlobContent?> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default) =>
        inner is IBlobReader reader ? Boundary.UnwrapAsync(WithLeaseAsync(root, t => reader.ReadBlobAsync(root, commit, path, t), ct)) : Task.FromResult<BlobContent?>(null);

    /// <summary>Runs one read with the repository read lease and, for application-owned object folders, an object-store lease.</summary>
    /// <typeparam name="T">Value the read produces.</typeparam>
    /// <param name="root">Repository or object root being read.</param>
    /// <param name="read">The read to run; it receives the caller's token.</param>
    /// <param name="ct">Cancels the object-store lease acquisition and the read.</param>
    /// <returns>The read's result, or the object store's failure when a lease cannot be taken.</returns>
    /// <remarks>Two independent lifetimes protect a read. The repository lease tells cleanup that a diff still uses the object database; it is
    /// taken whenever a registry was supplied and released when the read returns. The object-store lease is additional and taken only when the
    /// root lies inside the application's legacy <c>objects</c> or <c>history-objects</c> folder, because those folders are removable once no reader
    /// holds them. The prefix check requires a directory separator after the folder, so the folder path itself is not treated as private, and it is
    /// case-insensitive on Windows. Without an object store, or outside those folders, the read runs under the repository lease alone; a lease that
    /// cannot be taken fails the result instead of reading unprotected.</remarks>
    private async Task<Result<T>> WithLeaseAsync<T>(string root, Func<CancellationToken, Task<Result<T>>> read, CancellationToken ct)
    {
        using var localLease = repositoryGit?.TryAcquireRead(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var privateRoot = state is not null && new[] { "objects", "history-objects" }.Any(folder => Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.Combine(state.Root, folder)) + Path.DirectorySeparatorChar, comparison));
        if (objects is null || !privateRoot)
            return await read(ct);
        var lease = await objects.AcquireAsync(root, false, ct);
        if (lease.IsFailed)
            return Result<T>.Fail(lease.Problem!);
        using (lease.Value!)
            return await read(ct);
    }

    /// <summary>Returns a parsed patch, running git only on a miss.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="base">The base-side commit id.</param>
    /// <param name="head">The head-side commit id.</param>
    /// <param name="file">The file whose patch is loaded; both old and new paths are part of the key.</param>
    /// <param name="ct">Cancels this caller's await; the shared read continues while other subscribers still wait.</param>
    /// <returns>The cached patch, or the freshly parsed one when nothing was stored.</returns>
    /// <remarks>Flow: the cache is checked under the gate; on a miss the caller joins a shared read for the same identity, which runs through
    /// <see cref="ReadScheduler"/> at the diff limit, takes the leases, calls the inner reader and records one diagnostic event with the problem
    /// code, duration and retained size. After the read the cache is checked again because another subscriber may have stored the same patch in the
    /// meantime. A patch whose estimate exceeds <c>maxBytes</c>, or any patch when <c>capacity</c> is not positive, is returned without being stored,
    /// so one oversized result cannot empty the cache. Storing then evicts from the oldest insertion until both the entry count and the byte total
    /// fit; eviction never removes the patch just added unless it alone exceeds a bound already handled above. Size is estimated as described on
    /// <see cref="Size"/>, so the byte bound tracks retained text rather than object count, and a hit does not extend an entry's lifetime.</remarks>
    public async Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default)
    {
        var key = StableIdentity.For(root, @base, head, file.OldPath, file.NewPath);
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var hit))
                return hit;
        }
        finally { _gate.Release(); }
        var diff = await _queries.RunAsync(key, token => _scheduler.RunAsync(ReadWork.Diff, async readToken =>
        {
            var started = TimeProvider.System.GetTimestamp();
            var result = await WithLeaseAsync(root, t => inner.PatchAsync(root, @base, head, file, t), readToken);
            diagnostics?.Record(new(OperationCorrelation.Current ?? Guid.NewGuid().ToString("N"), "GitDiff", result.Problem?.Code, TimeProvider.System.GetElapsedTime(started).TotalMilliseconds, result.IsOk ? Size(result.Value!) : 0));
            return result.OrThrow();
        }, token), ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var raced))
                return raced;
            var size = Size(diff);
            if (size > maxBytes || capacity <= 0)
                return diff;
            _cache[key] = diff;
            _order.Enqueue(key);
            _bytes += size;
            while (_order.Count > capacity || _bytes > maxBytes)
            {
                var oldest = _order.Dequeue();
                if (_cache.Remove(oldest, out var evicted))
                    _bytes -= Size(evicted);
            }
            return diff;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Estimates the bytes one parsed patch retains, for the byte bound and eviction.</summary>
    /// <param name="diff">Patch whose retained size is estimated.</param>
    /// <returns>The estimate in bytes.</returns>
    /// <remarks>Two bytes for every retained character - UTF-16 - in the raw patch text and in every line's text, plus 64 bytes for each line
    /// object and each hunk. The raw text and the line list both keep their copy of the content, which is why the character sum is doubled, and the
    /// flat 64-byte terms approximate object headers and list overhead. This is an estimate used for eviction, not a measurement of managed memory.
    /// </remarks>
    private static long Size(FileDiff diff) => 2L * (diff.Patch.Length + diff.Lines.Sum(line => (long)line.Text.Length))
        + 64L * diff.Lines.Count + 64L * diff.Hunks.Count;

    /// <summary>Cancels shared reads that no other subscriber still waits for and disposes their coordinator.</summary>
    /// <remarks>It does not wait for in-flight reads to finish; their remaining subscribers observe cancellation and each read's continuation disposes
    /// its own source. The read scheduler is not disposed here, so its gates outlive this instance. Disposal is idempotent through
    /// <see cref="QueryCoordinator.Dispose"/>.</remarks>
    public void Dispose() => _queries.Dispose();

    /// <inheritdoc cref="IGitRepositoryReader.OpenAsync"/>
    /// <remarks>The port member captures a thrown failure as a failed result with the catalogued <see cref="ProblemCodes.GitFailed"/> code, or the
    /// exception's own problem when it carries one; cancellation is rethrown. The work is the public open, which never touches the cache.</remarks>
    Task<Result<RepositorySnapshot>> IGitRepositoryReader.OpenAsync(string path, CancellationToken ct) => Boundary.CaptureAsync(() => OpenAsync(path, ct), ProblemCodes.GitFailed);

    /// <inheritdoc cref="IGitRepositoryReader.MergeBasesAsync"/>
    /// <remarks>The port member captures a thrown failure as a failed result with <see cref="ProblemCodes.GitFailed"/>; the read itself takes the
    /// leases described on <see cref="WithLeaseAsync"/> and is never cached.</remarks>
    Task<Result<IReadOnlyList<string>>> IGitRepositoryReader.MergeBasesAsync(string root, string left, string right, CancellationToken ct) => Boundary.CaptureAsync(() => MergeBasesAsync(root, left, right, ct), ProblemCodes.GitFailed);

    /// <inheritdoc cref="IGitRepositoryReader.CountsAsync"/>
    /// <remarks>The port member captures a thrown failure as a failed result with <see cref="ProblemCodes.GitFailed"/>; the result is not cached.</remarks>
    Task<Result<(int Ahead, int Behind)>> IGitRepositoryReader.CountsAsync(string root, string parent, string head, CancellationToken ct) => Boundary.CaptureAsync(() => CountsAsync(root, parent, head, ct), ProblemCodes.GitFailed);

    /// <inheritdoc cref="IGitRepositoryReader.FilesAsync"/>
    /// <remarks>The port member captures a thrown failure as a failed result with <see cref="ProblemCodes.GitFailed"/>; the file list is not cached.</remarks>
    Task<Result<IReadOnlyList<FileChange>>> IGitRepositoryReader.FilesAsync(string root, string @base, string head, CancellationToken ct) => Boundary.CaptureAsync(() => FilesAsync(root, @base, head, ct), ProblemCodes.GitFailed);

    /// <inheritdoc cref="IGitRepositoryReader.PatchAsync"/>
    /// <remarks>The port member captures a thrown failure - including <see cref="OutputLimitException"/> from the inner reader - as a failed result
    /// with <see cref="ProblemCodes.GitFailed"/>, so the cached path still reports an over-limit patch as a problem rather than truncating it.</remarks>
    Task<Result<FileDiff>> IGitRepositoryReader.PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct) => Boundary.CaptureAsync(() => PatchAsync(root, @base, head, file, ct), ProblemCodes.GitFailed);

    /// <inheritdoc cref="IBlobReader.ReadBlobAsync"/>
    /// <remarks>The port member captures a thrown failure as a failed result with <see cref="ProblemCodes.GitFailed"/>; an absent blob is still reported
    /// as a successful null, matching the port's "optional read" contract.</remarks>
    Task<Result<BlobContent?>> IBlobReader.ReadBlobAsync(string root, string commit, string path, CancellationToken ct) => Boundary.CaptureAsync(() => ReadBlobAsync(root, commit, path, ct), ProblemCodes.GitFailed);

    /// <inheritdoc cref="IDemoSnapshotReader.ReadDemoSnapshotAsync"/>
    /// <remarks>The port member captures a read or deserialization failure as a failed result with <see cref="ProblemCodes.GitFailed"/>; a reader that
    /// cannot supply demo data reports a successful null.</remarks>
    Task<Result<GitHubSnapshot?>> IDemoSnapshotReader.ReadDemoSnapshotAsync(string root, CancellationToken ct) => Boundary.CaptureAsync(() => ReadDemoSnapshotAsync(root, ct), ProblemCodes.GitFailed);
}
