using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

public partial class MainWindow
{
    private void PullRequestsTabClick(object? sender, RoutedEventArgs e) => SetStackTab(local: false);
    private void LocalStacksTabClick(object? sender, RoutedEventArgs e) => SetStackTab(local: true);

    private void SetStackTab(bool local)
    {
        PullRequestsTab.IsChecked = PullRequestsPane.IsVisible = !local;
        LocalStacksTab.IsChecked = LocalStacksPane.IsVisible = local;
    }

    private void ToggleStackExpansionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StackGroupViewModel group })
            return;
        group.IsExpanded = !group.IsExpanded;
    }
}
