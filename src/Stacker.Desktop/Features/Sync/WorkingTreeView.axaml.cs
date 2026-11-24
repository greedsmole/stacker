using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Core;
using Stacker.Desktop.ViewModels;

namespace Stacker.Desktop;

public partial class WorkingTreeView : UserControl
{
    public WorkingTreeView() => InitializeComponent();

    private void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, WorktreeFiles) && DataContext is WorkingTreeViewModel model)
            model.SetSelection(WorktreeFiles.SelectedItems?.OfType<WorkingTreeFile>() ?? []);
    }

    private void UnstagedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkingTreeViewModel model)
            model.SelectSide(WorkingTreeSide.Unstaged);
        UnstagedButton.IsChecked = true;
    }

    private void StagedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkingTreeViewModel model)
            model.SelectSide(WorkingTreeSide.Staged);
        StagedButton.IsChecked = true;
    }

    private async void RefreshClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkingTreeViewModel model)
            await model.RefreshAsync();
    }
}
