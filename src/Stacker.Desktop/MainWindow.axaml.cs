using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;
public partial class MainWindow : Window
{
    private bool _ready;
    private bool _closing;
    private MainViewModel Vm => (MainViewModel)DataContext!;
    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => { if (_ready) Vm.SetWindowActive(true); };
        KeyDown += SearchShortcut;
        Deactivated += (_, _) => { if (_ready) Vm.SetWindowActive(false); };
        SizeChanged += (_, _) => Grid.SetColumn(ReviewHost, Bounds.Width < 1320 ? 2 : 3);
        Opened += async (_, _) =>
        {
            await Vm.InitializeAsync();
            Width = Vm.Settings.WindowWidth; Height = Vm.Settings.WindowHeight;
            Workspace.ColumnDefinitions[0].Width = new(Vm.Settings.NavigationWidth);
            ApplyTheme();
            Vm.Sections.CollectionChanged += (_, _) => ResizeSections();
            SectionScroll.SizeChanged += (_, _) => ResizeSections();
            Workspace.ColumnDefinitions[0].MinWidth = 200;
            Workspace.ColumnDefinitions[0].MaxWidth = 500;
            _ready = true;
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--demo")) await Vm.OpenDemoAsync();
            else if (args.Length > 1 && Directory.Exists(args[1])) await Vm.OpenRepositoryAsync(args[1]);
            else if (Vm.Settings.LastRepository is { } last && Directory.Exists(last)) await Vm.OpenRepositoryAsync(last);
            Vm.SetWindowActive(IsActive);
        };
        Closing += async (_, e) =>
        {
            if (_closing || !_ready) return;
            e.Cancel = true; _closing = true;
            Vm.Settings.WindowWidth = Width; Vm.Settings.WindowHeight = Height;
            Vm.Settings.NavigationWidth = Workspace.ColumnDefinitions[0].ActualWidth;
            try { await Vm.SaveWorkspaceAsync(); await Vm.FlushReviewsAsync(); await Vm.SaveSettingsAsync(); Vm.Dispose(); Close(); }
            catch (Exception ex) { _closing = false; Vm.Error = ex.Message; }
        };
    }
    private void ResizeSections()
    {
        foreach (var section in Vm.Sections) section.PanelHeight = Vm.Sections.Count == 1 ? Math.Max(300, SectionScroll.Bounds.Height - 12) : 440;
    }
    private void ApplyTheme() { if (Application.Current is { } app) app.RequestedThemeVariant = Vm.Settings.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark; }
    private void ChangesTabClick(object? sender, RoutedEventArgs e) => Vm.IsGitLogOpen = false;
    private void GitLogsTabClick(object? sender, RoutedEventArgs e) => Vm.IsGitLogOpen = true;
    private void FindClick(object? sender, RoutedEventArgs e) => OpenFind("Files");
    private void OpenFind(string scope) { Vm.OpenSearch(scope); SearchBox.Focus(); SearchBox.SelectAll(); }
    private void SearchShortcut(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        if (e.Key == Key.P) { e.Handled = true; OpenFind("Files"); }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; OpenFind("Changed lines"); }
    }
    private void SearchKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; _ = Vm.SearchAsync(); } else if (e.Key == Key.Escape) Vm.CloseSearch(); }
    private void CloseSearchClick(object? sender, RoutedEventArgs e) => Vm.CloseSearch();
    private async void SearchResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: ChangeSearchHit hit } list) return;
        list.SelectedItem = null;
        await Vm.OpenSearchHitAsync(hit);
        var view = SectionScroll.GetVisualDescendants().OfType<DiffSectionView>().FirstOrDefault(v => ReferenceEquals(v.DataContext, hit.Section));
        if (view is not null) { view.BringIntoView(); if (hit.Line is not null) view.ScrollTo(hit.Line); }
        Vm.CloseSearch();
    }
    private async void CopyGitLogClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(string.Join("\n\n", Vm.GitLogEntries.Select(entry => entry.Summary + "\n" + entry.WorkingDirectory + "\n" + entry.Detail)));
        }
        catch (Exception ex) { Vm.Error = "Cannot copy Git logs: " + ex.Message; }
    }
    private async void OpenFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Open Git repository", AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) await Vm.OpenRepositoryAsync(path);
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
    }
    private async void PathKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Vm.OpenAsync(); } }
    private async void RecentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready && RecentBox.SelectedItem is string path) { RecentBox.SelectedItem = null; await Vm.OpenRepositoryAsync(path); }
    }
    private async void StackClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: StackGroupViewModel group })
        { if (group.IsSinglePr) await Vm.SelectLayerAsync(group.Layers[0], false); else await Vm.SelectGroupAsync(group); } }
    private async void LayerClick(object? sender, RoutedEventArgs e) { if (sender is Control { DataContext: LayerViewModel layer }) await Vm.SelectLayerAsync(layer, false); }
    private async void LayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0 && sender is Control { DataContext: LayerViewModel layer }) { e.Handled = true; await Vm.SelectLayerAsync(layer, true); }
    }
    private async void LayerChecked(object? sender, RoutedEventArgs e) { if (sender is Control { DataContext: LayerViewModel layer }) await Vm.SelectLayerAsync(layer, true); }
    private void ThisLayerClick(object? sender, RoutedEventArgs e) => Vm.Mode = Stacker.Core.DiffMode.Layer;
    private void ThroughLayerClick(object? sender, RoutedEventArgs e) => Vm.Mode = Stacker.Core.DiffMode.Cumulative;
    private async void SaveAsLocalClick(object? sender, RoutedEventArgs e) => await Vm.SaveRemoteAsLocalAsync();
    private async void HelpClick(object? sender, RoutedEventArgs e)
    {
        var text = "For main → A → B → C:\n\nEntire stack: main → C. Click Overview inside a stack.\n\nThis layer: A → B. Click layer 2.\n\nThrough this layer: main → B. Includes layers 1 and 2. At layer 3 it equals Entire stack.\n\nSelected layers: check 1 and 3. Two separate comparisons: main → A and B → C. Layer 2 is not folded into layer 3.\n\nComparisons start from the common ancestor. Diverged branches show a warning.\n\nTry Open demo: Authorization changes the same file across three layers; Payments is independent. Shared foundation demonstrates branching PRs. Service branches demonstrate discovery boundaries.\n\nStack titles restore your last view. GitHub updates quietly every minute while this window is active. New code waits for Update changes; returning to the window does not reload it.";
        await new Window { Title = "How comparisons work", Width = 590, Height = 590, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(24) } } }.ShowDialog(this);
    }
    private async void GitHubSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Repository is null) return;
        var origin = new TextBox { Text = Vm.GitHubPreferences.GitHubOverride, Watermark = "Auto-detect origin, or host/owner/repo" };
        var boundaries = new TextBox { Text = string.Join("\n", Vm.GitHubPreferences.BoundaryBranches ?? []), AcceptsReturn = true, MinHeight = 130 };
        var save = new Button { Content = "Save and refresh", HorizontalAlignment = HorizontalAlignment.Right };
        var clear = new Button { Content = "Clear downloaded Git objects" };
        var window = new Window { Title = "GitHub settings", Width = 570, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children = {
                new TextBlock { Text = "GitHub repository override" }, origin,
                new TextBlock { Text = "Stack boundaries · one exact branch name per line" }, boundaries,
                new TextBlock { Text = "PRs into these branches can start stacks; links do not cross them.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, clear, save } } };
        save.Click += async (_, _) => { await Vm.SaveGitHubPreferencesAsync(new() { GitHubOverride = string.IsNullOrWhiteSpace(origin.Text) ? null : origin.Text.Trim(), BoundaryBranches = (boundaries.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList() }); window.Close(); };
        clear.Click += async (_, _) => await Vm.ClearGitHubCacheAsync();
        await window.ShowDialog(this);
    }
    private async void NewStackClick(object? sender, RoutedEventArgs e) => await EditAsync(false);
    private async void EditStackClick(object? sender, RoutedEventArgs e) => await EditAsync(true);
    private async Task EditAsync(bool existing)
    {
        if (Vm.Repository is null || existing && Vm.SelectedStack is null) return;
        try
        {
            var editor = new StackEditorWindow(Vm.Repository, existing ? Vm.SelectedStack : null, Vm.SaveStackAsync);
            var accepted = await editor.ShowDialog<bool>(this);
            if (accepted && editor.DeleteRequested) await Vm.DeleteSelectedStackAsync();
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
    }
    private async void PrCommentsClick(object? sender, RoutedEventArgs e) => await Vm.ShowReviewAsync(tab: 0);
    private async void ReviewPrClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Sections.FirstOrDefault() is { IsPrSnapshot: false, HasPr: true } section) await Vm.OpenPrChangesAsync(section);
        await Vm.ShowReviewAsync();
    }
    private async void OverviewClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: StackGroupViewModel group }) await Vm.SelectOverviewAsync(group); }
    private async void EditGroupClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: StackGroupViewModel group }) { await Vm.SelectGroupAsync(group); await EditAsync(true); } }
    private async void SaveGroupClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: StackGroupViewModel group }) { await Vm.SelectGroupAsync(group); await Vm.SaveRemoteAsLocalAsync(); } }
    private async void SettingsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var git = new TextBox { Text = Vm.Settings.GitPath, Watermark = "Auto-detect Git" };
            var gh = new TextBox { Text = Vm.Settings.GhPath, Watermark = "Auto-detect gh" };
            var savedCredentials = new CheckBox { Content = "Use saved gh credentials (ignore environment tokens)", IsChecked = Vm.Settings.UseSavedGhCredentials };
            var background = new CheckBox { Content = "Update GitHub in the background while active", IsChecked = Vm.Settings.BackgroundRefresh };
            var theme = new ComboBox { ItemsSource = new[] { "Dark", "Light" }, SelectedItem = Vm.Settings.Theme, HorizontalAlignment = HorizontalAlignment.Stretch };
            var save = new Button { Content = "Save settings", HorizontalAlignment = HorizontalAlignment.Right };
            var window = new Window
            {
                Title = "Settings", Width = 580, Height = 520, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children =
                {
                    new TextBlock { Text = "Git executable", FontWeight = Avalonia.Media.FontWeight.SemiBold }, git, new TextBlock { Text = "GitHub CLI executable" }, gh,
                    savedCredentials, background, new TextBlock { Text = "Connection changes are checked automatically.", FontSize = 12 }, new TextBlock { Text = "Theme" }, theme, save
                } }
            };
            save.Click += async (_, _) => { Vm.Settings.GitPath = string.IsNullOrWhiteSpace(git.Text) ? null : git.Text.Trim(); Vm.Settings.GhPath = string.IsNullOrWhiteSpace(gh.Text) ? null : gh.Text.Trim(); Vm.Settings.UseSavedGhCredentials = savedCredentials.IsChecked == true; Vm.Settings.BackgroundRefresh = background.IsChecked == true; Vm.Settings.Theme = theme.SelectedItem as string ?? "Dark"; await Vm.SaveSettingsAsync(); ApplyTheme(); foreach (var section in Vm.Sections) { section.Theme = Vm.Settings.Theme; await section.LoadAsync(section.SelectedFile); } window.Close(); await Vm.RefreshAllAsync(); };
            await window.ShowDialog(this);
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
    }
}
