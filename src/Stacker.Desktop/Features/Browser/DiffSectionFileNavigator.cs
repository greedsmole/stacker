using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

public partial class DiffSectionView
{
    private double _filesWidth = 220;
    /// <summary>Controls visibility of the section's file navigator.</summary>
    public bool ShowFileNavigator
    {
        get => FilesToggle.IsVisible;
        set
        {
            FilesToggle.IsVisible = value;
            if (!value && FilePane.IsVisible) ToggleFilesClick(null, new RoutedEventArgs());
        }
    }
    private void ToggleFilesClick(object? sender, RoutedEventArgs e)
    {
        var column = ContentGrid.ColumnDefinitions[0];
        if (FilePane.IsVisible)
        {
            _filesWidth = column.ActualWidth;
            FilePane.IsVisible = FileSplitter.IsVisible = false;
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
        else
        {
            column.MinWidth = 200;
            column.Width = new GridLength(_filesWidth);
            FilePane.IsVisible = FileSplitter.IsVisible = true;
        }
    }
    private void ToggleFileFoldersClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (sender is not ToggleButton toggle || DataContext is not DiffSectionViewModel section) return;
        var enabled = toggle.IsChecked == true;
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main) await main.SetGroupFilesByFolderAsync(enabled);
        else section.GroupFilesByFolder = enabled;
    }, message => AsyncHandler.Report(this, message));
}
