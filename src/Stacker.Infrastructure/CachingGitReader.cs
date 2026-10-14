// Purpose: Keeps recently parsed patches so a repeated search or a revisited file does not re-run git diff.
using Stacker.Core;
namespace Stacker.Infrastructure;

/// <summary>A bounded, byte-limited cache in front of the reader; every other call is passed straight through.</summary>
/// <remarks>Bounded by both entry count and total patch size, so a large comparison cannot grow without limit.</remarks>
public sealed class CachingGitReader(IGitRepositoryReader inner, int capacity = 32, long maxBytes = 48L * 1024 * 1024) : IGitRepositoryReader, IBlobReader
{
    private readonly Dictionary<string, FileDiff> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _bytes;

    public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default) => inner.OpenAsync(path, ct);
    public Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default) => inner.MergeBasesAsync(root, left, right, ct);
    public Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default) => inner.CountsAsync(root, parent, head, ct);
    public Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default) => inner.FilesAsync(root, @base, head, ct);
    public Task<BlobContent?> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default) =>
        inner is IBlobReader reader ? reader.ReadBlobAsync(root, commit, path, ct) : Task.FromResult<BlobContent?>(null);

    /// <summary>Returns a parsed patch, running git only on a miss.</summary>
    public async Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default)
    {
        var key = string.Join('|', root, @base, head, file.OldPath, file.NewPath);
        await _gate.WaitAsync(ct);
        try { if (_cache.TryGetValue(key, out var hit)) return hit; }
        finally { _gate.Release(); }
        var diff = await inner.PatchAsync(root, @base, head, file, ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var raced)) return raced;
            _cache[key] = diff; _order.Enqueue(key); _bytes += diff.Patch.Length;
            while (_order.Count > capacity || (_bytes > maxBytes && _order.Count > 1))
            {
                var oldest = _order.Dequeue();
                if (_cache.Remove(oldest, out var evicted)) _bytes -= evicted.Patch.Length;
            }
            return diff;
        }
        finally { _gate.Release(); }
    }
}
