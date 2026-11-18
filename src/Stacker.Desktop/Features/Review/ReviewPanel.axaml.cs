// Purpose: Review-panel event bridge for tab changes and local draft-comment editing actions.
using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

public partial class ReviewPanel : UserControl
{
    public ReviewPanel() => InitializeComponent();
    private void StartPrCommentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReviewViewModel { IsBusy: false } vm)
            return;
        vm.BeginPrComment();
        PrCommentBox.Focus();
    }
    private void ClosePrCommentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ReviewViewModel vm)
            vm.IsPrComposerOpen = false;
        StartPrCommentButton.Focus();
    }
    private void EditDraftClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DraftComment draft } && DataContext is ReviewViewModel vm)
            vm.EditDraftCommentCommand.Execute(draft);
    }
    private void ReanchorClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (sender is Control { DataContext: DraftComment draft } && DataContext is ReviewViewModel vm)
            await vm.ProposeReanchorAsync(draft);
    }, message => AsyncHandler.Report(this, message));
    private void RemoveDraftClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DraftComment draft } && DataContext is ReviewViewModel vm)
            vm.RemoveDraftCommentCommand.Execute(draft);
    }
}
