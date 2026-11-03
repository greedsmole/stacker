using Avalonia.Interactivity;
namespace Stacker.Desktop;

public partial class MainWindow
{
    private void HistoryClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        if (DataContext is ViewModels.MainViewModel model && await model.OpenHistoryAsync() is { } history)
            await new HistoryWindow { DataContext = history }.ShowDialog(this);
    }, error => { if (DataContext is ViewModels.MainViewModel model) model.Error = error; });
}
