using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Input;
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
        if (!_restoring && _section is { IsBusy: false, IsExpanded: true } && _scroll is not null && e.OffsetDelta != Vector.Zero)
        { _section.ScrollX = _scroll.Offset.X; _section.ScrollY = _scroll.Offset.Y; }
    }
    private int? _rangeStart;
    private bool _extendRange;
    private void AddLinePointerPressed(object? sender, PointerPressedEventArgs e) => _extendRange = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
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
    private async void ReviewClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffSectionViewModel section || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        if (!section.IsPrSnapshot) { await main.OpenPrChangesAsync(section); return; }
        await main.ShowReviewAsync(section);
    }
    private async void FileCommentClick(object? sender, RoutedEventArgs e)
    { if (DataContext is DiffSectionViewModel section && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main) await main.ShowReviewAsync(section, 1); }
    private async void AddLineClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: RenderedDiffLine line }) { await BeginLineAsync(line, _extendRange); _extendRange = false; } }
    private async void LineNumberPressed(object? sender, PointerPressedEventArgs e)
    { if (sender is Control { DataContext: RenderedDiffLine line }) { e.Handled = true; await BeginLineAsync(line, e.KeyModifiers.HasFlag(KeyModifiers.Shift), Grid.GetColumn((Control)sender) == 1 ? "LEFT" : "RIGHT"); } }
    private async Task BeginLineAsync(RenderedDiffLine line, bool range, string? side = null)
    {
        if (DataContext is not DiffSectionViewModel section || !line.IsCode || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
        if (!section.IsPrSnapshot) { section.Message = section.HasPr ? "Review the PR snapshot to comment on code. Use Review PR in the file menu." : "Select a GitHub PR layer to comment on code."; return; }
        var session = await main.EnsureReviewAsync(section); if (session is null || session.IsBusy) return;
        var end = section.Lines.IndexOf(line);
        var start = range && _rangeStart is { } first ? first : end; _rangeStart = start;
        var selected = section.Lines.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1).ToArray();
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
