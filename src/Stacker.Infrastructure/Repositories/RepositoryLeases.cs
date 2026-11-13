using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed partial class RepositoryGit
{
    private readonly object _readersSync = new();
    private readonly Dictionary<string, Readers> _readers = [];
    private readonly Dictionary<string, RepositoryIdentity> _roots = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    internal void Register(RepositoryIdentity repository)
    {
        if (!Path.IsPathFullyQualified(repository.Root) || !Directory.Exists(repository.Root))
            throw new StackerException("The selected repository folder is unavailable.");
        lock (_readersSync)
            _roots[Path.GetFullPath(repository.Root)] = repository;
    }
    public ObjectSnapshotLease? TryAcquireRead(string root)
    {
        lock (_readersSync)
            return _roots.TryGetValue(Path.GetFullPath(root), out var repository) ? AcquireRead(repository) : null;
    }
    internal ObjectSnapshotLease AcquireRead(RepositoryIdentity repository)
    {
        Register(repository);
        var key = Path.GetFullPath(repository.CommonDirectory);
        lock (_readersSync)
        {
            if (!_readers.TryGetValue(key, out var state))
                _readers[key] = state = new();
            state.Count++;
            return new(repository.Root, () => ReleaseRead(key));
        }
    }
    private void ReleaseRead(string key)
    {
        KeyValuePair<string, RepositoryIdentity>[] pending;
        lock (_readersSync)
        {
            var state = _readers[key];
            if (--state.Count > 0)
                return;
            pending = state.Pending.ToArray();
        }
        foreach (var (prefix, repository) in pending)
            _ = RetryRemovalAsync(repository, prefix);
    }
    internal async Task RemoveAsync(RepositoryIdentity repository, string prefix, CancellationToken ct)
    {
        using var gate = await LockAsync(repository, ct);
        var key = Path.GetFullPath(repository.CommonDirectory);
        lock (_readersSync)
        {
            if (!_readers.TryGetValue(key, out var state))
                _readers[key] = state = new();
            state.Pending[prefix] = repository;
            if (state.Count > 0)
                return;
        }
        await DeleteRefsAsync(repository, prefix, ct);
        lock (_readersSync)
            _readers[key].Pending.Remove(prefix);
    }
    internal void CheckRemoval(RepositoryIdentity repository, string prefix)
    {
        lock (_readersSync)
            if (_readers.TryGetValue(Path.GetFullPath(repository.CommonDirectory), out var state) && state.Pending.ContainsKey(prefix))
                throw new StackerException("Close active comparisons before downloading removed history again.");
    }
    private async Task RetryRemovalAsync(RepositoryIdentity repository, string prefix)
    {
        try
        {
            await RemoveAsync(repository, prefix, CancellationToken.None);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (StackerException) { }
    }
}
