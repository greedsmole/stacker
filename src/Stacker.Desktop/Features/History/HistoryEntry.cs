using Avalonia.Interactivity;
namespace Stacker.Desktop;

public partial class MainWindow
{
    /// <summary>Opens history through the window's asynchronous event boundary.</summary>
    /// <param name="sender">Control raising the click event.</param>
    /// <param name="e">Avalonia routed event arguments.</param>
    private void HistoryClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (DataContext is ViewModels.MainViewModel model) await model.ShowHistoryAsync();
    }, error => { if (DataContext is ViewModels.MainViewModel model) model.Error = error; });
}
