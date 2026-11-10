using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Leases protect readers from cleanup. Only disposable downloads are evicted automatically.</summary>
public sealed class ObjectStore(IApplicationState state, long budget = 2L * 1024 * 1024 * 1024) : IObjectStore
{
    private sealed class Entry { public int Leases; public bool Remove; }
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _budget = budget;
    public Result<Unit> SetBudget(long bytes) => Boundary.Capture(() =>
    {
        if (bytes < 1024 * 1024) throw new StackerException("Cache budget must be at least 1 MiB.");
        Interlocked.Exchange(ref _budget, bytes);
    }, ProblemCodes.StateInvalid);
    private string Validate(string root)
    {
        var full = Path.GetFullPath(root); var owner = Path.GetFullPath(state.Root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(owner, StringComparison.Ordinal)) throw new StackerException("Object storage must belong to this application."); return full;
    }
    public Task<Result<ObjectSnapshotLease>> AcquireAsync(string root, bool create = false, CancellationToken ct = default) => Task.Run(() => Boundary.Capture(() =>
    {
        ct.ThrowIfCancellationRequested(); root = Validate(root);
        lock (_sync)
        {
            if (create) Directory.CreateDirectory(root);
            if (!Directory.Exists(root)) throw new StackerException("Downloaded contents are unavailable.");
            if (!_entries.TryGetValue(root, out var entry)) _entries[root] = entry = new();
            if (entry.Remove) throw new StackerException("Contents are scheduled for removal. Close their active views first.");
            Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow);
            entry.Leases++; return new ObjectSnapshotLease(root, () => Release(root));
        }
    }, ProblemCodes.StoreRead), ct);
    private void Release(string root)
    {
        lock (_sync)
        {
            var entry = _entries[root]; entry.Leases--;
            if (entry.Leases == 0 && entry.Remove) _ = Task.Run(() =>
            {
                lock (_sync) if (_entries.TryGetValue(root, out var pending) && ReferenceEquals(pending, entry) && pending.Leases == 0)
                { try { Delete(root); _entries.Remove(root); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            });
        }
    }
    private static void Delete(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
    public Task<Result<bool>> RemoveAsync(string root, CancellationToken ct = default) => Task.Run(() => Boundary.Capture(() =>
    {
        ct.ThrowIfCancellationRequested(); root = Validate(root);
        lock (_sync)
        {
            if (_entries.TryGetValue(root, out var entry) && entry.Leases > 0) { entry.Remove = true; return false; }
            Delete(root); _entries.Remove(root); return true;
        }
    }, ProblemCodes.StoreWrite), ct);
    private static long Size(string root) => Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => new FileInfo(f).Length) : 0;
    public Task<Result<ObjectStoreStatus>> TrimAsync(CancellationToken ct = default) => Task.Run(() => Boundary.Capture(() =>
    {
        lock (_sync)
        {
            foreach (var (root, entry) in _entries.Where(pair => pair.Value.Remove && pair.Value.Leases == 0).ToArray())
                try { Delete(root); _entries.Remove(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var downloads = Path.Combine(state.Root, "objects"); var history = Path.Combine(state.Root, "history-objects");
            var roots = Directory.Exists(downloads) ? Directory.GetDirectories(downloads).OrderBy(Directory.GetLastWriteTimeUtc).ToArray() : [];
            var sizes = roots.ToDictionary(r => r, Size); var total = sizes.Values.Sum();
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested(); if (total <= _budget) break;
                if (_entries.TryGetValue(root, out var entry) && entry.Leases > 0) continue;
                Delete(root); total -= sizes[root]; _entries.Remove(root);
            }
            return new ObjectStoreStatus(total, Size(history), _budget, _entries.Values.Sum(e => e.Leases));
        }
    }, ProblemCodes.StoreWrite), ct);
}
