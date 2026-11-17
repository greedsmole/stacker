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
    if (_closePhase is ClosePhase.Committing or ClosePhase.ReadyToClose)
        return;
    Width = Vm.Settings.WindowWidth;
    Height = Vm.Settings.WindowHeight;
    ApplyTheme();
    ApplyDiffLineHeight();
    ApplyStacksVisibility();
    Vm.Sections.CollectionChanged += (_, _) => ResizeSections();
    SectionScroll.SizeChanged += (_, _) => ResizeSections();
    _ready = true;
    if (_closePhase != ClosePhase.Open)
        return;
    var args = Environment.GetCommandLineArgs();
    if (args.Contains("--demo"))
        await Vm.OpenDemoAsync();
    else if (args.Length > 1 && !args[1].StartsWith('-'))
        await Vm.OpenRepositoryAsync(args[1]);
    else if (Vm.Settings.LastRepository is { } last)
        await Vm.OpenRepositoryAsync(last);
    if (_closePhase == ClosePhase.Open)
        Vm.SetWindowActive(IsActive);
}, message => AsyncHandler.Report(this, message));
        Closing += (_, e) =>
        {
            if (_closePhase == ClosePhase.ReadyToClose)
                return;
            e.Cancel = true;
            if (_closePhase != ClosePhase.Open)
                return;
            _closePhase = ClosePhase.Preparing;
            AsyncHandler.Run(HandleCloseAsync, message => AsyncHandler.Report(this, message));
        };
    }

    private void ResizeSections()
    {
        foreach (var section in Vm.Sections)
            section.PanelHeight = Vm.Sections.Count == 1 ? Math.Max(300, SectionScroll.Bounds.Height - 12) : 440;
    }
    private void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = Vm.Settings.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
    }
    private void ChangesTabClick(object? sender, RoutedEventArgs e) => Vm.IsGitLogOpen = false;
    private void GitLogsTabClick(object? sender, RoutedEventArgs e) => Vm.IsGitLogOpen = true;
    private void FindClick(object? sender, RoutedEventArgs e) => OpenFind("Files");
    private void OpenFind(string scope)
    {
        Vm.OpenSearch(scope);
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
    private void SearchShortcut(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
            return;
        if (e.Key == Key.P)
        {
            e.Handled = true;
            OpenFind("Files");
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            OpenFind("Changed lines");
        }
    }
    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = Vm.SearchAsync();
        }
        else if (e.Key == Key.Escape)
            Vm.CloseSearch();
    }
    private void CloseSearchClick(object? sender, RoutedEventArgs e) => Vm.CloseSearch();
    private void DismissErrorClick(object? sender, RoutedEventArgs e) => Vm.Error = "";
    private void SearchResultSelected(object? sender, SelectionChangedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (sender is not ListBox { SelectedItem: ChangeSearchHit hit } list)
        return;
    list.SelectedItem = null;
    await Vm.OpenSearchHitAsync(hit);
    var view = SectionScroll.GetVisualDescendants().OfType<DiffSectionView>().FirstOrDefault(v => ReferenceEquals(v.DataContext, hit.Section));
    if (view is not null)
    {
        view.BringIntoView();
        if (hit.Line is not null)
            view.ScrollTo(hit.Line);
    }
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
        if (_repositoryActionPending || !_ready || _closePhase != ClosePhase.Open || !IsVisible)
            return;
        _repositoryActionPending = true;
        try
        {
            RecentBox.IsDropDownOpen = false;
            RepositoryButton.Flyout?.Hide();
            ToolTip.SetIsOpen(RepositoryButton, false);
            // macOS flyouts have native popup windows. Let the click and popup teardown finish
            // before presenting an NSOpenPanel sheet, otherwise the popup can retain input/focus.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (_closePhase == ClosePhase.Open && IsVisible)
                await action();
        }
        catch (Exception ex) { Vm.Error = ex.Message; }
        finally { _repositoryActionPending = false; }
    }
    /// <summary>Uses this main window as the native picker owner, never the transient repository popup.</summary>
    protected virtual async Task<string?> PickRepositoryFolderAsync()
    {
        if (!StorageProvider.CanPickFolder)
            throw new InvalidOperationException("Folder selection is unavailable. Enter the repository path instead.");
        var folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = "Open Git repository",
            AllowMultiple = false
        });
        try
        {
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        finally { foreach (var folder in folders) folder.Dispose(); }
    }
    private void OpenFolderClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () =>
    {
        await RunRepositoryActionAsync(async () =>
    {
        var path = await PickRepositoryFolderAsync();
        if (path is not null && _closePhase == ClosePhase.Open && IsVisible)
            await Vm.OpenRepositoryAsync(path);
    });
    }, message => AsyncHandler.Report(this, message));
    private void OpenDemoClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await RunRepositoryActionAsync(Vm.OpenDemoAsync); }, message => AsyncHandler.Report(this, message));
    private void PathKeyDown(object? sender, KeyEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if (e.Key != Key.Enter)
        return;
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
    {
        if (group.IsSinglePr)
            await Vm.SelectLayerAsync(group.Layers[0], false);
        else
            await Vm.SelectGroupAsync(group);
    }
}, message => AsyncHandler.Report(this, message));
    private void LayerClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { if (sender is Control { DataContext: LayerViewModel layer }) await Vm.SelectLayerAsync(layer, false); }, message => AsyncHandler.Report(this, message));
    private void LayerPointerPressed(object? sender, PointerPressedEventArgs e)
=> AsyncHandler.Run(async () =>
{
    if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0 && sender is Control { DataContext: LayerViewModel layer })
    {
        e.Handled = true;
        await Vm.SelectLayerAsync(layer, true);
    }
}, message => AsyncHandler.Report(this, message));
    private void LayerChecked(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { if (sender is Control { DataContext: LayerViewModel layer }) await Vm.SelectLayerAsync(layer, true); }, message => AsyncHandler.Report(this, message));
    private void ThisLayerClick(object? sender, RoutedEventArgs e) => Vm.Mode = Stacker.Core.DiffMode.Layer;
    private void ThroughLayerClick(object? sender, RoutedEventArgs e) => Vm.Mode = Stacker.Core.DiffMode.Cumulative;
    private void SaveAsLocalClick(object? sender, RoutedEventArgs e) => AsyncHandler.Run(async () => { await Vm.SaveRemoteAsLocalAsync(); }, message => AsyncHandler.Report(this, message));
}
