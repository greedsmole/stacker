// Purpose: Code-behind for one comparison section; coordinates virtualized diff scrolling, line selection, and review-anchor gestures.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
            if (ShowFileNavigator && TopLevel.GetTopLevel(this) is MainWindow window && window.DataContext is MainViewModel vm)
                ContentGrid.ColumnDefinitions[0].Width = new(vm.Settings.FilesWidth);
        };
        Loaded += (_, _) => AttachSection(DataContext as DiffSectionViewModel);
        DataContextChanged += (_, _) => { if (IsLoaded) AttachSection(DataContext as DiffSectionViewModel); };
        Unloaded += (_, _) => AttachSection(null);
        ContentGrid.SizeChanged += (_, _) =>
        {
            if (ShowFileNavigator && FilePane.IsVisible && TopLevel.GetTopLevel(this) is MainWindow window && window.DataContext is MainViewModel vm && ContentGrid.ColumnDefinitions[0].ActualWidth >= 150)
                vm.Settings.FilesWidth = ContentGrid.ColumnDefinitions[0].ActualWidth;
        };
    }
    private void AttachSection(DiffSectionViewModel? section)
    {
        if (ReferenceEquals(_section, section))
            return;
        _symbolLoad?.Cancel();
        _rangeStart = null;
        if (_section is not null)
            _section.PropertyChanged -= SectionChanged;
        if (_scroll is not null)
            _scroll.ScrollChanged -= RememberScroll;
        _scroll = null;
        _section = section;
        if (section is null)
            return;
        section.PropertyChanged += SectionChanged;
        RestoreScroll();
    }
    private void SectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffSectionViewModel.SelectedFile))
        {
            _symbolLoad?.Cancel();
            _rangeStart = null;
        }
        if (e.PropertyName == nameof(DiffSectionViewModel.IsBusy) && _section?.IsBusy == false)
            RestoreScroll();
    }
    private void RestoreScroll() => Dispatcher.UIThread.Post(() =>
    {
        if (_section is null || _section.IsBusy)
            return;
        if (_scroll is not null)
            _scroll.ScrollChanged -= RememberScroll;
        _scroll = DiffLines.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is null)
            return;
        _restoring = true;
        _scroll.Offset = new Vector(_section.ScrollX, _section.ScrollY);
        _restoring = false;
        _scroll.ScrollChanged += RememberScroll;
    }, DispatcherPriority.Loaded);
    private void RememberScroll(object? sender, ScrollChangedEventArgs e)
    {
        UpdateRangeButton();
        if (!_restoring && _section is { IsBusy: false, IsExpanded: true } && _scroll is not null && e.OffsetDelta != Vector.Zero)
        {
            _section.ScrollX = _scroll.Offset.X;
            _section.ScrollY = _scroll.Offset.Y;
        }
    }
    private readonly HashSet<RenderedDiffLine> _rangeRows = [];
    private void UpdateRangeSelection()
    {
        foreach (var row in _rangeRows)
            row.IsRangeSelected = false;
        _rangeRows.Clear();
        var selected = DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToArray() ?? [];
        if (selected.Length > 1 && selected.All(line => line.CanComment) &&
            DataContext is DiffSectionViewModel { IsPrSnapshot: true, AssociatedPr: { } pr, SelectedFile: { } file } &&
            ReviewSelection.TryCreateAnchor(pr, file.NewPath, selected.Select(l => l.Source).ToArray(), null, out var anchor, out _))
        {
            foreach (var row in selected)
            {
                row.IsRangeSelected = true;
                _rangeRows.Add(row);
            }
            ToolTip.SetTip(SelectedRangeButton, $"Comment on {anchor!.Side.ToLowerInvariant()} lines {anchor.StartLine}–{anchor.Line}");
        }
        UpdateRangeButton();
    }
    /// <summary>Spans the realized selection rows while retaining the virtualized diff list and wrapped row heights.</summary>
    private void UpdateRangeButton()
    {
        if (_rangeRows.Count == 0)
        {
            SelectedRangeButton.IsVisible = false;
            return;
        }
        var bounds = DiffLines.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(item => item.DataContext is RenderedDiffLine row && _rangeRows.Contains(row))
            .Select(item => item.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "CodeRow"))
            .OfType<Grid>().Select(grid => (Point: grid.TranslatePoint(default, DiffViewport), Height: grid.Bounds.Height))
            .Where(row => row.Point.HasValue && row.Point.Value.Y + row.Height > 0 && row.Point.Value.Y < DiffViewport.Bounds.Height).ToArray();
        if (bounds.Length == 0)
        {
            SelectedRangeButton.IsVisible = false;
            return;
        }
        var top = Math.Max(0, bounds.Min(row => row.Point!.Value.Y));
        var bottom = Math.Min(DiffViewport.Bounds.Height, bounds.Max(row => row.Point!.Value.Y + row.Height));
        Canvas.SetLeft(SelectedRangeButton, Math.Max(0, bounds[0].Point!.Value.X));
        Canvas.SetTop(SelectedRangeButton, top);
        SelectedRangeButton.Height = Math.Max(0, bottom - top);
        SelectedRangeButton.IsVisible = bottom > top;
    }
    private RenderedDiffLine? _rangeStart;
    private bool _extendRange;
    private RenderedDiffLine? _commentLine;
    private RenderedDiffLine[]? _commentSelection;
    private void AddLinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _commentLine = null;
        _commentSelection = null;
        _extendRange = false;
        if (e.Source is not Control source)
            return;
        var button = source as Button ?? source.FindAncestorOfType<Button>();
        if (button is not { DataContext: RenderedDiffLine line } || !button.Classes.Contains("line-add"))
            return;
        // Capture before button focus or ListBox pointer handling can alter the Shift-selected rows.
        _commentLine = line;
        _commentSelection = DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToArray();
        _extendRange = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    }
    private void DiffKeyDown(object? sender, KeyEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (e.Key == Key.Escape && _symbolLoad is { IsCancellationRequested: false } load)
    {
        e.Handled = true;
        load.Cancel();
        return;
    }
    if (e.Key != Key.C || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        return;
    if (DiffLines.GetVisualDescendants().OfType<SyntaxText>().Any(t => !string.IsNullOrEmpty(t.SelectedText)))
        return;
    var lines = DiffLines.SelectedItems?.OfType<RenderedDiffLine>().Where(l => l.IsCode).ToHashSet();
    if (DataContext is not DiffSectionViewModel section || lines is null || lines.Count == 0)
        return;
    try
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(string.Join("\n", section.Lines.Where(lines.Contains).Select(l => l.Text)));
            e.Handled = true;
        }
    }
    catch (Exception ex) { section.Message = "Cannot copy code: " + ex.Message; }
}, message => AsyncHandler.Report(this, message));
    public void ScrollTo(ReviewAnchor? anchor)
    {
        if (anchor is null || DataContext is not DiffSectionViewModel section)
            return;
        var line = section.Lines.FirstOrDefault(l => anchor.Side == "LEFT" ? l.Kind == DiffLineKind.Removed && l.OldLineNumber == anchor.Line : l.Kind != DiffLineKind.Removed && l.NewLineNumber == anchor.Line);
        if (line is not null)
            DiffLines.ScrollIntoView(line);
    }
    public void ScrollTo(DiffLine source)
    {
        if (DataContext is not DiffSectionViewModel section)
            return;
        var line = section.Lines.FirstOrDefault(row => row.Kind == source.Kind && row.OldLineNumber == source.OldLineNumber && row.NewLineNumber == source.NewLineNumber);
        if (line is not null)
            DiffLines.ScrollIntoView(line);
    }
    private void ExpandContextClick(object? sender, RoutedEventArgs e) => RevealContext(sender, fromEnd: false);
    private void ExpandContextUpClick(object? sender, RoutedEventArgs e) => RevealContext(sender, fromEnd: true);
    private void RevealContext(object? sender, bool fromEnd)
    {
        if (sender is not Control { DataContext: RenderedDiffLine row } || DataContext is not DiffSectionViewModel section)
            return;
        if (section.ExpandContext(row, fromEnd) is { } revealed)
            ScrollToContextRow(section, revealed);
    }
    private void ExpandAllContextClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (DataContext is not DiffSectionViewModel section)
            return;
        var anchor = FirstVisibleCodeLine();
        if (await section.ExpandAllContextAsync())
            ScrollToContextRow(section, anchor ?? section.Lines.FirstOrDefault(row => row.IsCode));
    }, message => AsyncHandler.Report(this, message));
    private void CollapseContextClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffSectionViewModel section)
            return;
        var anchor = FirstVisibleCodeLine();
        section.CollapseContext();
        if (anchor is { IsExpandedContext: true, NewLineNumber: int number })
            anchor = section.Lines.FirstOrDefault(row => row.NewLineNumber >= number) ?? section.Lines.LastOrDefault(row => row.IsCode);
        ScrollToContextRow(section, anchor ?? section.Lines.FirstOrDefault());
    }
    private RenderedDiffLine? FirstVisibleCodeLine() => DiffLines.GetVisualDescendants().OfType<ListBoxItem>()
        .Where(item => item.DataContext is RenderedDiffLine { IsCode: true })
        .Select(item => (Item: item, Y: item.TranslatePoint(default, DiffViewport)?.Y))
        .Where(row => row.Y is double y && y < DiffViewport.Bounds.Height && y + row.Item.Bounds.Height > 0)
        .OrderBy(row => row.Y).FirstOrDefault().Item?.DataContext as RenderedDiffLine;
    private void ScrollToContextRow(DiffSectionViewModel section, RenderedDiffLine? row)
    {
        if (row is null)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(DataContext, section) && section.Lines.Contains(row))
                DiffLines.ScrollIntoView(row);
        }, DispatcherPriority.Loaded);
    }
}
