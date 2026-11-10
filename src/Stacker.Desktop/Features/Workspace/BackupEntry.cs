using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Stacker.Core;
namespace Stacker.Desktop;

public partial class MainWindow
{
    public Func<IReadOnlyList<string>, Task<UnsavedCloseChoice>>? CloseFailureHandler { get; set; }
    private void ExportDiagnosticsClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Export diagnostic metadata", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder) await Vm.ExportDiagnosticsAsync(folder);
    }, message => AsyncHandler.Report(this, message));
    private void ExportWorkspaceClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Export workspace", SuggestedFileName = "Stacker-workspace.zip" });
        if (file?.TryGetLocalPath() is { } path) await Vm.ExportWorkspaceAsync(path);
    }, message => AsyncHandler.Report(this, message));
    private void RestoreWorkspaceClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Choose workspace backup", AllowMultiple = false });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose an empty folder for the restored copy", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder) await Vm.RestoreWorkspaceAsync(path, folder);
    }, message => AsyncHandler.Report(this, message));
    private void OpenRestoredWorkspaceClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    { Vm.OpenRestoredWorkspace(); Close(); await Task.CompletedTask; }, message => AsyncHandler.Report(this, message));
    private async Task<UnsavedCloseChoice> AskUnsavedAsync(IReadOnlyList<string> problems)
    {
        if (CloseFailureHandler is not null) return await CloseFailureHandler(problems);
        var dialog = new Window { Title = "Unsaved work", Width = 580, Height = 330, CanResize = false };
        var panel = new StackPanel { Margin = new(20), Spacing = 12 }; panel.Children.Add(new TextBlock { Text = "Some changes could not be saved", FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = string.Join("\n", problems), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxHeight = 150 });
        foreach (var (label, choice) in new[] { ("Retry", UnsavedCloseChoice.Retry), ("Save a text copy and close", UnsavedCloseChoice.SaveCopy), ("Continue editing", UnsavedCloseChoice.ContinueEditing), ("Close without saving", UnsavedCloseChoice.Discard) })
        { var button = new Button { Content = label }; button.Click += (_, _) => dialog.Close(choice); panel.Children.Add(button); }
        dialog.Content = new ScrollViewer { Content = panel }; return await dialog.ShowDialog<UnsavedCloseChoice>(this);
    }
    private async Task HandleCloseAsync()
    {
        if (_closing) return; _closing = true;
        try
        {
            Vm.Settings.WindowWidth = Width; Vm.Settings.WindowHeight = Height; Vm.Settings.NavigationWidth = Workspace.ColumnDefinitions[0].ActualWidth;
            while (true)
            {
                var problems = await Vm.PrepareCloseAsync(); if (problems.Count == 0) break;
                var choice = await AskUnsavedAsync(problems);
                if (choice == UnsavedCloseChoice.ContinueEditing) { Vm.CancelClose(); _closing = false; return; }
                if (choice == UnsavedCloseChoice.Retry) continue;
                if (choice == UnsavedCloseChoice.Discard) break;
                var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save unsent review text", SuggestedFileName = "Stacker-unsaved.txt" });
                if (file?.TryGetLocalPath() is not { } path) continue;
                await Vm.SaveRecoveryCopyAsync(path); break;
            }
            await Vm.CommitCloseAsync(); Close();
        }
        catch { Vm.CancelClose(); _closing = false; throw; }
    }
}
