using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class RenderedDiffLine(DiffLine line) : ObservableObject
{
    public DiffLine Source { get; } = line;
    public DiffLineKind Kind => Source.Kind;
    public int? OldLineNumber => Source.OldLineNumber;
    public int? NewLineNumber => Source.NewLineNumber;
    public bool IsCode => Kind is DiffLineKind.Context or DiffLineKind.Added or DiffLineKind.Removed;
    public string Marker => Kind == DiffLineKind.Added ? "+" : Kind == DiffLineKind.Removed ? "−" : "";
    public string Text => IsCode && Source.Text.Length > 0 ? Source.Text[1..] : Source.Text;
    [ObservableProperty] private ReviewViewModel? _editor;
    public ObservableCollection<ReviewThread> InlineThreads { get; } = [];
    public ObservableCollection<DraftComment> Drafts { get; } = [];
    public bool HasEditor => Editor is not null;
    partial void OnEditorChanged(ReviewViewModel? value) => OnPropertyChanged(nameof(HasEditor));
    [ObservableProperty] private IReadOnlyList<SyntaxSpan> _tokens = [];
    [ObservableProperty] private TextWrapping _textWrapping = TextWrapping.NoWrap;
}
public sealed partial class DiffSectionViewModel : ObservableObject, IDisposable
{
    private readonly IGitRepositoryReader _git;
    private readonly string _root;
    private readonly ISyntaxHighlighter? _highlighter;
    private CancellationTokenSource? _load;
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
    public string ReviewAction => IsPrSnapshot ? "Comments & review" : "Open PR changes";
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
    [ObservableProperty] private bool _hasLoadError;
    [ObservableProperty] private bool _showSectionHeader;
    public ReviewViewModel? Review { get; set; }
    public bool IsContentVisible => !ShowSectionHeader || IsExpanded;
    partial void OnShowSectionHeaderChanged(bool value) => OnPropertyChanged(nameof(IsContentVisible));
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsContentVisible));
    public ObservableCollection<ReviewThread> FileThreads { get; } = [];
    [ObservableProperty] private string _patch = "";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _wrapCode;
    public ScrollBarVisibility CodeHorizontalScroll => WrapCode ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    partial void OnWrapCodeChanged(bool value)
    { foreach (var line in Lines) line.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap; OnPropertyChanged(nameof(CodeHorizontalScroll)); }
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
        Lines.Clear(); Patch = ""; HasLoadError = false; IsBusy = file is not null;
        Message = file is null ? "Select a file to view changes." : "Loading diff…";
        try
        {
            if (file is null || _disposed) return;
            var diff = await _git.PatchAsync(_root, Result.BaseSha, Result.HeadSha, file, load.Token);
            if (load.IsCancellationRequested || _disposed) return;
            Patch = diff.Patch;
            foreach (var hunk in diff.Hunks)
            {
                Lines.Add(new(new(DiffLineKind.Header, null, null, $"Lines {hunk.OldStart}–{hunk.OldStart + Math.Max(0, hunk.OldCount - 1)} → {hunk.NewStart}–{hunk.NewStart + Math.Max(0, hunk.NewCount - 1)}  {hunk.Context}")) { TextWrapping = WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap });
                foreach (var line in hunk.Lines)
                    if (line.Kind != DiffLineKind.Notice || line.Text.StartsWith("\\ No newline", StringComparison.Ordinal))
                        Lines.Add(new(line.Kind == DiffLineKind.Notice ? line with { Text = "No newline at end of file" } : line) { TextWrapping = WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap });
            }
            Message = file.IsBinary ? "Binary file — text preview unavailable." : file.IsSubmodule ? "Submodule commit changed." : file.ModeChanged ? file.Detail : diff.Lines.Count == 0 ? "No textual changes." : "";
            IsBusy = false; ApplyThreads();
            if (!file.IsBinary && !file.IsSubmodule) await HighlightAsync(file, load.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!load.IsCancellationRequested && !_disposed) { HasLoadError = true; Message = ex.Message; } }
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
                if (number is not null && source.TryGetValue(number.Value, out var spans)) line.Tokens = spans;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Syntax is optional; a readable patch remains on screen. */ }
    }
    public void SetThreads(IEnumerable<ReviewThread> threads) { ReplaceIfChanged(Threads, threads.ToArray()); ApplyThreads(); }
    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    { if (target.SequenceEqual(values)) return; target.Clear(); foreach (var value in values) target.Add(value); }
    public void ApplyThreads()
    {
        var path = SelectedFile?.NewPath;
        ReplaceIfChanged(FileThreads, Threads.Where(t => t.IsFileLevel && !t.IsOutdated && t.Path == path).ToArray());
        var threads = Threads.Where(t => !t.IsOutdated && t.Anchor is { } a && a.HeadSha == Result.HeadSha && a.Path == path)
            .ToLookup(t => (t.Anchor!.Side, t.Anchor.Line));
        var drafts = (Review?.DraftComments.AsEnumerable() ?? []).Where(d => d.Anchor.Path == path && d.Anchor.HeadSha == Result.HeadSha)
            .ToLookup(d => (d.Anchor.Side, d.Anchor.Line));
        foreach (var line in Lines)
        {
            var left = ("LEFT", line.OldLineNumber ?? -1); var right = ("RIGHT", line.NewLineNumber ?? -1);
            ReplaceIfChanged(line.InlineThreads, threads[left].Concat(threads[right]).DistinctBy(t => t.Id).ToArray());
            ReplaceIfChanged(line.Drafts, drafts[left].Concat(drafts[right]).ToArray());
        }
    }
    public void Dispose() { Review?.Detach(this); _disposed = true; _load?.Cancel(); }
}
