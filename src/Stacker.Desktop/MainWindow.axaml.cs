// Purpose: Main-window event bridge: translates user gestures into view-model actions and owns native dialogs/clipboard interaction.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;
public partial class MainWindow : Window
{
    private bool _ready;
    private bool _closing;
    private bool _repositoryActionPending;
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
            if (_closing) return;
            e.Cancel = true; _closing = true;
            // Persistence happens even when the window is still loading; a failure is reported, never fatal.
            Vm.Settings.WindowWidth = Width; Vm.Settings.WindowHeight = Height;
            Vm.Settings.NavigationWidth = Workspace.ColumnDefinitions[0].ActualWidth;
            var problems = await Vm.ShutdownAsync();
            if (problems.Count > 0) Console.Error.WriteLine("Stacker closed with unsaved changes: " + string.Join(" ", problems));
            Close();
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
    /// <summary>Ends popup interaction before starting a repository action on the owning main window.</summary>
    private async Task RunRepositoryActionAsync(Func<Task> action)
    {
        if (_repositoryActionPending || !_ready || _closing || !IsVisible) return;
        _repositoryActionPending = true;
        try
        {
            RecentBox.IsDropDownOpen = false;
            RepositoryButton.Flyout?.Hide();
            ToolTip.SetIsOpen(RepositoryButton, false);
            // macOS flyouts have native popup windows. Let the click and popup teardown finish
            // before presenting an NSOpenPanel sheet, otherwise the popup can retain input/focus.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (!_closing && IsVisible) await action();
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
        finally { _repositoryActionPending = false; }
    }
    /// <summary>Uses this main window as the native picker owner, never the transient repository popup.</summary>
    protected virtual async Task<string?> PickRepositoryFolderAsync()
    {
        if (!StorageProvider.CanPickFolder) throw new InvalidOperationException("Folder selection is unavailable. Enter the repository path instead.");
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Open Git repository", AllowMultiple = false });
        try { return folders.Count > 0 ? folders[0].TryGetLocalPath() : null; }
        finally { foreach (var folder in folders) folder.Dispose(); }
    }
    private async void OpenFolderClick(object? sender, RoutedEventArgs e) => await RunRepositoryActionAsync(async () =>
    {
        var path = await PickRepositoryFolderAsync();
        if (path is not null && !_closing && IsVisible) await Vm.OpenRepositoryAsync(path);
    });
    private async void OpenDemoClick(object? sender, RoutedEventArgs e) => await RunRepositoryActionAsync(Vm.OpenDemoAsync);
    private async void PathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var path = Vm.RepositoryPath;
        await RunRepositoryActionAsync(() => Vm.OpenRepositoryAsync(path));
    }
    private async void RecentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready && RecentBox.SelectedItem is string path)
        {
            RecentBox.SelectedItem = null;
            await RunRepositoryActionAsync(() => Vm.OpenRepositoryAsync(path));
        }
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
    /// <summary>Opens the restack preview for a stack chosen in the sidebar or the active comparison.</summary>
    private async void RestackClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StackGroupViewModel group }) await Vm.SelectGroupAsync(group);
        await ShowSyncWindowAsync();
    }
    private async Task ShowSyncWindowAsync()
    {
        try
        {
            var plan = await Vm.BuildRestackPlanAsync();
            if (plan is null) return;
            var undoAvailable = Vm.LocalGroups.FirstOrDefault(g => g.Definition.Id == plan.StackId)?.UndoAvailable == true;
            await new SyncPreviewWindow(plan, Vm, undoAvailable).ShowDialog(this);
            await Vm.RefreshSyncAvailabilityAsync();
            await Vm.RefreshWorkingTreeAsync();
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
    }
    private async void UndoRestackClick(object? sender, RoutedEventArgs e) => await Vm.UndoAsync();
    /// <summary>Opens the conflicted files in the editor the user configured.</summary>
    private async void ResolveConflictsClick(object? sender, RoutedEventArgs e) => await Vm.OpenConflictsInEditorAsync();
    /// <summary>Opens the sync window, whose conflict panel covers Continue, Skip and Abort.</summary>
    private async void ReviewConflictsClick(object? sender, RoutedEventArgs e) => await ShowSyncWindowAsync();
    /// <summary>Restores what an interrupted run moved; the result is always reported.</summary>
    private async void RestoreRecoveryClick(object? sender, RoutedEventArgs e)
    {
        var (ok, message) = await Vm.RestoreRecoveryAsync();
        if (ok) Vm.Status = message; else Vm.Error = message;
        await Vm.RefreshWorkingTreeAsync();
    }
    /// <summary>Dismisses the interrupted run without touching the branches, and says so.</summary>
    private async void DismissRecoveryClick(object? sender, RoutedEventArgs e)
    {
        var (ok, message) = await Vm.DismissRecoveryAsync();
        if (ok) Vm.Status = message; else Vm.Error = message;
    }
    /// <summary>Commits the working tree, staging everything when the user leaves the box ticked.</summary>
    private async void CommitClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Control { DataContext: StackGroupViewModel group }) await Vm.SelectGroupAsync(group);
            await Vm.RefreshWorkingTreeAsync();
            await new CommitWindow(Vm).ShowDialog<bool>(this);
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
    }
    /// <summary>Creates a new layer branch on top of a local stack and adds it to the definition.</summary>
    private async void AddLayerClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: StackGroupViewModel group }) return;
        try
        {
            await Vm.SelectGroupAsync(group);
            await new AddLayerWindow(group.Definition, Vm.AddLayerAsync).ShowDialog<bool>(this);
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
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
            var editor = new TextBox { Text = Vm.Settings.EditorCommand, Watermark = "Empty uses git core.editor, then $EDITOR" };
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
                    new TextBlock { Text = "Editor for conflicts and commit messages" }, editor,
                    new TextBlock { Text = "Add a wait flag (for example \"code --wait\") if your editor returns immediately.", FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    savedCredentials, background, new TextBlock { Text = "Connection changes are checked automatically.", FontSize = 12 }, new TextBlock { Text = "Theme" }, theme, save
                } }
            };
            save.Click += async (_, _) => { Vm.Settings.GitPath = string.IsNullOrWhiteSpace(git.Text) ? null : git.Text.Trim(); Vm.Settings.GhPath = string.IsNullOrWhiteSpace(gh.Text) ? null : gh.Text.Trim(); Vm.Settings.EditorCommand = string.IsNullOrWhiteSpace(editor.Text) ? null : editor.Text.Trim(); Vm.Settings.UseSavedGhCredentials = savedCredentials.IsChecked == true; Vm.Settings.BackgroundRefresh = background.IsChecked == true; Vm.Settings.Theme = theme.SelectedItem as string ?? "Dark"; await Vm.SaveSettingsAsync(); ApplyTheme(); foreach (var section in Vm.Sections) { section.Theme = Vm.Settings.Theme; await section.LoadAsync(section.SelectedFile); } window.Close(); await Vm.RefreshAllAsync(); };
            await window.ShowDialog(this);
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
    }
}
