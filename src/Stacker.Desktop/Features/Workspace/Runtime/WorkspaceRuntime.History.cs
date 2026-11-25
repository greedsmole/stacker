using System.Collections.ObjectModel;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal IHistoryStore? HistoryStore = default!;
    internal IHistoryGit? HistoryObjects = default!;
    internal ChangeGraphService? HistoryGraph = default!;
    internal VersionComparisonService? HistoryComparisons = default!;
    internal ReviewProgressService? HistoryProgress = default!;
    private readonly SemaphoreSlim _localHistoryGate = new(1, 1);
    private (RepositorySnapshot Repository, string? Revision, StackDefinition? Extra, GraphSnapshot Graph)? _localHistory;

    internal async Task<GraphSnapshot?> ObserveLocalHistoryAsync(RepositorySnapshot repository, StackDocument document,
        CancellationToken ct, StackDefinition? extra = null)
    {
        if (HistoryGraph is null)
            return null;
        await _localHistoryGate.WaitAsync(ct);
        try
        {
            if (_localHistory is { } cached && cached.Repository.WorkspaceKey == repository.WorkspaceKey &&
                cached.Revision == document.Revision && cached.Extra == extra && cached.Repository.Refs.SequenceEqual(repository.Refs))
                return cached.Graph;
            var stacks = extra is null ? document.Stacks : document.Stacks.Append(extra).ToArray();
            var graph = await HistoryGraph.ObserveLocalAsync(repository, stacks, ct);
            ct.ThrowIfCancellationRequested();
            _localHistory = (repository, document.Revision, extra, graph);
            return graph;
        }
        finally { _localHistoryGate.Release(); }
    }

    internal void InvalidateLocalHistory() => _localHistory = null;
}
