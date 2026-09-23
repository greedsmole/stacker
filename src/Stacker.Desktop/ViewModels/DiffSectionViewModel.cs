using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class RenderedDiffLine(DiffLine line) : ObservableObject
{
    public DiffLine Source { get; } = line;
    public DiffLineKind Kind => Source.Kind;
    public int? OldLineNumber => Source.OldLineNumber;
    public int? NewLineNumber => Source.NewLineNumber;
    public string Text => Source.Text;
    [ObservableProperty] private IReadOnlyList<SyntaxSpan> _tokens = [];
    [ObservableProperty] private string _threadText = "";
    public bool HasThread => !string.IsNullOrEmpty(ThreadText);
    partial void OnThreadTextChanged(string value) => OnPropertyChanged(nameof(HasThread));
}
public sealed partial class DiffSectionViewModel : ObservableObject, IDisposable
{
    private readonly IGitRepositoryReader _git;
    private readonly string _root;
    private readonly ISyntaxHighlighter? _highlighter;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _threadLoad;
    private bool _threadsRequested;
    private bool _disposed;
    private bool _filtering;
    public string Theme { get; set; }
    public string Root => _root;
    public DiffResult Result { get; }
    public PullRequest? AssociatedPr { get; set; }
    public GitHubRepositoryContext? GitHubContext { get; set; }
    public bool IsPrSnapshot { get; set; }
    public bool IsDemo { get; set; }
    public bool HasPr => AssociatedPr is not null;
    public string ReviewAction => IsPrSnapshot ? "Review PR" : "Open PR changes";
    public string Title => AssociatedPr?.Label ?? Result.Layer.Replace("refs/heads/", "").Replace("refs/remotes/", "");
    public string Comparison => $"{(IsPrSnapshot ? AssociatedPr?.BaseRef : Result.ParentRef.Replace("refs/heads/", ""))} → {Title}   ·   {Result.BaseSha[..8]}…{Result.HeadSha[..8]}";
    public string Summary => $"{Result.Files.Count} files   +{Result.Additions}  −{Result.Deletions}";
    public string? Warning => Result.Warning;
    public bool HasWarning => Warning is not null;
    public double ScrollX { get; set; }
    public double ScrollY { get; set; }
    public ObservableCollection<FileChange> Files { get; } = [];
    public ObservableCollection<RenderedDiffLine> Lines { get; } = [];
    public ObservableCollection<ReviewThread> Threads { get; } = [];
    [ObservableProperty] private FileChange? _selectedFile;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _message = "Select a file to view changes.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _patch = "";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private double _panelHeight = 560;
    public DiffSectionViewModel(IGitRepositoryReader git, string root, DiffResult result, ISyntaxHighlighter? highlighter = null, string theme = "Dark")
    {
        _git = git; _root = root; Result = result; _highlighter = highlighter; Theme = theme;
        UpdateFilter();
        if (Files.Count == 0) Message = "No changes in this comparison."; else SelectedFile = Files[0];
    }
    public void BindPr(PullRequest? pr, GitHubRepositoryContext? context)
    {
        AssociatedPr = pr; GitHubContext = context;
        OnPropertyChanged(nameof(Comparison)); OnPropertyChanged(nameof(HasPr)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(ReviewAction));
    }
    public void RestoreView(DiffSectionViewModel old)
    {
        Filter = old.Filter; IsExpanded = old.IsExpanded;
        SelectedFile = Files.FirstOrDefault(f => f.NewPath == old.SelectedFile?.NewPath) ?? Files.FirstOrDefault();
        ScrollX = old.ScrollX; ScrollY = old.ScrollY;
    }
    partial void OnFilterChanged(string value) => UpdateFilter();
    private void UpdateFilter()
    {
        var selected = SelectedFile; _filtering = true;
        Files.Clear(); foreach (var file in Result.Files.Where(f => f.DisplayPath.Contains(Filter, StringComparison.OrdinalIgnoreCase))) Files.Add(file);
        SelectedFile = selected is not null && Files.Contains(selected) ? selected : null;
        _filtering = false;
        if (SelectedFile is null && selected is not null) _ = LoadAsync(null);
    }
    partial void OnSelectedFileChanged(FileChange? value) { if (!_filtering) { ScrollX = ScrollY = 0; _ = LoadAsync(value); } }
    public async Task LoadAsync(FileChange? file)
    {
        _load?.Cancel(); var load = new CancellationTokenSource(); _load = load;
        Lines.Clear(); Patch = ""; IsBusy = file is not null;
        Message = file is null ? "Select a file to view changes." : "Loading diff…";
        try
        {
            if (file is null || _disposed) return;
            var diff = await _git.PatchAsync(_root, Result.BaseSha, Result.HeadSha, file, load.Token);
            if (load.IsCancellationRequested || _disposed) return;
            Patch = diff.Patch; foreach (var line in diff.Lines) Lines.Add(new(line));
            Message = file.IsBinary ? "Binary file — text preview unavailable." : file.IsSubmodule ? "Submodule commit changed." : file.ModeChanged ? file.Detail : diff.Lines.Count == 0 ? "No textual changes." : "";
            IsBusy = false; ApplyThreads();
            if (!file.IsBinary && !file.IsSubmodule) await HighlightAsync(file, load.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!load.IsCancellationRequested && !_disposed) Message = ex.Message; }
        finally { if (ReferenceEquals(_load, load)) { _load = null; IsBusy = false; } load.Dispose(); }
    }
    private async Task HighlightAsync(FileChange file, CancellationToken ct)
    {
        if (_highlighter is null || _git is not IBlobReader reader) return;
        try
        {
            var old = await reader.ReadBlobAsync(_root, Result.BaseSha, file.OldPath, ct);
            var current = await reader.ReadBlobAsync(_root, Result.HeadSha, file.NewPath, ct);
            var oldTokens = old is null ? new Dictionary<int, IReadOnlyList<SyntaxSpan>>() : await _highlighter.HighlightAsync(old.Id, file.OldPath, old.Text, Theme, ct);
            var newTokens = current is null ? new Dictionary<int, IReadOnlyList<SyntaxSpan>>() : await _highlighter.HighlightAsync(current.Id, file.NewPath, current.Text, Theme, ct);
            ct.ThrowIfCancellationRequested();
            foreach (var line in Lines)
            {
                var number = line.Kind == DiffLineKind.Removed ? line.OldLineNumber : line.NewLineNumber;
                var source = line.Kind == DiffLineKind.Removed ? oldTokens : newTokens;
                if (number is not null && source.TryGetValue(number.Value, out var spans)) line.Tokens = spans.Select(s => s with { Start = s.Start + 1 }).ToArray();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Syntax is optional; a readable patch remains on screen. */ }
    }
    public async Task LoadThreadsAsync(IGitHubReader reader)
    {
        if (_threadsRequested || !IsPrSnapshot || AssociatedPr is not { } pr || GitHubContext is not { } context) return;
        _threadsRequested = true; var load = new CancellationTokenSource(); _threadLoad = load;
        try
        {
            var discussion = IsDemo ? Stacker.Infrastructure.DemoRepositoryGenerator.Discussion(pr) : await reader.DiscussionAsync(context, pr, load.Token);
            if (!load.IsCancellationRequested && !_disposed) SetThreads(discussion.Threads);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!load.IsCancellationRequested && !_disposed) { Message = "PR comments unavailable: " + ex.Message; _threadsRequested = false; } }
        finally { if (ReferenceEquals(_threadLoad, load)) _threadLoad = null; load.Dispose(); }
    }
    public void SetThreads(IEnumerable<ReviewThread> threads) { Threads.Clear(); foreach (var thread in threads) Threads.Add(thread); ApplyThreads(); }
    private void ApplyThreads()
    {
        foreach (var line in Lines)
            line.ThreadText = string.Join("\n\n", Threads.Where(t => !t.IsOutdated && t.Anchor is { } a && a.HeadSha == Result.HeadSha && a.Path == SelectedFile?.NewPath && (a.Side == "LEFT" ? line.OldLineNumber : line.NewLineNumber) == a.Line).Select(t => t.Text));
    }
    public void Dispose() { _disposed = true; _load?.Cancel(); _threadLoad?.Cancel(); }
}
