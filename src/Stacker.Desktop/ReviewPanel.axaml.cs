// Purpose: Review-panel event bridge for tab changes and local draft-comment editing actions.
using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;
public partial class ReviewPanel : UserControl
{
    public ReviewPanel() => InitializeComponent();
    private void TabChanged(object? sender, SelectionChangedEventArgs e)
    { if (ReferenceEquals(sender, e.Source) && DataContext is ReviewViewModel vm && vm.PanelTab == 0) vm.BeginPrComment(); }
    private void EditDraftClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: DraftComment draft } && DataContext is ReviewViewModel vm) vm.EditDraftCommentCommand.Execute(draft); }
    private void RemoveDraftClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: DraftComment draft } && DataContext is ReviewViewModel vm) vm.RemoveDraftCommentCommand.Execute(draft); }
}
