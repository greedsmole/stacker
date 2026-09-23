using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;
public partial class ReviewWindow : Window
{
    public ReviewWindow() => InitializeComponent();
    private void RemoveDraftClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DraftComment comment } && DataContext is ReviewViewModel vm) vm.RemoveDraftCommentCommand.Execute(comment);
    }
}
