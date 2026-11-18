using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
        var pointer = TakePointerPress(sender);
        if (sender is not Button { DataContext: StackGroupViewModel group } toggle)
            return;
        group.IsExpanded = !group.IsExpanded;
        if (!group.IsExpanded || !pointer || Vm.Settings.ReduceMotion)
            return;
        if (toggle.FindAncestorOfType<Border>()?.FindAncestorOfType<StackPanel>() is not { } owner ||
            owner.Children.OfType<StackPanel>().FirstOrDefault(panel => panel.Name == "StackLayers") is not { } layers)
            return;
        AsyncHandler.Run(() => WorkspaceMotion.RevealAsync(layers, TimeSpan.FromMilliseconds(150), y: -4),
            message => AsyncHandler.Report(this, message));
    }
}
