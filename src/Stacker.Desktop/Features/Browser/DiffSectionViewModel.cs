// Purpose: State and asynchronous loading for one diff section, including file filtering, source highlighting, review threads, and preserved view state.
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
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsHunk => Kind == DiffLineKind.Header;
    public int? OldLineNumber => Source.OldLineNumber;
    public int? NewLineNumber => Source.NewLineNumber;
    public bool IsCode => Kind is DiffLineKind.Context or DiffLineKind.Added or DiffLineKind.Removed;
    public bool IsExpandedContext { get; init; }
    public bool CanComment => IsCode && AllowComment && !IsExpandedContext;
    public bool CanCommentLeft => IsRemoved && CanComment;
    public bool IsLineCommentVisible => CanComment && !IsRangeSelected;
    public bool AllowComment { get; init; } = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCollapsedContext), nameof(ContextLabel), nameof(ShowExpandUp))]
    private DiffContextGap? _contextGap;
    public bool IsCollapsedContext => ContextGap is not null;
    public bool ShowExpandUp => ContextGap is { HasHunkAbove: true, HasHunkBelow: true, Count: > DiffContextGap.ExpandStep };
    public string? ContextLabel => ContextGap switch
    {
        null => null,
        { Count: 1 } => "Show 1 unchanged line",
        { Count: <= DiffContextGap.ExpandStep } gap => $"Show {gap.Count} unchanged lines",
        { RevealFromEnd: true } gap => $"Show {gap.RevealCount} lines above · {gap.Count} hidden",
        { } gap => $"Show {gap.RevealCount} lines below · {gap.Count} hidden"
    };
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLineCommentVisible))] private bool _isRangeSelected;
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
public sealed partial class DiffFileRow(FileChange change) : ObservableObject
{
    public FileChange Change { get; } = change;
    public string AccessibleName => $"{Change.DisplayPath} · {Change.Kind}";
    public bool IsReviewed => ReviewStatus?.State == FileReviewState.Reviewed;
    public bool HasReviewStatus => ReviewStatus is not null;
    public bool CanChangeReview => HasReviewStatus && !SavingReview;
    public string ReviewAction => ReviewStatus?.State switch
    {
        FileReviewState.Reviewed => "Mark unreviewed",
        FileReviewState.CodeChanged => "Code changed since review. Mark reviewed",
        FileReviewState.BaseChanged => "Base changed since review. Mark reviewed",
        FileReviewState.MetadataChanged => "File metadata changed since review. Mark reviewed",
        FileReviewState.Unavailable => "Previous review could not be compared. Mark reviewed",
        _ => "Mark reviewed"
    };

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsReviewed), nameof(HasReviewStatus), nameof(CanChangeReview), nameof(ReviewAction))]
    private FileReviewStatus? _reviewStatus;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChangeReview))]
    private bool _savingReview;
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
    public IDisposable? ContentLease { get; set; }
    public string Root => _root;
    public DiffResult Result { get; }
    public PullRequest? AssociatedPr { get; set; }
    public GitHubRepositoryContext? GitHubContext { get; set; }
    public bool IsReadOnlyComparison { get; init; }
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
    internal Task SelectedFileLoad { get; private set; } = Task.CompletedTask;
    private readonly Dictionary<FileChange, DiffFileRow> _fileRows;
    public IReadOnlyList<FileChange> Files { get; private set; } = [];
    public BatchCollection<DiffFileRow> VisibleFiles { get; } = [];
    internal ChangeVersion? ReviewVersion { get; set; }
    internal string? ReviewScope { get; set; }
    public BatchCollection<RenderedDiffLine> Lines { get; } = [];
    public ObservableCollection<ReviewThread> Threads { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedFileRow))] private FileChange? _selectedFile;
    public DiffFileRow? SelectedFileRow
    {
        get => SelectedFile is { } file ? _fileRows.GetValueOrDefault(file) : null;
        set { if (!_filtering) SelectedFile = value?.Change; }
    }
    [ObservableProperty] private string _filter = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowMessage))] private string _message = "Select a file to view changes.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowMessage))] private bool _hasLoadError;
    public bool ShowMessage => Message.Length > 0 || HasLoadError;
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
    public DiffSectionViewModel(IGitRepositoryReader git, string root, DiffResult result, ISyntaxHighlighter? highlighter = null,
        string theme = "Dark", bool readOnly = false, bool selectFirstFile = true)
    {
        IsReadOnlyComparison = readOnly; _git = git; _root = root; Result = result; _highlighter = highlighter; Theme = theme;
        _fileRows = result.Files.Distinct().ToDictionary(file => file, file => new DiffFileRow(file));
        UpdateFilter();
        if (Files.Count == 0) Message = "No changes in this comparison.";
        else if (selectFirstFile) SelectedFile = Files[0];
    }
    public void BindPr(PullRequest? pr, GitHubRepositoryContext? context)
    {
        AssociatedPr = pr; GitHubContext = context;
        OnPropertyChanged(nameof(Comparison)); OnPropertyChanged(nameof(HasPr)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(ReviewAction));
    }
    /// <summary>Transfers transient browsing state when identical comparison data is refreshed.</summary>
    /// <remarks>The selected file is matched by path so a refreshed snapshot can replace file objects safely.</remarks>
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
        Files = Result.Files.Where(f => f.DisplayPath.Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        VisibleFiles.ReplaceAll(Files.Select(file => _fileRows[file]));
        SelectedFile = selected is not null && Files.Contains(selected) ? selected : null;
        if (GroupFilesByFolder) RebuildFolderFiles();
        OnPropertyChanged(nameof(FileCountLabel));
        OnPropertyChanged(nameof(HasNoFileMatches));
        _filtering = false;
        if (SelectedFile == selected) OnPropertyChanged(nameof(SelectedFileRow));
        if (SelectedFile is null && selected is not null) _ = LoadAsync(null);
    }
    internal void SetReviewStatuses(IReadOnlyList<FileReviewStatus> statuses)
    {
        if (_disposed) return;
        foreach (var status in statuses)
            if (_fileRows.TryGetValue(status.Change, out var row)) row.ReviewStatus = status;
    }
    partial void OnSelectedFileChanged(FileChange? value)
    {
        if (_filtering) return;
        UpdateFolderSelection();
        ScrollX = ScrollY = 0;
        SelectedFileLoad = LoadAsync(value);
    }
    /// <summary>Loads one file's patch on demand and cancels any request for the previously selected file.</summary>
    /// <remarks>Results are applied only while this request remains current, preventing stale selections from flashing.</remarks>
    public async Task LoadAsync(FileChange? file)
    {
        _load?.Cancel(); var load = new CancellationTokenSource(); _load = load;
        ResetContext();
        Lines.Clear(); Patch = ""; HasLoadError = false; IsBusy = file is not null;
        Message = file is null ? "Select a file to view changes." : "Loading diff…";
        try
        {
            if (file is null || _disposed) return;
            var diff = (await _git.PatchAsync(_root, Result.BaseSha, Result.HeadSha, file, load.Token)).OrThrow();
            if (load.IsCancellationRequested || _disposed) return;
            Patch = diff.Patch;
            var readContext = _git is IBlobReader && DiffContext.CanRead(file);
            var highlight = _highlighter is not null && !file.IsBinary && !file.IsSubmodule;
            var head = readContext || (highlight && diff.Hunks.Any(hunk => hunk.NewCount > 0))
                ? await ReadSourceBlobAsync(Result.HeadSha, file.NewPath, load.Token)
                : null;
            var wrapping = WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap;
            var prepared = await Task.Run(() =>
            {
                var presentation = DiffView.From(diff); var rows = new RenderedDiffLine[presentation.Rows.Count];
                for (var i = 0; i < rows.Length; i++) { if (i % 1024 == 0) load.Token.ThrowIfCancellationRequested(); rows[i] = new(presentation.Rows[i]) { AllowComment = !IsReadOnlyComparison, TextWrapping = wrapping }; }
                var context = readContext && head is not null ? DiffContext.Create(diff, presentation, head.Text, load.Token) : null;
                return (presentation, rows, context);
            }, load.Token);
            if (load.IsCancellationRequested || _disposed) return;
            var view = prepared.presentation;
            if (wrapping != (WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap)) foreach (var row in prepared.rows) row.TextWrapping = WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap;
            SetContext(prepared.context, prepared.rows, readContext && head is null);
            Message = Lines.Count == 0 ? view.EmptyReason : "";
            IsBusy = false; ApplyThreads();
            if (highlight) await HighlightAsync(file, head, load.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!load.IsCancellationRequested && !_disposed) { HasLoadError = true; Message = ex.Message; } }
        finally { if (ReferenceEquals(_load, load)) { _load = null; IsBusy = false; } load.Dispose(); }
    }
    // Old and new blobs are tokenized separately so multiline grammar state follows each side's actual source text.
    private async Task HighlightAsync(FileChange file, BlobContent? head, CancellationToken ct)
    {
        if (_highlighter is null) return;
        var old = Lines.Any(line => line.IsRemoved) ? await ReadSourceBlobAsync(Result.BaseSha, file.OldPath, ct) : null;
        var oldTokens = await ReadTokensAsync(old, file.OldPath);
        var newTokens = await ReadTokensAsync(head, file.NewPath);
        ct.ThrowIfCancellationRequested();
        _contextTokens = _context is null ? null : newTokens;
        foreach (var line in Lines)
        {
            var number = line.IsRemoved ? line.OldLineNumber : line.NewLineNumber;
            var source = line.IsRemoved ? oldTokens : newTokens;
            if (number is int index && source?.TryGetValue(index, out var spans) == true) line.Tokens = spans;
        }

        async Task<IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>?> ReadTokensAsync(BlobContent? content, string path)
        {
            if (content is null) return null;
            try
            {
                var tokens = await _highlighter.HighlightAsync(content.Id, path, content.Text, Theme, ct);
                return tokens.IsFailed ? null : tokens.Value;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
    }
    private async Task<BlobContent?> ReadSourceBlobAsync(string commit, string path, CancellationToken ct)
    {
        if (_git is not IBlobReader reader) return null;
        try
        {
            var result = await reader.ReadBlobAsync(_root, commit, path, ct);
            return result.IsFailed ? null : result.Value;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
    public void SetThreads(IEnumerable<ReviewThread> threads) { ReplaceIfChanged(Threads, threads.ToArray()); ApplyThreads(); }
    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    { if (target.SequenceEqual(values)) return; target.Clear(); foreach (var value in values) target.Add(value); }
    /// <summary>Attaches only current-PR comments whose path, side, and line match the displayed file lines.</summary>
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
            if (line.InlineThreads.Count > 0 || threads.Contains(left) || threads.Contains(right)) ReplaceIfChanged(line.InlineThreads, threads[left].Concat(threads[right]).DistinctBy(t => t.Id).ToArray());
            if (line.Drafts.Count > 0 || drafts.Contains(left) || drafts.Contains(right)) ReplaceIfChanged(line.Drafts, drafts[left].Concat(drafts[right]).ToArray());
        }
    }
    public void Dispose()
    {
        Review?.Detach(this); _disposed = true; ContentLease?.Dispose(); ContentLease = null; _load?.Cancel();
        ResetContext();
        // An evicted comparison must not keep a patch, its rows or their token tables alive.
        Lines.Clear(); Threads.Clear(); FileThreads.Clear(); Patch = "";
    }
}
