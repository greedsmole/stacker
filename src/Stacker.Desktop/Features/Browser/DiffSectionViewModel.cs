// Purpose: State and asynchronous loading for one diff section, including file filtering, source highlighting, review threads, and preserved view state.
using System.Collections.ObjectModel;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class DiffSectionViewModel : ObservableObject, IDisposable
{
    private readonly IGitRepositoryReader _git;
    private readonly string _root;
    private readonly ISyntaxHighlighter? _highlighter;
    private CancellationTokenSource? _load;
    private bool _disposed;
    private bool _filtering;
    public string Theme
    {
        get; set;
    }
    public IDisposable? ContentLease
    {
        get; set;
    }
    public string Root => _root;
    public DiffResult Result
    {
        get;
    }
    public PullRequest? AssociatedPr
    {
        get; set;
    }
    public GitHubRepositoryContext? GitHubContext
    {
        get; set;
    }
    public bool IsReadOnlyComparison
    {
        get; init;
    }
    public bool IsPrSnapshot
    {
        get; set;
    }
    public bool IsDemo
    {
        get; set;
    }
    public bool HasPr => AssociatedPr is not null;
    public string ReviewAction => IsPrSnapshot ? "Comments & review" : "Open PR changes";
    public string Title => AssociatedPr?.Label ?? Result.Layer.Replace("refs/heads/", "").Replace("refs/remotes/", "");
    public string Comparison => $"{(IsPrSnapshot ? AssociatedPr?.BaseRef : Result.ParentRef.Replace("refs/heads/", ""))} → {Title}   ·   {Result.BaseSha[..8]}…{Result.HeadSha[..8]}";
    public string Summary => $"{Result.Files.Count} files   +{Result.Additions}  −{Result.Deletions}";
    public string? Warning => Result.Warning;
    public bool HasWarning => Warning is not null;
    public double ScrollX
    {
        get; set;
    }
    public double ScrollY
    {
        get; set;
    }
    internal Task SelectedFileLoad { get; private set; } = Task.CompletedTask;
    private readonly Dictionary<FileChange, DiffFileRow> _fileRows;
    public IReadOnlyList<FileChange> Files { get; private set; } = [];
    public BatchCollection<DiffFileRow> VisibleFiles { get; } = [];
    internal ChangeVersion? ReviewVersion
    {
        get; set;
    }
    internal string? ReviewScope
    {
        get; set;
    }
    public BatchCollection<RenderedDiffLine> Lines { get; } = [];
    public ObservableCollection<ReviewThread> Threads { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedFileRow))] private FileChange? _selectedFile;
    public DiffFileRow? SelectedFileRow
    {
        get => SelectedFile is { } file ? _fileRows.GetValueOrDefault(file) : null;
        set
        {
            if (!_filtering)
                SelectedFile = value?.Change;
        }
    }
    [ObservableProperty] private string _filter = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowMessage))] private string _message = "Select a file to view changes.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowMessage))] private bool _hasLoadError;
    public bool ShowMessage => Message.Length > 0 || HasLoadError;
    [ObservableProperty] private bool _showSectionHeader;
    public ReviewViewModel? Review
    {
        get; set;
    }
    public bool IsContentVisible => !ShowSectionHeader || IsExpanded;
    partial void OnShowSectionHeaderChanged(bool value) => OnPropertyChanged(nameof(IsContentVisible));
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsContentVisible));
    public ObservableCollection<ReviewThread> FileThreads { get; } = [];
    [ObservableProperty] private string _patch = "";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _wrapCode;
    public ScrollBarVisibility CodeHorizontalScroll => WrapCode ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    partial void OnWrapCodeChanged(bool value)
    {
        foreach (var line in Lines)
            line.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
        OnPropertyChanged(nameof(CodeHorizontalScroll));
    }
    [ObservableProperty] private double _panelHeight = 560;
    public DiffSectionViewModel(IGitRepositoryReader git, string root, DiffResult result, ISyntaxHighlighter? highlighter = null,
        string theme = "Dark", bool readOnly = false, bool selectFirstFile = true)
    {
        IsReadOnlyComparison = readOnly;
        _git = git;
        _root = root;
        Result = result;
        _highlighter = highlighter;
        Theme = theme;
        _fileRows = result.Files.Distinct().ToDictionary(file => file, file => new DiffFileRow(file));
        UpdateFilter();
        if (Files.Count == 0)
            Message = "No changes in this comparison.";
        else if (selectFirstFile)
            SelectedFile = Files[0];
    }
    public void BindPr(PullRequest? pr, GitHubRepositoryContext? context)
    {
        AssociatedPr = pr;
        GitHubContext = context;
        OnPropertyChanged(nameof(Comparison));
        OnPropertyChanged(nameof(HasPr));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ReviewAction));
    }
    /// <summary>Transfers transient browsing state when identical comparison data is refreshed.</summary>
    /// <remarks>The selected file is matched by path so a refreshed snapshot can replace file objects safely.</remarks>
    public void RestoreView(DiffSectionViewModel old)
    {
        Filter = old.Filter;
        IsExpanded = old.IsExpanded;
        SelectedFile = Files.FirstOrDefault(f => f.NewPath == old.SelectedFile?.NewPath) ?? Files.FirstOrDefault();
        ScrollX = old.ScrollX;
        ScrollY = old.ScrollY;
    }
    partial void OnFilterChanged(string value) => UpdateFilter();
    private void UpdateFilter()
    {
        var selected = SelectedFile;
        _filtering = true;
        Files = Result.Files.Where(f => f.DisplayPath.Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        VisibleFiles.ReplaceAll(Files.Select(file => _fileRows[file]));
        SelectedFile = selected is not null && Files.Contains(selected) ? selected : null;
        if (GroupFilesByFolder)
            RebuildFolderFiles();
        OnPropertyChanged(nameof(FileCountLabel));
        OnPropertyChanged(nameof(HasNoFileMatches));
        _filtering = false;
        if (SelectedFile == selected)
            OnPropertyChanged(nameof(SelectedFileRow));
        if (SelectedFile is null && selected is not null)
            _ = LoadAsync(null);
    }
    internal void SetReviewStatuses(IReadOnlyList<FileReviewStatus> statuses)
    {
        if (_disposed)
            return;
        foreach (var status in statuses)
            if (_fileRows.TryGetValue(status.Change, out var row))
                row.ReviewStatus = status;
    }
    partial void OnSelectedFileChanged(FileChange? value)
    {
        if (_filtering)
            return;
        UpdateFolderSelection();
        ScrollX = ScrollY = 0;
        SelectedFileLoad = LoadAsync(value);
    }
    public void SetThreads(IEnumerable<ReviewThread> threads)
    {
        ReplaceIfChanged(Threads, threads.ToArray());
        ApplyThreads();
    }
    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        if (target.SequenceEqual(values))
            return;
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
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
            var left = ("LEFT", line.OldLineNumber ?? -1);
            var right = ("RIGHT", line.NewLineNumber ?? -1);
            if (line.InlineThreads.Count > 0 || threads.Contains(left) || threads.Contains(right))
                ReplaceIfChanged(line.InlineThreads, threads[left].Concat(threads[right]).DistinctBy(t => t.Id).ToArray());
            if (line.Drafts.Count > 0 || drafts.Contains(left) || drafts.Contains(right))
                ReplaceIfChanged(line.Drafts, drafts[left].Concat(drafts[right]).ToArray());
        }
    }
    public void Dispose()
    {
        Review?.Detach(this);
        _disposed = true;
        ContentLease?.Dispose();
        ContentLease = null;
        _load?.Cancel();
        ResetContext();
        // An evicted comparison must not keep a patch, its rows or their token tables alive.
        Lines.Clear();
        Threads.Clear();
        FileThreads.Clear();
        Patch = "";
    }
}
