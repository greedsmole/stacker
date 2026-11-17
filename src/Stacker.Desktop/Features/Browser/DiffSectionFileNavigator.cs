using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

public partial class DiffSectionView
{
    public static readonly StyledProperty<bool> ShowFileNavigatorProperty =
        AvaloniaProperty.Register<DiffSectionView, bool>(nameof(ShowFileNavigator), defaultValue: true);

    private double _filesWidth = 220;

    /// <summary>Controls visibility of the section's file navigator.</summary>
    public bool ShowFileNavigator
    {
        get => GetValue(ShowFileNavigatorProperty);
        set => SetValue(ShowFileNavigatorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowFileNavigatorProperty && ContentGrid is not null)
            ApplyFileNavigatorVisibility();
    }

    private void InitializeFileNavigator()
    {
        var column = ContentGrid.ColumnDefinitions[0];
        column.MaxWidth = 600;
        column.PropertyChanged += RememberFilesWidth;
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is MainWindow { DataContext: MainViewModel main })
            {
                _filesWidth = main.Settings.FilesWidth;
                ApplyFileNavigatorVisibility();
            }
        };
        ApplyFileNavigatorVisibility();
    }

    private void RememberFilesWidth(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != ColumnDefinition.WidthProperty || !ShowFileNavigator ||
            sender is not ColumnDefinition { Width.IsAbsolute: true } column)
            return;

        var width = Math.Clamp(column.Width.Value, column.MinWidth, column.MaxWidth);
        if (width == _filesWidth)
            return;

        _filesWidth = width;
        if (TopLevel.GetTopLevel(this) is MainWindow { DataContext: MainViewModel main })
            main.Settings.FilesWidth = width;
    }

    private void ApplyFileNavigatorVisibility()
    {
        var visible = ShowFileNavigator;
        var column = ContentGrid.ColumnDefinitions[0];
        column.MinWidth = visible ? 200 : 0;
        column.Width = new GridLength(visible ? _filesWidth : 0);
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(visible ? 1 : 0);
        FilePane.IsVisible = FileSplitter.IsVisible = visible;
    }

    private void ToggleFileFoldersClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (sender is not ToggleButton toggle || DataContext is not DiffSectionViewModel section)
            return;
        var enabled = toggle.IsChecked == true;
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
            await main.SetGroupFilesByFolderAsync(enabled);
        else
            section.GroupFilesByFolder = enabled;
    }, message => AsyncHandler.Report(this, message));
}
