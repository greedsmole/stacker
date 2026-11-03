using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

public sealed partial class HistoryWindow : Window
{
    public HistoryWindow() { AvaloniaXamlLoader.Load(this); Closed += (_, _) => (DataContext as HistoryWorkspaceViewModel)?.Dispose(); }
}
