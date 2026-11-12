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
    public Func<ChangeNode, FileChange?, Task>? SelectionChanged { get; set; }
    internal Func<Task>? CheckpointChanged { get; set; }
    private readonly ISyntaxHighlighter? _highlighter;
    internal string Theme { get; set; }
    private bool _loadingSelection;
    partial void OnSelectedFileChanged(FileReviewStatus? value)
    {
        NotifySelectionCommands();
        if (!_loadingSelection && SelectedNode is { } node) SelectLayerFile(node, value?.Change);
    }
    partial void OnPreviousVersionChanged(ChangeVersion? value) => NotifySelectionCommands();
    /// <summary>Allows loading only when a layer is selected and no history operation is running.</summary>
    public bool CanLoadLayer => !IsBusy && SelectedNode is not null;
    /// <summary>Requires a selected file from the loaded version of the selected layer.</summary>
    public bool CanReviewFile => CanLoadLayer && _current?.ChangeId == SelectedNode?.Id && SelectedFile is not null;
    public bool CanShowCurrent => CanLoadLayer && _current?.ChangeId == SelectedNode?.Id && Files.Count > 0;
    public bool CanMarkReviewed => CanReviewFile && (ActiveDocumentSection is null || ActiveDocumentSection.Result.HeadSha == _current?.HeadSha);
    /// <summary>Requires both the selected layer's loaded version and an earlier version to compare.</summary>
    public bool CanCompareVersions => CanLoadLayer && _current?.ChangeId == SelectedNode?.Id && PreviousVersion is not null;
    /// <summary>Enables the earlier diff only when a file and earlier version are available.</summary>
    public bool CanShowPrevious => CanReviewFile && PreviousVersion is not null;
    /// <summary>Enables checkpoint operations only for a selected file with a saved review checkpoint.</summary>
    public bool CanUseCheckpoint => CanReviewFile && SelectedFile?.CheckedVersion is not null;
    /// <summary>Explains the next step needed to show a history document.</summary>
    public string EmptyDocumentMessage => _current is null ? "Select a layer and choose Load layer."
        : Files.Count == 0 ? "This layer has no changed files." : "Choose Current diff or compare with an earlier version.";
    private void NotifySelectionCommands()
    {
        LoadLayerCommand.NotifyCanExecuteChanged(); MarkReviewedCommand.NotifyCanExecuteChanged(); UnmarkReviewedCommand.NotifyCanExecuteChanged();
        ShowCurrentCommand.NotifyCanExecuteChanged(); ShowPreviousCommand.NotifyCanExecuteChanged(); CompareVersionsCommand.NotifyCanExecuteChanged();
        SinceReviewedCommand.NotifyCanExecuteChanged(); EvolutionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(EmptyDocumentMessage));
    }
    private async Task SyncSelectionAsync(ChangeNode node, FileChange? file)
    { try { if (SelectionChanged is not null) await SelectionChanged(node, file); } catch (Exception ex) { Message = ex.Message; } }
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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreview))] private string _preview = "";
    public bool HasPreview => Preview.Length > 0;
    [ObservableProperty] private bool _isBusy;
    private ChangeVersion? _current;
    private int _generation;
    private bool _disposed;
    public HistoryWorkspaceViewModel(IHistoryStore store, IHistoryGit objects, IGitRepositoryReader git,
        VersionComparisonService comparisons, ReviewProgressService progress, GraphSnapshot graph, RepositoryIdentity repository,
        IReadOnlyList<(ChangeNode Node, ChangeVersion Version)> path, Func<ChangeNode, ChangeVersion, CancellationToken, Task<ReviewDiscussion?>>? discussion = null,
        ISyntaxHighlighter? highlighter = null, string theme = "Dark")
    {
        _store = store; _objects = objects; _git = git; _comparisons = comparisons; _progress = progress;
        Graph = graph; _repository = repository; _root = repository.Root; _path = path; _discussion = discussion;
        _highlighter = highlighter; Theme = theme;
        foreach (var item in path.DistinctBy(p => p.Node.Id)) Nodes.Add(item.Node);
    }
    public async Task InitializeAsync()
    {
        foreach (var graph in (await _store.GraphsAsync(Graph.Scope, _lifetime.Token)).OrThrow()) GraphHistory.Add(graph);
        HistoricalGraph = GraphHistory.LastOrDefault();
        SelectedNode = Nodes.FirstOrDefault();
        await LoadLayerDataAsync();
    }
    partial void OnSelectedNodeChanged(ChangeNode? value)
    {
        _generation++; _current = null;
        // Clearing the old file is local state reset, not navigation to the new layer.
        var loadingSelection = _loadingSelection; _loadingSelection = true;
        try { SelectedFile = null; }
        finally { _loadingSelection = loadingSelection; }
        PreviousVersion = null;
        Files.Clear(); Versions.Clear(); Preview = ""; ClearDocument();
        Message = value is null ? "Select a layer to load its review history." : "Select Load layer to read this layer's files and versions.";
        NotifySelectionCommands();
    }
    [RelayCommand(CanExecute = nameof(CanLoadLayer))]
    private Task LoadLayerAsync() => LoadCurrentDiffAsync();

    internal async Task LoadCurrentDiffAsync(string? filePath = null)
    {
        if (await LoadLayerDataAsync(filePath) && CanShowCurrent) await ShowCurrentAsync();
    }

    private async Task<bool> LoadLayerDataAsync(string? filePath = null)
    {
        if (_disposed || SelectedNode is not { } node) return false;
        filePath ??= SelectedFile?.Change.NewPath;
        _loadingSelection = true;
        var generation = ++_generation;
        IsBusy = true;
        try
        {
            var versions = (await _store.VersionsAsync(node.Id, _lifetime.Token)).OrThrow();
            var current = _path.First(p => p.Node.Id == node.Id).Version;
            var files = (await _git.FilesAsync(_root, current.MergeBase!, current.HeadSha, _lifetime.Token)).OrThrow();
            var status = await _progress.StatusAsync(_repository, Graph.Scope, current, files, _lifetime.Token);
            if (_disposed || generation != _generation) return false;
            _current = current; Versions.Clear();
            foreach (var version in versions.Where(v => v.Id != current.Id).Reverse()) Versions.Add(version);
            PreviousVersion = Versions.FirstOrDefault(); Files.Clear();
            foreach (var file in status) Files.Add(file);
            SelectedFile = Files.FirstOrDefault(file => file.Change.NewPath == filePath) ?? Files.FirstOrDefault();
            if (SelectionChanged is not null) await SyncSelectionAsync(node, SelectedFile?.Change);
            if (_disposed || generation != _generation) return false;
            Message = $"{status.Count(f => f.State == FileReviewState.Reviewed)}/{status.Count} files reviewed on this layer.";
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Message = ex.Message; return false; }
        finally { IsBusy = false; _loadingSelection = false; }
    }
    [RelayCommand(CanExecute = nameof(CanMarkReviewed))] private async Task MarkReviewedAsync() => await SetReviewedAsync(false);
    [RelayCommand(CanExecute = nameof(CanUseCheckpoint))] private async Task UnmarkReviewedAsync() => await SetReviewedAsync(true);
    private async Task SetReviewedAsync(bool remove)
    {
        var file = SelectedFile; var current = _current;
        if (file is null || current is null || SelectedNode?.Id != current.ChangeId || (!remove && !CanMarkReviewed)) return;
        try
        {
            (await _store.SetCheckpointAsync(new(current.ChangeId, file.Change.NewPath, current.Id, DateTimeOffset.UtcNow), remove, _lifetime.Token)).OrThrow();
            await RefreshFileReviewStatusesAsync();
            if (CheckpointChanged is not null) await CheckpointChanged();
        }
        catch (Exception ex) { Message = ex.Message; }
    }
    internal async Task RefreshFileReviewStatusesAsync()
    {
        if (_disposed || _current is not { } current) return;
        var generation = _generation;
        var statuses = await _progress.StatusAsync(_repository, Graph.Scope, current, Files.Select(file => file.Change).ToArray(), _lifetime.Token);
        if (_disposed || generation != _generation) return;
        var selected = SelectedFile?.Change;
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        try
        {
            Files.Clear();
            foreach (var status in statuses) Files.Add(status);
            SelectedFile = Files.FirstOrDefault(file => file.Change == selected);
            Message = $"{statuses.Count(f => f.State == FileReviewState.Reviewed)}/{statuses.Count} files reviewed on this layer.";
        }
        finally { _loadingSelection = wasLoading; }
    }
    [RelayCommand(CanExecute = nameof(CanShowCurrent))] private Task ShowCurrentAsync() => ShowVersionAsync(_current);
    [RelayCommand(CanExecute = nameof(CanShowPrevious))] private async Task ShowPreviousAsync() => await ShowVersionAsync(PreviousVersion);
    private async Task ShowVersionAsync(ChangeVersion? version)
    {
        var selected = SelectedFile ?? Files.FirstOrDefault();
        if (version?.MergeBase is null || selected is null) { Message = "Select an available version and file."; return; }
        var generation = _generation;
        await ExecuteAsync(async () =>
        {
            var files = version == _current ? Files.Select(file => file.Change).ToArray()
                : (await _git.FilesAsync(_root, version.MergeBase, version.HeadSha, _lifetime.Token)).OrThrow();
            if (_disposed || generation != _generation) return;
            var file = files.FirstOrDefault(f => f.NewPath == selected.Change.NewPath || f.OldPath == selected.Change.OldPath);
            Preview = file is null ? "No file change in this version." : "";
            await ShowDocumentAsync(new(DiffDocumentMode.Current, [new(version.Label, _root, version.MergeBase, version.HeadSha, files)], "Original layer diff"), file?.NewPath, version.Id);
            if (generation == _generation && !_disposed) Message = version.Label + " · original layer diff · read-only";
        });
    }
    [RelayCommand(CanExecute = nameof(CanCompareVersions))] private Task CompareVersionsAsync() => CompareVersionDocumentAsync(DiffDocumentMode.BetweenVersions);
    private async Task CompareVersionDocumentAsync(DiffDocumentMode mode)
    {
        if (_current is null || PreviousVersion is null) { Message = "No earlier observed version. Open this layer again after it changes."; return; }
        var current = _current; var previous = PreviousVersion; var file = SelectedFile;
        var generation = _generation;
        await ExecuteAsync(async () =>
        {
            var comparison = await _comparisons.CompareAsync(_repository, Graph.Scope, previous, current, _lifetime.Token);
            if (_disposed || generation != _generation) return;
            Message = comparison.Unavailable ?? $"Textual comparison: {comparison.CodeChanges.Count} files changed beyond replay; {comparison.BaseChanges.Count} base files changed. This is not semantic equivalence.";
            Preview = "Base changes:\n" + string.Join("\n", comparison.BaseChanges.Select(f => f.DisplayPath)) + "\n\nOwn changes after replay:\n" + string.Join("\n", comparison.CodeChanges.Select(f => f.DisplayPath));
            var changed = comparison.CodeChanges.FirstOrDefault(f => f.NewPath == file?.Change.NewPath);
            var sections = new List<DiffDocumentSection>();
            if (comparison.IsReliable)
            {
                if (comparison.BaseChanges.Count > 0) sections.Add(new("Base changes", _root, previous.MergeBase!, current.MergeBase!, comparison.BaseChanges));
                if (comparison.CodeChanges.Count > 0) sections.Add(new("Own changes after replay", _root, comparison.ExpectedTree!, current.HeadSha, comparison.CodeChanges));
            }
            await ShowDocumentAsync(new(mode, sections, Message), changed?.NewPath, previous.Id,
                comparison.IsReliable && changed is not null ? sections.Count - 1 : null);
        });
    }
    [RelayCommand(CanExecute = nameof(CanUseCheckpoint))]
    private async Task SinceReviewedAsync()
    {
        if (SelectedFile?.CheckedVersion is not { } version) { Message = "This file has no review checkpoint."; return; }
        PreviousVersion = version; await CompareVersionDocumentAsync(DiffDocumentMode.SinceReviewed);
    }
    [RelayCommand]
    private async Task AnalyzeAsync() => await ExecuteAsync(async () =>
    {
        ClearDocument();
        var result = await new StackAnalysisService(_store, _objects, _git).AnalyzeAsync(_repository, Graph, _lifetime.Token);
        Preview = string.Join("\n\n", result.Issues.Select(i => $"{Graph.Nodes.First(n => n.Id == i.ChangeId).Title} · {i.Kind}\n{i.Message}\n{string.Join("\n", i.Paths)}"));
        Message = result.Issues.Count == 0 ? "No issues found in the captured edges. This does not prove semantic correctness." : $"{result.Issues.Count} observations for this graph snapshot.";
    });
    [RelayCommand]
    private async Task AttentionAsync() => await ExecuteAsync(async () =>
    {
        ClearDocument();
        Attention.Clear();
        foreach (var (node, version) in _path.DistinctBy(p => p.Node.Id))
        {
            var files = (await _git.FilesAsync(_root, version.MergeBase!, version.HeadSha, _lifetime.Token)).OrThrow();
            var statuses = await _progress.StatusAsync(_repository, Graph.Scope, version, files, _lifetime.Token);
            foreach (var status in statuses.Where(f => f.State != FileReviewState.Reviewed || f.Concern is not null)) Attention.Add(new(node.Id, status.Change.NewPath, node.Title + " · " + status.Label, status.Concern is not null));
            foreach (var concern in (await _store.ConcernsAsync(node.Id, _lifetime.Token)).OrThrow().Where(c => !statuses.Any(s => s.Change.NewPath == c.Path)))
                Attention.Add(new(node.Id, concern.Path, node.Title + " · " + concern.Path + " · BLOCKER on former file: " + concern.Text, true));
        }
        Preview = string.Join("\n", Attention); Message = $"{Attention.Count} files require attention, in dependency order.";
    });
    [RelayCommand(CanExecute = nameof(CanReviewFile))]
    private async Task EvolutionAsync()
    {
        if (SelectedFile is not { } selected) return;
        var generation = _generation;
        await ExecuteAsync(async () =>
        {
            var evolution = await new FileEvolutionService(_git).BuildAsync(_root, _path, selected.Change.NewPath, _lifetime.Token);
            var text = new System.Text.StringBuilder(); var sections = new List<DiffDocumentSection>();
            foreach (var step in evolution.Steps)
            {
                text.AppendLine($"{step.Change.Title} · {step.Version.Label}" + (step.Continuous ? "" : " · ancestry gap — independent diff"));
                if (step.File is { } file) sections.Add(new(step.Change.Title, _root, step.Version.MergeBase!, step.Version.HeadSha, [file]));
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
            if (_disposed || generation != _generation) return;
            await ShowDocumentAsync(new(DiffDocumentMode.FileEvolution, sections, "File evolution"), selected.Change.NewPath);
            if (_disposed || generation != _generation) return;
            Preview = text.ToString(); Message = "File evolution on the selected stack path. Comments stay in their original PR.";
        });
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); ClearDocument(); ContentLease?.Dispose(); ContentLease = null; Preview = ""; }
}
