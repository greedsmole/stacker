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
    private enum ClosePhase { Open, Preparing, AwaitingDecision, SavingCopy, Committing, ReadyToClose }
    private ClosePhase _closePhase;
    private bool _repositoryActionPending;
    private MainViewModel Vm => (MainViewModel)DataContext!;
    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => { if (_ready && _closePhase == ClosePhase.Open) Vm.SetWindowActive(true); };
        KeyDown += SearchShortcut;
        Deactivated += (_, _) => { if (_ready) Vm.SetWindowActive(false); };
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 1320;
            Grid.SetColumn(ReviewHost, compact ? 2 : 3);
            ReviewHost.Width = compact ? double.NaN : 390;
            ReviewHost.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        };
        Opened += (_, _) =>
AsyncHandler.Run(async () =>
{
    await Vm.InitializeAsync();
    if (_closePhase is ClosePhase.Committing or ClosePhase.ReadyToClose) return;
    Width = Vm.Settings.WindowWidth; Height = Vm.Settings.WindowHeight;
    ApplyTheme();
    ApplyDiffLineHeight();
    ApplyStacksVisibility();
    Vm.Sections.CollectionChanged += (_, _) => ResizeSections();
    SectionScroll.SizeChanged += (_, _) => ResizeSections();
    _ready = true;
    if (_closePhase != ClosePhase.Open) return;
    var args = Environment.GetCommandLineArgs();
    if (args.Contains("--demo")) await Vm.OpenDemoAsync();
    else if (args.Length > 1 && !args[1].StartsWith('-')) await Vm.OpenRepositoryAsync(args[1]);
    else if (Vm.Settings.LastRepository is { } last) await Vm.OpenRepositoryAsync(last);
    if (_closePhase == ClosePhase.Open) Vm.SetWindowActive(IsActive);
}, message => AsyncHandler.Report(this, message));
        Closing += (_, e) =>
        {
            if (_closePhase == ClosePhase.ReadyToClose) return;
            e.Cancel = true;
            if (_closePhase != ClosePhase.Open) return;
            _closePhase = ClosePhase.Preparing;
            AsyncHandler.Run(HandleCloseAsync, message => AsyncHandler.Report(this, message));
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
    private void DismissErrorClick(object? sender, RoutedEventArgs e) => Vm.Error = "";
    private void SearchResultSelected(object? sender, SelectionChangedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (sender is not ListBox { SelectedItem: ChangeSearchHit hit } list) return;
    list.SelectedItem = null;
    await Vm.OpenSearchHitAsync(hit);
    var view = SectionScroll.GetVisualDescendants().OfType<DiffSectionView>().FirstOrDefault(v => ReferenceEquals(v.DataContext, hit.Section));
    if (view is not null) { view.BringIntoView(); if (hit.Line is not null) view.ScrollTo(hit.Line); }
    Vm.CloseSearch();
}, message => AsyncHandler.Report(this, message));
    private void CopyGitLogClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    try
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(string.Join("\n\n", Vm.VisibleGitLogEntries.Select(entry =>
                string.Join("\n", new[] { entry.Summary, entry.WorkingDirectory, entry.Detail }.Where(line => line.Length > 0)))));
    }
    catch (Exception ex) { Vm.Error = "Cannot copy the command log. " + ex.Message; }
}, message => AsyncHandler.Report(this, message));
    /// <summary>Ends popup interaction before starting a repository action on the owning main window.</summary>
    private async Task RunRepositoryActionAsync(Func<Task> action)
    {
        if (_repositoryActionPending || !_ready || _closePhase != ClosePhase.Open || !IsVisible) return;
        _repositoryActionPending = true;
        try
        {
            RecentBox.IsDropDownOpen = false;
            RepositoryButton.Flyout?.Hide();
            ToolTip.SetIsOpen(RepositoryButton, false);
            // macOS flyouts have native popup windows. Let the click and popup teardown finish
            // before presenting an NSOpenPanel sheet, otherwise the popup can retain input/focus.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (_closePhase == ClosePhase.Open && IsVisible) await action();
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
    private void OpenFolderClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        await RunRepositoryActionAsync(async () =>
    {
        var path = await PickRepositoryFolderAsync();
        if (path is not null && _closePhase == ClosePhase.Open && IsVisible) await Vm.OpenRepositoryAsync(path);
    });
    }, message => AsyncHandler.Report(this, message));
    private void OpenDemoClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await RunRepositoryActionAsync(Vm.OpenDemoAsync); }, message => AsyncHandler.Report(this, message));
    private void PathKeyDown(object? sender, KeyEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (e.Key != Key.Enter) return;
    e.Handled = true;
    var path = Vm.RepositoryPath;
    await RunRepositoryActionAsync(() => Vm.OpenRepositoryAsync(path));
}, message => AsyncHandler.Report(this, message));
    private void RecentChanged(object? sender, SelectionChangedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (_ready && RecentBox.SelectedItem is string path)
    {
        RecentBox.SelectedItem = null;
        await RunRepositoryActionAsync(() => Vm.OpenRepositoryAsync(path));
    }
}, message => AsyncHandler.Report(this, message));
    private void StackClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (sender is Control { DataContext: StackGroupViewModel group })
    { if (group.IsSinglePr) await Vm.SelectLayerAsync(group.Layers[0], false); else await Vm.SelectGroupAsync(group); }
}, message => AsyncHandler.Report(this, message));
    private void LayerClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { if (sender is Control { DataContext: LayerViewModel layer }) await Vm.SelectLayerAsync(layer, false); }, message => AsyncHandler.Report(this, message));
    private void LayerPointerPressed(object? sender, PointerPressedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0 && sender is Control { DataContext: LayerViewModel layer }) { e.Handled = true; await Vm.SelectLayerAsync(layer, true); }
}, message => AsyncHandler.Report(this, message));
    private void LayerChecked(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { if (sender is Control { DataContext: LayerViewModel layer }) await Vm.SelectLayerAsync(layer, true); }, message => AsyncHandler.Report(this, message));
    private void ThisLayerClick(object? sender, RoutedEventArgs e) => Vm.Mode = Stacker.Core.DiffMode.Layer;
    private void ThroughLayerClick(object? sender, RoutedEventArgs e) => Vm.Mode = Stacker.Core.DiffMode.Cumulative;
    private void SaveAsLocalClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await Vm.SaveRemoteAsLocalAsync(); }, message => AsyncHandler.Report(this, message));
    private void HelpClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    var text = "For main → A → B → C:\n\nEntire stack: main → C. Click Entire stack in the sidebar.\n\nThis layer: A → B. Click layer 2.\n\nThrough this layer: main → B. Includes layers 1 and 2. At layer 3 it equals Entire stack.\n\nSelected layers: check 1 and 3. Two separate comparisons: main → A and B → C. Layer 2 is not folded into layer 3.\n\nComparisons start from the common ancestor. Diverged branches show a warning.\n\nTry Open demo: Authorization changes the same file across three layers; Payments is independent. Shared foundation demonstrates branching PRs. Service branches demonstrate discovery boundaries.\n\nStack titles restore your last view. GitHub updates quietly every minute while this window is active. New code waits for Update changes; returning to the window does not reload it.";
    var help = new Window { Title = "How comparisons work", Width = 590, Height = 560, MinWidth = 460, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    var close = new Button { Content = "Close", IsCancel = true }; close.Click += (_, _) => help.Close();
    help.Content = DialogLayout.Create("How comparisons work", "Every layer is compared with its own parent.", new Control[] { new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } }, close);
    await help.ShowDialog(this);
}, message => AsyncHandler.Report(this, message));
    private void GitHubSettingsClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (Vm.Repository is null) return;
    var origin = new TextBox { Text = Vm.GitHubPreferences.GitHubOverride, Watermark = "Auto-detect origin, or host/owner/repo" };
    var boundaries = new TextBox { Text = string.Join("\n", Vm.GitHubPreferences.BoundaryBranches ?? []), AcceptsReturn = true, MinHeight = 130 };
    Avalonia.Automation.AutomationProperties.SetName(origin, "GitHub repository override");
    Avalonia.Automation.AutomationProperties.SetName(boundaries, "Boundary branches");
    var save = new Button { Content = "Save and refresh", Classes = { "primary" } };
    var cancel = new Button { Content = "Cancel", IsCancel = true };
    var clear = new Button { Content = "Clear PR retention references" };
    var window = new Window
    {
        Title = "GitHub settings",
        Width = 570,
        Height = 520,
        MinWidth = 480,
        MinHeight = 400,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = DialogLayout.Create("GitHub connection", "Choose the repository and where stacks begin.", new Control[] {
                new TextBlock { Text = "GitHub repository override" }, origin,
                new TextBlock { Text = "Stack boundaries · one exact branch name per line" }, boundaries,
                new TextBlock { Text = "PRs into these branches can start stacks; links do not cross them.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, clear }, cancel, save)
    };
    cancel.Click += (_, _) => window.Close();
    save.Click += (_, _) => AsyncHandler.Run(async () => { await Vm.SaveGitHubPreferencesAsync(new() { GitHubOverride = string.IsNullOrWhiteSpace(origin.Text) ? null : origin.Text.Trim(), BoundaryBranches = (boundaries.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList() }); window.Close(); }, message => AsyncHandler.Report(this, message));
    clear.Click += (_, _) => AsyncHandler.Run(async () => { await Vm.ClearGitHubCacheAsync(); }, message => AsyncHandler.Report(this, message));
    await window.ShowDialog(this);
}, message => AsyncHandler.Report(this, message));
    /// <summary>Opens the restack preview for a stack chosen in the sidebar or the active comparison.</summary>
    private void RestackClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (sender is Control { DataContext: StackGroupViewModel group }) await Vm.SelectGroupAsync(group);
    await ShowSyncWindowAsync();
}, message => AsyncHandler.Report(this, message));
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
    private void UndoRestackClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await Vm.UndoAsync(); }, message => AsyncHandler.Report(this, message));
    /// <summary>Opens the conflicted files in the editor the user configured.</summary>
    private void ResolveConflictsClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await Vm.OpenConflictsInEditorAsync(); }, message => AsyncHandler.Report(this, message));
    /// <summary>Opens the sync window, whose conflict panel covers Continue, Skip and Abort.</summary>
    private void ReviewConflictsClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await ShowSyncWindowAsync(); }, message => AsyncHandler.Report(this, message));
    /// <summary>Restores what an interrupted run moved; the result is always reported.</summary>
    private void RestoreRecoveryClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    var (ok, message) = await Vm.RestoreRecoveryAsync();
    if (ok) Vm.Status = message; else Vm.Error = message;
    await Vm.RefreshWorkingTreeAsync();
}, message => AsyncHandler.Report(this, message));
    /// <summary>Dismisses the interrupted run without touching the branches, and says so.</summary>
    private void DismissRecoveryClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    var (ok, message) = await Vm.DismissRecoveryAsync();
    if (ok) Vm.Status = message; else Vm.Error = message;
}, message => AsyncHandler.Report(this, message));
    /// <summary>Commits the working tree, staging everything when the user leaves the box ticked.</summary>
    private void CommitClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    try
    {
        if (sender is Control { DataContext: StackGroupViewModel group }) await Vm.SelectGroupAsync(group);
        await Vm.RefreshWorkingTreeAsync();
        await new CommitWindow(Vm).ShowDialog<bool>(this);
    }
    catch (Exception ex) { Vm.Error = ex.Message; }
}, message => AsyncHandler.Report(this, message));
    /// <summary>Creates a new layer branch on top of a local stack and adds it to the definition.</summary>
    private void AddLayerClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (sender is not Control { DataContext: StackGroupViewModel group }) return;
    try
    {
        await Vm.SelectGroupAsync(group);
        await new AddLayerWindow(group.Definition, Vm.AddLayerAsync).ShowDialog<bool>(this);
    }
    catch (Exception ex) { Vm.Error = ex.Message; }
}, message => AsyncHandler.Report(this, message));
    private void NewStackClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await EditAsync(false); }, message => AsyncHandler.Report(this, message));
    private void EditStackClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await EditAsync(true); }, message => AsyncHandler.Report(this, message));
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
    private void PrCommentsClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await Vm.ShowReviewAsync(tab: 0); }, message => AsyncHandler.Report(this, message));
    private void ReviewPrClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (Vm.Sections.FirstOrDefault() is { IsPrSnapshot: false, HasPr: true } section) await Vm.OpenPrChangesAsync(section);
    await Vm.ShowReviewAsync();
}, message => AsyncHandler.Report(this, message));
    private void OverviewClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () => { if (sender is Control { DataContext: StackGroupViewModel group }) await Vm.SelectOverviewAsync(group); }, message => AsyncHandler.Report(this, message));
    private void EditGroupClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () => { if (sender is Control { DataContext: StackGroupViewModel group }) { await Vm.SelectGroupAsync(group); await EditAsync(true); } }, message => AsyncHandler.Report(this, message));
    private void SaveGroupClick(object? sender, RoutedEventArgs e)
=> AsyncHandler.Run(async () => { if (sender is Control { DataContext: StackGroupViewModel group }) { await Vm.SelectGroupAsync(group); await Vm.SaveRemoteAsLocalAsync(); } }, message => AsyncHandler.Report(this, message));
}
