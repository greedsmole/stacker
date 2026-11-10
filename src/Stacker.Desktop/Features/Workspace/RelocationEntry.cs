using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
namespace Stacker.Desktop;

public partial class MainWindow
{
    private void RelocateRepositoryClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        var repositories = await Vm.KnownRepositoriesAsync();
        if (repositories.Count == 0) { Vm.Error = "No previous repository identity is available."; return; }
        var original = new ComboBox { ItemsSource = repositories.Select(r => r.Root).ToArray(), SelectedIndex = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(original, "Previous repository location");
        var choose = new Button { Content = "Locate new folder…", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window { Title = "Locate moved repository", Width = 620, Height = 320, MinWidth = 480, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = DialogLayout.Create("Locate moved repository", "Keep its history and review progress by reconnecting the previous location.", new Control[] { new TextBlock { Text = "Previous location" }, original }, cancel, choose);
        cancel.Click += (_, _) => dialog.Close();
        choose.Click += (_, _) => AsyncHandler.Run(async () =>
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new() { Title = "Locate this repository", AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } root) return;
            var selected = repositories[original.SelectedIndex];
            await Vm.RelocateRepositoryAsync(selected.Repository, root, selected.Worktree); dialog.Close();
        }, message => AsyncHandler.Report(this, message));
        await dialog.ShowDialog(this);
    }, message => AsyncHandler.Report(this, message));
}
