using System.Collections.Concurrent;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal WorkspaceState _workspace = new();
    internal WorkspacePersistence _workspacePersistence = default!;
    private readonly Dictionary<string, CoalescingWriter<WorkspaceState>> _workspaceWriters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Problem> _workspaceSaveProblems = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _workspaceIo = new(1, 1);
    private readonly CancellationTokenSource _workspaceWriteLifetime = new();
    internal Problem? WorkspaceSaveProblem => _workspaceSaveProblems.Values.FirstOrDefault();
    internal Problem? WorkspaceSaveProblemFor(string key) => _workspaceSaveProblems.GetValueOrDefault(key);
    internal Dictionary<string, ReviewViewModel> _reviews = [];
    internal LinkedList<string> _reviewOrder = new();
    internal SemaphoreSlim _reviewGate = new(1, 1);
    internal SemaphoreSlim _syncGate = new(1, 1);
    internal string? _displayedStateKey = default!;
    internal string? _displayedGroupKey;
    internal bool _applyingChanges = default!;
    internal GitHubSnapshot? _pendingRemote
    {
        get => Session.Pending?.Remote; set
        {
            if (value is null)
                Session.DiscardPending();
            else
                Session.Offer(value, _historyRemoteGraph);
        }
    }
    internal bool _pendingLocal = default!;
    internal RefreshCoordinator? _coordinator = default!;
    internal bool _windowActive = default!;

    internal Task QueueWorkspaceWrite(string root, WorkspaceState snapshot)
    {
        if (!_workspaceWriters.TryGetValue(root, out var writer))
            _workspaceWriters[root] = writer = new(copy => WriteWorkspaceAsync(root, copy));
        return writer.Queue(snapshot);
    }

    internal Task FlushWorkspaceWritesAsync() => Task.WhenAll(_workspaceWriters.Values.Select(writer => writer.FlushAsync()));

    internal void StopWorkspaceWrites()
    {
        _workspaceWriteLifetime.Cancel();
        // Writes run independently of the dispatcher. Drain any atomic file cleanup before releasing the profile.
        var drained = FlushWorkspaceWritesAsync();
        if (drained.Wait(TimeSpan.FromSeconds(2)))
            _workspaceWriteLifetime.Dispose();
        else
            _ = drained.ContinueWith(_ => _workspaceWriteLifetime.Dispose(), TaskScheduler.Default);
    }

    internal async Task<Result<WorkspaceState>> LoadWorkspaceAsync(string key, string root, CancellationToken ct)
    {
        if (_workspaceWriters.TryGetValue(key, out var writer))
            await writer.FlushAsync().WaitAsync(ct);
        await _workspaceIo.WaitAsync(ct);
        try { return await _workspacePersistence.LoadAsync(key, ct, root); }
        finally { _workspaceIo.Release(); }
    }

    private async Task WriteWorkspaceAsync(string root, WorkspaceState snapshot)
    {
        var ct = _workspaceWriteLifetime.Token;
        try
        {
            await _workspaceIo.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await _workspacePersistence.SaveAsync(root, snapshot, ct).ConfigureAwait(false);
                if (result.Problem is { } problem)
                    _workspaceSaveProblems[root] = problem;
                else
                    _workspaceSaveProblems.TryRemove(root, out _);
            }
            finally { _workspaceIo.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _workspaceSaveProblems[root] = Boundary.Problem(ex, ProblemCodes.StoreWrite); }
    }
}
