using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
namespace Stacker.Desktop;

public partial class MainWindow
{
    private void SettingsClick(object? sender, RoutedEventArgs e)
=>AsyncHandler.Run(async () => {         try
        {
            await Vm.RefreshStorageStatusAsync();
            var git = new TextBox { Text = Vm.Settings.GitPath, Watermark = "Auto-detect Git" };
            var gh = new TextBox { Text = Vm.Settings.GhPath, Watermark = "Auto-detect gh" };
            var editor = new TextBox { Text = Vm.Settings.EditorCommand, Watermark = "Empty uses git core.editor, then $EDITOR" };
            var savedCredentials = new CheckBox { Content = "Use saved gh credentials for GitHub API", IsChecked = Vm.Settings.UseSavedGhCredentials };
            var background = new CheckBox { Content = "Update GitHub in the background while active", IsChecked = Vm.Settings.BackgroundRefresh };
            var theme = new ComboBox { ItemsSource = new[] { "Dark", "Light" }, SelectedItem = Vm.Settings.Theme, HorizontalAlignment = HorizontalAlignment.Stretch };
            var storage = new TextBlock { Text = Vm.StorageStatus, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
            var save = new Button { Content = "Save settings", HorizontalAlignment = HorizontalAlignment.Right };
            var window = new Window
            {
                Title = "Settings", Width = 580, Height = 700, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children =
                {
                    new TextBlock { Text = "Git executable", FontWeight = Avalonia.Media.FontWeight.SemiBold }, git, new TextBlock { Text = "GitHub CLI executable" }, gh,
                    new TextBlock { Text = "Editor for conflicts and commit messages" }, editor,
                    new TextBlock { Text = "Add a wait flag (for example \"code --wait\") if your editor returns immediately.", FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    savedCredentials, background, storage, new TextBlock { Text = "Connection changes are checked automatically.", FontSize = 12 }, new TextBlock { Text = "Theme" }, theme, save
                } } }
            };
            save.Click += (_, _) => AsyncHandler.Run(async () => { Vm.Settings.GitPath = string.IsNullOrWhiteSpace(git.Text) ? null : git.Text.Trim(); Vm.Settings.GhPath = string.IsNullOrWhiteSpace(gh.Text) ? null : gh.Text.Trim(); Vm.Settings.EditorCommand = string.IsNullOrWhiteSpace(editor.Text) ? null : editor.Text.Trim(); Vm.Settings.UseSavedGhCredentials = savedCredentials.IsChecked == true; Vm.Settings.BackgroundRefresh = background.IsChecked == true; Vm.Settings.Theme = theme.SelectedItem as string ?? "Dark"; await Vm.SaveSettingsAsync(); ApplyTheme(); foreach (var section in Vm.Sections) { section.Theme = Vm.Settings.Theme; await section.LoadAsync(section.SelectedFile); } window.Close(); await Vm.RefreshAllAsync();  }, message => AsyncHandler.Report(this, message));
            await window.ShowDialog(this);
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
 }, message => AsyncHandler.Report(this, message));
}
