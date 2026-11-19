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
        var git = new TextBox { Text = Vm.Settings.GitPath, Watermark = "Path to Git" };
        var gh = new TextBox { Text = Vm.Settings.GhPath, Watermark = "Path to gh" };
        var editor = new TextBox { Text = Vm.Settings.EditorCommand, Watermark = "code --wait" };
        Avalonia.Automation.AutomationProperties.SetName(git, "Git executable");
        Avalonia.Automation.AutomationProperties.SetName(gh, "GitHub CLI executable");
        Avalonia.Automation.AutomationProperties.SetName(editor, "Editor for conflicts and commit messages");
        var savedCredentials = new CheckBox { Content = new TextBlock { Text = "Use saved gh credentials for GitHub API", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsChecked = Vm.Settings.UseSavedGhCredentials };
        var background = new CheckBox { Content = new TextBlock { Text = "Update GitHub in the background while active", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsChecked = Vm.Settings.BackgroundRefresh };
        Avalonia.Automation.AutomationProperties.SetName(savedCredentials, "Use saved gh credentials for GitHub API");
        Avalonia.Automation.AutomationProperties.SetName(background, "Update GitHub in the background while active");
        var theme = new ToggleSwitch { Content = "Use light appearance", IsChecked = Vm.Settings.Theme == "Light" };
        var reduceMotion = new CheckBox { Content = "Reduce motion", IsChecked = Vm.Settings.ReduceMotion };
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
        var storage = new TextBlock { Text = Vm.StorageStatus, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } };
        storage.Bind(TextBlock.FontSizeProperty, storage.GetResourceObservable("StackerCaptionFontSize"));
        var save = new Button { Content = "Save settings", IsDefault = true, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var window = new Window
        {
            Title = "Settings",
            Width = 580,
            Height = 600,
            MinWidth = 480,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = DialogLayout.Create("Settings", "Changes take effect when you save.", new Control[] {
                    DialogLayout.Section("Appearance", theme, reduceMotion,
                        DialogLayout.Field("Diff line height", lineHeight, $"{AppSettings.MinDiffLineHeight}–{AppSettings.MaxDiffLineHeight} pixels per code line")),
                    DialogLayout.Section("Tools",
                        DialogLayout.Field("Git executable", git, "Leave empty to detect Git automatically"),
                        DialogLayout.Field("GitHub CLI executable", gh, "Leave empty to detect the GitHub CLI automatically"),
                        DialogLayout.Field("Editor for conflicts and commit messages", editor,
                            "Leave empty to use Git’s configured editor, then $EDITOR. Add a wait flag, such as code --wait, if needed")),
                    DialogLayout.Section("GitHub updates", savedCredentials, background),
                    DialogLayout.Section("Workspace data", storage)
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
            settings.ReduceMotion = reduceMotion.IsChecked == true;
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
