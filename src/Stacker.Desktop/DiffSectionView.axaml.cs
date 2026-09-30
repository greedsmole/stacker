// Purpose: Code-behind for one comparison section; coordinates virtualized diff scrolling, line selection, and review-anchor gestures.
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;
public partial class DiffSectionView : UserControl
{
    private ScrollViewer? _scroll;
    private bool _restoring;
    private DiffSectionViewModel? _section;
    public DiffSectionView()
    {
        InitializeComponent();
        DiffLines.AddHandler(PointerPressedEvent, AddLinePointerPressed, RoutingStrategies.Tunnel);
        DiffLines.SelectionChanged += (_, _) => UpdateRangeSelection();
        LayoutUpdated += (_, _) => UpdateRangeButton();
        ContentGrid.ColumnDefinitions[0].MinWidth = 200;
        ContentGrid.ColumnDefinitions[0].MaxWidth = 600;
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is MainWindow window && window.DataContext is MainViewModel vm)
                ContentGrid.ColumnDefinitions[0].Width = new(vm.Settings.FilesWidth);
        };
        Loaded += (_, _) =>
        {
            _section = DataContext as DiffSectionViewModel;
            if (_section is not null) _section.PropertyChanged += SectionChanged;
            RestoreScroll();
        };
        Unloaded += (_, _) =>
        {
            if (_section is not null) _section.PropertyChanged -= SectionChanged;
            if (_scroll is not null) _scroll.ScrollChanged -= RememberScroll;
            _section = null; _scroll = null;
        };
        ContentGrid.SizeChanged += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is MainWindow window && window.DataContext is MainViewModel vm && ContentGrid.ColumnDefinitions[0].ActualWidth >= 150)
                vm.Settings.FilesWidth = ContentGrid.ColumnDefinitions[0].ActualWidth;
        };
    }
    private void SectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(DiffSectionViewModel.IsBusy) && _section?.IsBusy == false) RestoreScroll(); }
    private void RestoreScroll() => Dispatcher.UIThread.Post(() =>
    {
        if (_section is null || _section.IsBusy) return;
        if (_scroll is not null) _scroll.ScrollChanged -= RememberScroll;
        _scroll = DiffLines.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is null) return;
        _restoring = true; _scroll.Offset = new Vector(_section.ScrollX, _section.ScrollY); _restoring = false;
        _scroll.ScrollChanged += RememberScroll;
    }, DispatcherPriority.Loaded);
    private void RememberScroll(object? sender, ScrollChangedEventArgs e)
    {
        UpdateRangeButton();
        if (!_restoring && _section is { IsBusy: false, IsExpanded: true } && _scroll is not null && e.OffsetDelta != Vector.Zero)
        { _section.ScrollX = _scroll.Offset.X; _section.ScrollY = _scroll.Offset.Y; }
    }
    private readonly HashSet<RenderedDiffLine> _rangeRows = [];
    private void UpdateRangeSelection()
    {
        foreach (var row in _rangeRows) row.IsRangeSelected = false;
        _rangeRows.Clear();
        var selected = DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToArray() ?? [];
        if (selected.Length > 1 && DataContext is DiffSectionViewModel { IsPrSnapshot: true, AssociatedPr: { } pr, SelectedFile: { } file } &&
            ReviewSelection.TryCreateAnchor(pr, file.NewPath, selected.Select(l => l.Source).ToArray(), null, out var anchor, out _))
        {
            foreach (var row in selected) { row.IsRangeSelected = true; _rangeRows.Add(row); }
            ToolTip.SetTip(SelectedRangeButton, $"Comment on {anchor!.Side.ToLowerInvariant()} lines {anchor.StartLine}–{anchor.Line}");
        }
        UpdateRangeButton();
    }
    /// <summary>Spans the realized selection rows while retaining the virtualized diff list and wrapped row heights.</summary>
    private void UpdateRangeButton()
    {
        if (_rangeRows.Count == 0) { SelectedRangeButton.IsVisible = false; return; }
        var bounds = DiffLines.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(item => item.DataContext is RenderedDiffLine row && _rangeRows.Contains(row))
            .Select(item => item.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "CodeRow"))
            .OfType<Grid>().Select(grid => (Point: grid.TranslatePoint(default, DiffViewport), Height: grid.Bounds.Height))
            .Where(row => row.Point.HasValue && row.Point.Value.Y + row.Height > 0 && row.Point.Value.Y < DiffViewport.Bounds.Height).ToArray();
        if (bounds.Length == 0) { SelectedRangeButton.IsVisible = false; return; }
        var top = Math.Max(0, bounds.Min(row => row.Point!.Value.Y));
        var bottom = Math.Min(DiffViewport.Bounds.Height, bounds.Max(row => row.Point!.Value.Y + row.Height));
        Canvas.SetLeft(SelectedRangeButton, Math.Max(0, bounds[0].Point!.Value.X));
        Canvas.SetTop(SelectedRangeButton, top);
        SelectedRangeButton.Height = Math.Max(0, bottom - top);
        SelectedRangeButton.IsVisible = bottom > top;
    }
    private int? _rangeStart;
    private bool _extendRange;
    private RenderedDiffLine? _commentLine;
    private RenderedDiffLine[]? _commentSelection;
    private void AddLinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _commentLine = null; _commentSelection = null; _extendRange = false;
        if (e.Source is not Control source) return;
        var button = source as Button ?? source.FindAncestorOfType<Button>();
        if (button is not { DataContext: RenderedDiffLine line } || !button.Classes.Contains("line-add")) return;
        // Capture before button focus or ListBox pointer handling can alter the Shift-selected rows.
        _commentLine = line;
        _commentSelection = DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToArray();
        _extendRange = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    }
    private async void DiffKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        if (DiffLines.GetVisualDescendants().OfType<SyntaxText>().Any(t => !string.IsNullOrEmpty(t.SelectedText))) return;
        var lines = DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToHashSet();
        if (DataContext is not DiffSectionViewModel section || lines is null || lines.Count == 0) return;
        try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) { await clipboard.SetTextAsync(string.Join("\n", section.Lines.Where(lines.Contains).Select(l => l.Text))); e.Handled = true; } }
        catch (Exception ex) { section.Message = "Cannot copy code: " + ex.Message; }
    }
    public void ScrollTo(ReviewAnchor? anchor)
    {
        if (anchor is null || DataContext is not DiffSectionViewModel section) return;
        var line = section.Lines.FirstOrDefault(l => anchor.Side == "LEFT" ? l.Kind == DiffLineKind.Removed && l.OldLineNumber == anchor.Line : l.Kind != DiffLineKind.Removed && l.NewLineNumber == anchor.Line);
        if (line is not null) DiffLines.ScrollIntoView(line);
    }
    public void ScrollTo(DiffLine source)
    {
        if (DataContext is not DiffSectionViewModel section) return;
        var line = section.Lines.FirstOrDefault(row => row.Kind == source.Kind && row.OldLineNumber == source.OldLineNumber && row.NewLineNumber == source.NewLineNumber);
        if (line is not null) DiffLines.ScrollIntoView(line);
    }
    private async void ReviewClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffSectionViewModel section || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        if (!section.IsPrSnapshot) { await main.OpenPrChangesAsync(section); return; }
        await main.ShowReviewAsync(section);
    }
    private async void FileCommentClick(object? sender, RoutedEventArgs e)
    { if (DataContext is DiffSectionViewModel section && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main) await main.ShowReviewAsync(section, 1); }
    private async void WrapCodeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        await main.SetWrapCodeAsync(toggle.IsChecked == true);
    }
    private async void CommentSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffSectionViewModel section || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        var selected = DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToHashSet() ?? [];
        if (selected.Count == 0) { section.Message = "Select adjacent code rows with Shift-click, then comment on the selection."; return; }
        var session = await main.EnsureReviewAsync(section);
        if (session is null || session.IsBusy) return;
        session.SetSelection(section.Lines.Where(selected.Contains));
        if (!session.HasSelection) { section.Message = session.Message; return; }
        foreach (var row in section.Lines) row.Editor = null;
        var last = section.Lines.Last(selected.Contains); last.Editor = session; main.ActiveReview = session;
        DiffLines.ScrollIntoView(last);
    }
    private async void AddLineClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RenderedDiffLine line }) return;
        var selected = ReferenceEquals(_commentLine, line) ? _commentSelection
            : DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToArray();
        var range = _extendRange;
        _commentLine = null; _commentSelection = null; _extendRange = false;
        // A plus inside the selected range comments on that range. A plus outside it starts a new single-line comment.
        await BeginLineAsync(line, range, selectedRows: selected is { Length: > 1 } && selected.Contains(line) ? selected : null);
    }
    private async void LineNumberPressed(object? sender, PointerPressedEventArgs e)
    { if (sender is Control { DataContext: RenderedDiffLine line }) { e.Handled = true; await BeginLineAsync(line, e.KeyModifiers.HasFlag(KeyModifiers.Shift), Grid.GetColumn((Control)sender) == 1 ? "LEFT" : "RIGHT"); } }
    private async Task BeginLineAsync(RenderedDiffLine line, bool range, string? side = null, RenderedDiffLine[]? selectedRows = null)
    {
        if (DataContext is not DiffSectionViewModel section || !line.IsCode || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        if (!section.IsPrSnapshot) { section.Message = section.HasPr ? "Review the PR snapshot to comment on code. Use Review PR in the file menu." : "Select a GitHub PR layer to comment on code."; return; }
        var session = await main.EnsureReviewAsync(section); if (session is null || session.IsBusy) return;
        var end = section.Lines.IndexOf(line);
        var start = range && _rangeStart is { } first ? first : end; _rangeStart = start;
        var selected = selectedRows ?? section.Lines.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1).ToArray();
        session.SetSelection(selected, side);
        if (!session.HasSelection) { section.Message = session.Message; return; }
        foreach (var row in section.Lines) row.Editor = null;
        line.Editor = session; main.ActiveReview = session;
        DiffLines.ScrollIntoView(line);
    }
    private void CloseInlineClick(object? sender, RoutedEventArgs e)
    { if (DataContext is DiffSectionViewModel section) foreach (var line in section.Lines) line.Editor = null; }
    private async void EditInlineDraftClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DraftComment draft } || DataContext is not DiffSectionViewModel section || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        await main.ShowReviewAsync(section); main.ActiveReview?.EditDraftCommentCommand.Execute(draft);
    }
    private async void RetryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiffSectionViewModel vm) await vm.LoadAsync(vm.SelectedFile);
    }
    private async void CopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffSectionViewModel vm) return;
        try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(vm.Patch); }
        catch (Exception ex) { vm.Message = "Cannot copy patch: " + ex.Message; }
    }
}
