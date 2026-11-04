using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

/// <summary>A snapshot-bound history view sharing layer and file selection with the main workspace.</summary>
public sealed partial class HistoryWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IHistoryStore _store;
    private readonly IHistoryGit _objects;
    private readonly IGitRepositoryReader _git;
    private readonly VersionComparisonService _comparisons;
    private readonly ReviewProgressService _progress;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IReadOnlyList<(ChangeNode Node, ChangeVersion Version)> _path;
    private readonly string _root;
    private readonly RepositoryIdentity _repository;
    public IDisposable? ContentLease { get; set; }
    public Func<ChangeNode,FileChange?,Task>? SelectionChanged { get; set; }
    private Task _selectionSync=Task.CompletedTask;
    private bool _loadingSelection;
    partial void OnSelectedFileChanged(FileReviewStatus? value)
    {if(!_loadingSelection&&SelectedNode is { } node)_selectionSync=SelectDocumentFileAsync(node,value?.Change);}
    private async Task SyncSelectionAsync(ChangeNode node,FileChange? file)
    {try{if(SelectionChanged is not null)await SelectionChanged(node,file);}catch(Exception ex){Message=ex.Message;}}
    private readonly Func<ChangeNode, ChangeVersion, CancellationToken, Task<ReviewDiscussion?>>? _discussion;
    public GraphSnapshot Graph { get; private set; }
    public string Topology => string.Join("\n", Graph.Edges.Select(e => $"{Graph.Nodes.First(n => n.Id == e.Parent).Title} → {Graph.Nodes.First(n => n.Id == e.Child).Title}"));
    public ObservableCollection<ChangeNode> Nodes { get; } = [];
    public ObservableCollection<ChangeVersion> Versions { get; } = [];
    public ObservableCollection<FileReviewStatus> Files { get; } = [];
    public ObservableCollection<HistoryAttentionItem> Attention { get; } = [];
    public ObservableCollection<GraphSnapshot> GraphHistory { get; } = [];
    [ObservableProperty] private ChangeNode? _selectedNode;
    [ObservableProperty] private ChangeVersion? _previousVersion;
    [ObservableProperty] private FileReviewStatus? _selectedFile;
    [ObservableProperty] private string _message = "Select a layer, then a file. Viewing code never marks it reviewed.";
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private bool _isBusy;
    private ChangeVersion? _current;
    private int _generation;
    private bool _disposed;
    public HistoryWorkspaceViewModel(IHistoryStore store, IHistoryGit objects, IGitRepositoryReader git,
        VersionComparisonService comparisons, ReviewProgressService progress, GraphSnapshot graph, RepositoryIdentity repository,
        IReadOnlyList<(ChangeNode Node, ChangeVersion Version)> path, Func<ChangeNode, ChangeVersion, CancellationToken, Task<ReviewDiscussion?>>? discussion = null)
    {
        _store = store; _objects = objects; _git = git; _comparisons = comparisons; _progress = progress;
        Graph = graph; _repository=repository;_root = repository.Root; _path = path; _discussion = discussion;
        foreach (var item in path.DistinctBy(p => p.Node.Id)) Nodes.Add(item.Node);
    }
    public async Task InitializeAsync()
    {
        foreach (var graph in (await _store.GraphsAsync(Graph.Scope, _lifetime.Token)).OrThrow()) GraphHistory.Add(graph);
        HistoricalGraph = GraphHistory.LastOrDefault();
        SelectedNode = Nodes.FirstOrDefault();
        await LoadLayerAsync();
    }
    partial void OnSelectedNodeChanged(ChangeNode? value) { _generation++; Files.Clear(); Versions.Clear(); Preview = ""; ClearDocument(); }
    [RelayCommand] private async Task LoadLayerAsync()
    {
        var node = SelectedNode; _loadingSelection=true; var generation = ++_generation;
        if (node is null) { _loadingSelection=false; return; }
        IsBusy = true;
        try
        {
            var versions = (await _store.VersionsAsync(node.Id, _lifetime.Token)).OrThrow();
            var current = _path.First(p => p.Node.Id == node.Id).Version;
            var files = (await _git.FilesAsync(_root, current.MergeBase!, current.HeadSha, _lifetime.Token)).OrThrow();
            var status = await _progress.StatusAsync(_repository,Graph.Scope, current, files, _lifetime.Token);
            if (generation != _generation) return;
            _current = current; Versions.Clear();
            foreach (var version in versions.Where(v => v.Id != current.Id).Reverse()) Versions.Add(version);
            PreviousVersion = Versions.FirstOrDefault(); Files.Clear();
            foreach (var file in status) Files.Add(file);
            SelectedFile = Files.FirstOrDefault();
            if(SelectionChanged is not null)await SyncSelectionAsync(node,SelectedFile?.Change);
            Message = $"{status.Count(f => f.State == FileReviewState.Reviewed)}/{status.Count} files reviewed on this layer. Select Load layer after changing the layer.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; _loadingSelection=false; }
    }
    [RelayCommand] private async Task MarkReviewedAsync() => await SetReviewedAsync(false);
    [RelayCommand] private async Task UnmarkReviewedAsync() => await SetReviewedAsync(true);
    private async Task SetReviewedAsync(bool remove)
    {
        var file = SelectedFile; var current = _current;
        if (file is null || current is null || SelectedNode?.Id != current.ChangeId) return;
        try
        {
            (await _store.SetCheckpointAsync(new(current.ChangeId, file.Change.NewPath, current.Id, DateTimeOffset.UtcNow), remove, _lifetime.Token)).OrThrow();
            await LoadLayerAsync(); SelectedFile = Files.FirstOrDefault(f => f.Change.NewPath == file.Change.NewPath);
        }
        catch (Exception ex) { Message = ex.Message; }
    }
    [RelayCommand] private async Task ShowCurrentAsync() => await ShowVersionAsync(_current);
    [RelayCommand] private async Task ShowPreviousAsync() => await ShowVersionAsync(PreviousVersion);
    private async Task ShowVersionAsync(ChangeVersion? version)
    {
        var selected = SelectedFile;
        if (version?.MergeBase is null || selected is null) { Message = "Select an available version and file."; return; }
        await ExecuteAsync(async () =>
        {
            var files = (await _git.FilesAsync(_root, version.MergeBase, version.HeadSha, _lifetime.Token)).OrThrow();
            var file = files.FirstOrDefault(f => f.NewPath == selected.Change.NewPath || f.OldPath == selected.Change.OldPath);
            Preview = file is null ? "No file change in this version." : "";
            await ShowDocumentAsync(new(DiffDocumentMode.Current,[new(version.Label,_root,version.MergeBase,version.HeadSha,files)],"Original layer diff"),file?.NewPath,version.Id);
            Message = version.Label + " · original layer diff · read-only";
        });
    }
    [RelayCommand] private Task CompareVersionsAsync()=>CompareVersionDocumentAsync(DiffDocumentMode.BetweenVersions);
    private async Task CompareVersionDocumentAsync(DiffDocumentMode mode)
    {
        if (_current is null || PreviousVersion is null) { Message = "No earlier observed version. Open this layer again after it changes."; return; }
        var current = _current; var previous = PreviousVersion; var file = SelectedFile;
        await ExecuteAsync(async () =>
        {
            var comparison = await _comparisons.CompareAsync(_repository,Graph.Scope, previous, current, _lifetime.Token);
            Message = comparison.Unavailable ?? $"Textual comparison: {comparison.CodeChanges.Count} files changed beyond replay; {comparison.BaseChanges.Count} base files changed. This is not semantic equivalence.";
            Preview = "Base changes:\n" + string.Join("\n", comparison.BaseChanges.Select(f => f.DisplayPath)) + "\n\nOwn changes after replay:\n" + string.Join("\n", comparison.CodeChanges.Select(f => f.DisplayPath));
            var changed = comparison.CodeChanges.FirstOrDefault(f => f.NewPath == file?.Change.NewPath);
            var sections = new List<DiffDocumentSection>();
            if(comparison.IsReliable)
            {
                if(comparison.BaseChanges.Count>0)sections.Add(new("Base changes",_root,previous.MergeBase!,current.MergeBase!,comparison.BaseChanges));
                if(comparison.CodeChanges.Count>0)sections.Add(new("Own changes after replay",_root,comparison.ExpectedTree!,current.HeadSha,comparison.CodeChanges));
            }
            await ShowDocumentAsync(new(mode,sections,Message),changed?.NewPath,previous.Id);
        });
    }
    [RelayCommand] private async Task SinceReviewedAsync()
    {
        if (SelectedFile?.CheckedVersion is not { } version) { Message = "This file has no review checkpoint."; return; }
        PreviousVersion = version; await CompareVersionDocumentAsync(DiffDocumentMode.SinceReviewed);
    }
    [RelayCommand] private async Task AnalyzeAsync() => await ExecuteAsync(async () =>
    {
        ClearDocument();
        var result = await new StackAnalysisService(_store, _objects, _git).AnalyzeAsync(_repository,Graph, _lifetime.Token);
        Preview = string.Join("\n\n", result.Issues.Select(i => $"{Graph.Nodes.First(n => n.Id == i.ChangeId).Title} · {i.Kind}\n{i.Message}\n{string.Join("\n", i.Paths)}"));
        Message = result.Issues.Count == 0 ? "No issues found in the captured edges. This does not prove semantic correctness." : $"{result.Issues.Count} observations for this graph snapshot.";
    });
    [RelayCommand] private async Task AttentionAsync() => await ExecuteAsync(async () =>
    {
        ClearDocument();
        Attention.Clear();
        foreach (var (node, version) in _path.DistinctBy(p => p.Node.Id))
        {
            var files = (await _git.FilesAsync(_root, version.MergeBase!, version.HeadSha, _lifetime.Token)).OrThrow();
            var statuses = await _progress.StatusAsync(_repository,Graph.Scope, version, files, _lifetime.Token);
            foreach (var status in statuses.Where(f => f.State != FileReviewState.Reviewed || f.Concern is not null)) Attention.Add(new(node.Id, status.Change.NewPath, node.Title + " · " + status.Label, status.Concern is not null));
            foreach (var concern in (await _store.ConcernsAsync(node.Id, _lifetime.Token)).OrThrow().Where(c => !statuses.Any(s => s.Change.NewPath == c.Path)))
                Attention.Add(new(node.Id, concern.Path, node.Title + " · " + concern.Path + " · BLOCKER on former file: " + concern.Text, true));
        }
        Preview = string.Join("\n", Attention); Message = $"{Attention.Count} files require attention, in dependency order.";
    });
    [RelayCommand] private async Task EvolutionAsync()
    {
        if (SelectedFile is not { } selected) return;
        await ExecuteAsync(async () =>
        {
            var evolution = await new FileEvolutionService(_git).BuildAsync(_root, _path, selected.Change.NewPath, _lifetime.Token);
            var text = new System.Text.StringBuilder(); var sections = new List<DiffDocumentSection>();
            foreach (var step in evolution.Steps)
            {
                text.AppendLine($"{step.Change.Title} · {step.Version.Label}" + (step.Continuous ? "" : " · ancestry gap — independent diff"));
                if (step.File is { } file) sections.Add(new(step.Change.Title,_root,step.Version.MergeBase!,step.Version.HeadSha,[file]));
                else text.AppendLine("No file change in this layer.");
                if (_discussion is not null && step.Change.PullRequest is not null && step.File is { } changed)
                {
                    try
                    {
                        var discussion = await _discussion(step.Change, step.Version, _lifetime.Token);
                        foreach (var thread in discussion?.Threads.Where(t => t.Path == changed.NewPath || t.Path == changed.OldPath) ?? [])
                            text.AppendLine($"Original PR #{step.Change.PullRequest} · {thread.Label}\n" + string.Join("\n", thread.Comments.Select(c => c.Author + ": " + c.DisplayBody)));
                    }
                    catch (StackerException ex) { text.AppendLine("Discussion unavailable: " + ex.Message); }
                }
                if (text.Length > 2_000_000) { text.AppendLine("Preview limit reached. Inspect individual layers."); break; }
            }
            await ShowDocumentAsync(new(DiffDocumentMode.FileEvolution,sections,"File evolution"),selected.Change.NewPath);
            Preview = text.ToString(); Message = "File evolution on the selected stack path. Comments stay in their original PR.";
        });
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true;
        try { await action(); } catch (OperationCanceledException) { } catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); ClearDocument(); ContentLease?.Dispose(); ContentLease=null; Preview = ""; }
}
