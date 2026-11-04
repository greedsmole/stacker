using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class HistoryWorkspaceViewModel
{
    [ObservableProperty] private ChangeNode? _dependencyParent;
    [ObservableProperty] private ChangeNode? _dependencyChild;
    [ObservableProperty] private GraphSnapshot? _historicalGraph;
    [ObservableProperty] private string _newLocalRef = "";
    private bool _confirmForget;
    [ObservableProperty] private string _lineNumber = "1";
    public IReadOnlyList<ChangeNode> GraphNodes => Graph.Nodes;
    [RelayCommand] private async Task ForgetHistoricalCodeAsync()
    {
        if (!_confirmForget) { _confirmForget = true; Message = "Click Remove history pins again to confirm. Versions and draft text stay. Git may later collect unreachable historical commits."; return; }
        await ExecuteAsync(async () =>
        {
            (await _objects.ForgetContentsAsync(_repository,Graph.Scope, _lifetime.Token)).OrThrow();
            _confirmForget = false; _current = null; Files.Clear(); Versions.Clear(); Preview = "";ClearDocument();
            Message = "History pins are scheduled for removal after active views close. The repository and its objects remain managed by Git.";
        });
    }
    [RelayCommand] private async Task SetDependencyAsync()
    {
        if (DependencyParent is not { } parent || DependencyChild is not { } child) return;
        await ExecuteAsync(async () =>
        {
            var edges = Graph.Edges.Where(e => e.Child != child.Id).ToList();
            if (parent.Id == child.Id || ChangeGraphService.Descendants(child.Id, edges).Contains(parent.Id))
            { Message = "Dependency would create a cycle. No relationship was saved."; return; }
            edges.Add(new(parent.Id, child.Id, "explicit"));
            var next = Graph with { Edges = edges, ObservedAt = DateTimeOffset.UtcNow };
            (await _store.SaveGraphAsync(next, _lifetime.Token)).OrThrow();
            Graph = (await _store.LatestGraphAsync(Graph.Scope, _lifetime.Token)).OrThrow()!; OnPropertyChanged(nameof(Topology));
            Message = "Explicit parent saved. Reopen history to capture the updated graph. Stack definitions were not rewritten.";
        });
    }
    [RelayCommand] private void ShowHistoricalGraph()
    {
        if (HistoricalGraph is not { } graph) return;
        ClearDocument();
        Preview = string.Join("\n", graph.Nodes.Select(n => $"{n.Title} · {n.Reference} · {n.State}")) + "\n\n" +
            string.Join("\n", graph.Edges.Select(e => $"{graph.Nodes.First(n => n.Id == e.Parent).Title} → {graph.Nodes.First(n => n.Id == e.Child).Title} · {e.Source}"));
        Message = $"Graph observed {graph.ObservedAt.LocalDateTime:g} · read-only historical view";
    }
    [RelayCommand] private async Task RebindLocalRefAsync()
    {
        if (SelectedNode is not { PullRequest: null } node) { Message = "Select a local node first."; return; }
        await ExecuteAsync(async () =>
        {
            (await _store.RebindLocalNodeAsync(node, NewLocalRef.Trim(), _lifetime.Token)).OrThrow();
            Message = "History identity rebound. Update the local stack definition and reopen history to use this ref.";
        });
    }
    [RelayCommand] private async Task TraceLineAsync()
    {
        if (SelectedFile is not { } selected || !int.TryParse(LineNumber, out var line) || line < 1) { Message = "Enter a positive final-file line number."; return; }
        await ExecuteAsync(async () =>
        {
            var evolution = await new FileEvolutionService(_git).BuildAsync(_root, _path, selected.Change.NewPath, _lifetime.Token);
            var origin = await new FileEvolutionService(_git).TraceLineAsync(_root, evolution, line, _lifetime.Token);
            Message = origin; Preview = origin;
        });
    }
}
