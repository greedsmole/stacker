using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Stacker.Desktop;

public partial class MainWindow
{
    private void ToggleStacksClick(object? sender, RoutedEventArgs e)
    {
        RememberStacksWidth();
        Vm.Settings.StacksHidden = !Vm.Settings.StacksHidden;
        ApplyStacksVisibility();
    }

    private void ApplyStacksVisibility()
    {
        var visible = !Vm.Settings.StacksHidden;
        var column = Workspace.ColumnDefinitions[0];
        column.MinWidth = visible ? 200 : 0;
        column.MaxWidth = 500;
        column.Width = new(visible ? Vm.Settings.NavigationWidth : 0);
        Workspace.ColumnDefinitions[1].Width = new(visible ? 1 : 0);
        StacksPane.IsVisible = StacksSplitter.IsVisible = visible;
        StacksToggle.IsChecked = visible;
        ToolTip.SetTip(StacksToggle, visible ? "Hide stacks" : "Show stacks");
    }

    private void RememberStacksWidth()
    {
        if (StacksPane.IsVisible && Workspace.ColumnDefinitions[0].ActualWidth >= 200)
            Vm.Settings.NavigationWidth = Workspace.ColumnDefinitions[0].ActualWidth;
    }
}
