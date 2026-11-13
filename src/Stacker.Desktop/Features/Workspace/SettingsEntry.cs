using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Stacker.Core;
namespace Stacker.Desktop;

public partial class MainWindow
{
    private void ApplyDiffLineHeight()
    {
        if (Application.Current is { } app)
            app.Resources["DiffLineHeight"] = (double)Vm.Settings.DiffLineHeight;
    }

    private void SettingsClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    try
    {
        await Vm.RefreshStorageStatusAsync();
        var git = new TextBox { Text = Vm.Settings.GitPath, Watermark = "Auto-detect Git" };
        var gh = new TextBox { Text = Vm.Settings.GhPath, Watermark = "Auto-detect gh" };
        var editor = new TextBox { Text = Vm.Settings.EditorCommand, Watermark = "Empty uses git core.editor, then $EDITOR" };
        Avalonia.Automation.AutomationProperties.SetName(git, "Git executable");
        Avalonia.Automation.AutomationProperties.SetName(gh, "GitHub CLI executable");
        Avalonia.Automation.AutomationProperties.SetName(editor, "Editor for conflicts and commit messages");
        var savedCredentials = new CheckBox { Content = "Use saved gh credentials for GitHub API", IsChecked = Vm.Settings.UseSavedGhCredentials };
        var background = new CheckBox { Content = "Update GitHub in the background while active", IsChecked = Vm.Settings.BackgroundRefresh };
        var theme = new ToggleSwitch { Content = "Use light appearance", IsChecked = Vm.Settings.Theme == "Light" };
        var lineHeight = new NumericUpDown
        {
            Minimum = AppSettings.MinDiffLineHeight,
            Maximum = AppSettings.MaxDiffLineHeight,
            Increment = 1,
            FormatString = "0",
            Value = Vm.Settings.DiffLineHeight,
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Avalonia.Automation.AutomationProperties.SetName(lineHeight, "Diff line height");
        var storage = new TextBlock { Text = Vm.StorageStatus, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
        var save = new Button { Content = "Save settings", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var window = new Window
        {
            Title = "Settings",
            Width = 580,
            Height = 600,
            MinWidth = 480,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = DialogLayout.Create("Settings", "Appearance, tools and background updates.", new Control[] {
                    theme, new TextBlock { Text = "Diff line height" }, lineHeight,
                    new TextBlock { Text = "Git executable", FontWeight = Avalonia.Media.FontWeight.SemiBold }, git, new TextBlock { Text = "GitHub CLI executable" }, gh,
                    new TextBlock { Text = "Editor for conflicts and commit messages" }, editor,
                    new TextBlock { Text = "Add a wait flag (for example \"code --wait\") if your editor returns immediately.", FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    savedCredentials, background, storage, new TextBlock { Text = "Connection changes are checked automatically.", FontSize = 12 }
                }, cancel, save)
        };
        cancel.Click += (_, _) => window.Close();
        save.Click += (_, _) => AsyncHandler.Run(async () =>
        {
            static string? Setting(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
            var settings = Vm.Settings;
            var toolsChanged = settings.GitPath != Setting(git) || settings.GhPath != Setting(gh)
                || settings.EditorCommand != Setting(editor) || settings.UseSavedGhCredentials != (savedCredentials.IsChecked == true)
                || settings.BackgroundRefresh != (background.IsChecked == true);
            var nextTheme = theme.IsChecked == true ? "Light" : "Dark";
            var themeChanged = settings.Theme != nextTheme;
            settings.GitPath = Setting(git);
            settings.GhPath = Setting(gh);
            settings.EditorCommand = Setting(editor);
            settings.UseSavedGhCredentials = savedCredentials.IsChecked == true;
            settings.BackgroundRefresh = background.IsChecked == true;
            settings.Theme = nextTheme;
            settings.DiffLineHeight = Math.Clamp((int)(lineHeight.Value ?? AppSettings.DefaultDiffLineHeight),
                AppSettings.MinDiffLineHeight, AppSettings.MaxDiffLineHeight);
            await Vm.SaveSettingsAsync();
            ApplyDiffLineHeight();
            if (themeChanged)
            {
                ApplyTheme();
                if (Vm.ActiveHistory is { } history)
                    history.Theme = settings.Theme;
                foreach (var section in Vm.Sections.Concat(Vm.ActiveHistory?.DocumentSections.AsEnumerable() ?? []))
                {
                    section.Theme = settings.Theme;
                    await section.LoadAsync(section.SelectedFile);
                }
            }
            window.Close();
            if (toolsChanged || themeChanged)
                await Vm.RefreshAllAsync();
        }, message => AsyncHandler.Report(this, message));
        await window.ShowDialog(this);
    }
    catch (Exception ex) { Vm.Error = ex.Message; }
}, message => AsyncHandler.Report(this, message));
}
