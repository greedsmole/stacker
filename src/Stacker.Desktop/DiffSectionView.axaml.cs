using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Interactivity;
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
    private async void ReviewClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffSectionViewModel section || TopLevel.GetTopLevel(this) is not MainWindow window || window.DataContext is not MainViewModel main) return;
        if (!section.IsPrSnapshot) { await main.OpenPrChangesAsync(section); return; }
        var review = main.CreateReview(section); if (review is null) return;
        try
        {
            await review.InitializeAsync(DiffLines.SelectedItems?.OfType<RenderedDiffLine>());
            await new ReviewWindow { DataContext = review }.ShowDialog(window);
            await review.FlushAsync();
        }
        catch (Exception ex) { main.Error = ex.Message; }
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
