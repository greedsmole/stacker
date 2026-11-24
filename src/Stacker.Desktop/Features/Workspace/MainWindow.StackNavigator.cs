using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Stacker.Core.Framework;
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

    private void OpenActiveStackClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.ActiveStackGroup is not { } group)
            return;

        _focusMode = false;
        Vm.Settings.StacksHidden = false;
        group.IsExpanded = true;
        SetStackTab(local: !group.IsRemote);
        ApplyWorkspaceLayout();
        Dispatcher.UIThread.Post(() =>
        {
            var target = FindLayerButton(layer => layer.Group == group && layer.IsSelected)
                ?? StacksPane.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(button => button.Classes.Contains("stack-title") && button.DataContext == group);
            target?.BringIntoView();
            target?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void StackLayerKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Button { DataContext: LayerViewModel layer } || e.KeyModifiers != KeyModifiers.None
            || e.Key is not (Key.Up or Key.Down))
            return;

        var position = layer.Number + (e.Key == Key.Up ? -1 : 1);
        if (FindLayerButton(candidate => candidate.Group == layer.Group && candidate.Number == position) is not { } target)
            return;

        target.BringIntoView();
        e.Handled = target.Focus(NavigationMethod.Directional);
    }

    private Button? FindLayerButton(Func<LayerViewModel, bool> matches) =>
        StacksPane.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
            button.IsEffectivelyVisible && button.Classes.Contains("layer-title")
            && button.DataContext is LayerViewModel layer && matches(layer));

    private void CheckoutLayerClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (Vm.CheckoutLayer is not { } branch || Vm.IsBusy || Vm.IsSyncing)
            return;
        var (ok, message) = await Vm.SwitchToBranchAsync(LocalBranch.Prefix + branch);
        if (ok)
            Vm.Status = message;
        else
            Vm.Error = message;
    }, message => AsyncHandler.Report(this, message));
}
