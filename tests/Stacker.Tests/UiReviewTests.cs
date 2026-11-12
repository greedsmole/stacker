using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed class UiReviewTests
{
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task Changed_files_can_be_browsed_by_folder_and_show_status_in_both_themes(string theme)
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        await fixture.Write("src/auth/Token.cs", "old token\n");
        await fixture.Write("src/legacy/Legacy.cs", "delete this\n");
        await fixture.Write("src/previous/Config.json", "{\"stable\": true}\n");
        await fixture.Commit("Original files");
        var basis = await fixture.Git("rev-parse", "HEAD");

        await fixture.Write("src/auth/Token.cs", "new token\n");
        await fixture.Write("src/auth/Session.cs", "added session\n");
        await fixture.Write("README.md", "Project\n");
        File.Delete(Path.Combine(fixture.Root, "src/legacy/Legacy.cs"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src/settings"));
        await fixture.Git("mv", "src/previous/Config.json", "src/settings/Config.json");
        await fixture.Commit("Mixed changes");
        var head = await fixture.Git("rev-parse", "HEAD");
        var files = await fixture.Reader.FilesAsync(fixture.Root, basis, head);
        var modified = Assert.Single(files, file => file.Kind == FileChangeKind.Modified);
        var renamed = Assert.Single(files, file => file.Kind == FileChangeKind.Renamed);

        using var section = new DiffSectionViewModel(fixture.Reader, fixture.Root, new("feature", "main", basis, head, null, files),
            new TextMateSyntaxHighlighter(), theme, selectFirstFile: false);
        section.SelectedFile = modified;
        await Until(() => !section.IsBusy && section.Patch.Contains("new token", StringComparison.Ordinal));
        var initialPatch = section.Patch;
        var view = new DiffSectionView { DataContext = section };
        var window = new Window { Content = view, Width = 820, Height = 440,
            RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show();
        try
        {
            var flat = view.FindControl<ListBox>("FlatFiles")!;
            var tree = view.FindControl<TreeView>("FolderFiles")!;
            var toggle = view.FindControl<ToggleButton>("FileFoldersToggle")!;
            Capture(window, theme, "files-list");
            Assert.True(flat.IsEffectivelyVisible);
            Assert.Same(section.SelectedFileRow, flat.SelectedItem);
            Assert.False(tree.IsEffectivelyVisible);

            var statuses = new[]
            {
                (FileChangeKind.Added, "A", "#8BD7A0", "#276642"),
                (FileChangeKind.Modified, "M", "#91C7FF", "#005A9E"),
                (FileChangeKind.Deleted, "D", "#FF9FA6", "#A6273A"),
                (FileChangeKind.Renamed, "R", "#D1B8FC", "#673D9A")
            };
            foreach (var (kind, letter, dark, light) in statuses)
            {
                var badge = flat.GetVisualDescendants().OfType<FileChangeBadge>().First(control => control.Change?.Kind == kind);
                Assert.Equal(letter, Assert.IsType<TextBlock>(badge.Child).Text);
                Assert.Equal(Color.Parse(theme == "Light" ? light : dark), Assert.IsAssignableFrom<ISolidColorBrush>(badge.BorderBrush).Color);
                Assert.Contains(kind.ToString(), Assert.IsType<string>(ToolTip.GetTip(badge)));
            }

            ClickAt(window, toggle);
            window.UpdateLayout();
            Assert.True(tree.IsEffectivelyVisible);
            Assert.False(flat.IsEffectivelyVisible);
            Assert.Equal(initialPatch, section.Patch);
            Assert.Same(modified, section.SelectedFile);
            Assert.Same(section.SelectedFolderFile, tree.SelectedItem);
            Assert.Equal(new[] { "src", "README.md" }, section.FolderFiles.Select(node => node.Name));
            var src = section.FolderFiles[0];
            Assert.True(src.IsExpanded);
            Assert.Equal(new[] { "auth", "legacy", "settings" }, src.Children.Select(node => node.Name));
            Assert.True(src.Children[0].IsExpanded);
            var legacy = src.Children[1];
            var settings = src.Children[2];

            var folderRow = TreeRow(legacy);
            Assert.True(folderRow.Focus());
            Press(window, Key.Right);
            Assert.True(legacy.IsExpanded);
            Assert.Same(modified, section.SelectedFile);
            Press(window, Key.Left);
            Assert.False(legacy.IsExpanded);
            legacy.IsExpanded = settings.IsExpanded = true;
            Capture(window, theme, "files-folders");
            var renamedNode = Assert.Single(settings.Children);
            Assert.Equal("Config.json", renamedNode.Name);
            Assert.Equal("src/previous/Config.json → src/settings/Config.json", renamedNode.Path);
            ClickAt(window, TreeRow(renamedNode));
            await Until(() => !section.IsBusy && ReferenceEquals(renamed, section.SelectedFile));
            Assert.Same(renamed, section.SelectedFile);
            Assert.Contains("rename to src/settings/Config.json", section.Patch);

            var filter = view.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "Filter files…");
            filter.Text = "Config.json";
            window.UpdateLayout();
            var match = Assert.Single(Assert.Single(Assert.Single(section.FolderFiles).Children).Children);
            Assert.Same(renamed, match.Change);
            Assert.Same(match, tree.SelectedItem);
            filter.Text = "no-such-file";
            Assert.Empty(section.FolderFiles);
            Assert.True(section.HasNoFileMatches);
            filter.Text = "";
            Assert.True(legacy.IsExpanded);
            section.SelectedFile = modified;
            ClickAt(window, toggle);
            Assert.True(flat.IsEffectivelyVisible);
            Assert.Same(section.SelectedFileRow, flat.SelectedItem);

            TreeViewItem TreeRow(FileTreeNode node) =>
                Assert.Single(tree.GetVisualDescendants().OfType<TreeViewItem>(), item => ReferenceEquals(item.DataContext, node));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Light", "\n")]
    [InlineData("Dark", "\r\n")]
    public async Task Collapsed_diff_context_expands_in_place_with_exact_line_numbers(string theme, string newline)
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        const string path = "Numbers.cs";
        var original = Enumerable.Range(1, 110).Select(number => $"const int value{number} = {number};").ToArray();
        original[33] = "";
        await fixture.Write(path, string.Join(newline, original));
        await fixture.Commit("Original file");
        var basis = await fixture.Git("rev-parse", "HEAD");
        var changed = original.ToList();
        changed[34] = "const int value35 = 350;";
        changed.Insert(35, "// The next line keeps its original value.");
        changed[80] = "const int value80 = 800;";
        await fixture.Write(path, string.Join(newline, changed));
        await fixture.Commit("Two separate edits");
        var head = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("config", "diff.suppressBlankEmpty", "true");
        await fixture.Write(path, "// local edit");

        var files = await fixture.Reader.FilesAsync(fixture.Root, basis, head);
        var file = Assert.Single(files);
        using var section = new DiffSectionViewModel(fixture.Reader, fixture.Root, new("example", "main", basis, head, null, files),
            new TextMateSyntaxHighlighter(), theme, readOnly: theme == "Dark", selectFirstFile: false);
        section.SelectedFile = file;
        await Until(() => !section.IsBusy && section.Lines.Any(line => line.NewLineNumber == 32 && line.Tokens.Count > 0));
        var patch = section.Patch;
        var patchRows = section.Lines.Where(line => !line.IsCollapsedContext).ToArray();
        var gaps = section.Lines.Where(line => line.IsCollapsedContext).ToArray();
        Assert.Equal(new[] { 31, 38, 27 }, gaps.Select(line => line.ContextGap!.Count));
        Assert.Contains(patchRows, line => line.Kind == DiffLineKind.Context && line.NewLineNumber == 34);

        var view = new DiffSectionView { DataContext = section, ShowFileNavigator = false };
        var window = new Window { Content = view, Width = 788, Height = 470,
            RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show();
        try
        {
            var list = view.FindControl<ListBox>("DiffLines")!;
            Capture(window, theme, "diff-context-collapsed");
            await ClickGap(gaps[0], "Show 20 lines above · 31 hidden");
            Assert.Equal(11, gaps[0].ContextGap!.Count);
            Assert.Equal(12, section.Lines.First(line => line.IsExpandedContext).NewLineNumber);
            Assert.NotEmpty(section.Lines.First(line => line.IsExpandedContext).Tokens);
            await ClickGap(gaps[1], "↑");
            Assert.Equal(18, gaps[1].ContextGap!.Count);
            Assert.False(gaps[1].ShowExpandUp);
            Assert.Contains(section.Lines, line => line.IsExpandedContext && line.OldLineNumber == 57 && line.NewLineNumber == 58);
            await ClickGap(gaps[2], "Show 20 lines below · 27 hidden");
            Assert.Equal(7, gaps[2].ContextGap!.Count);
            Assert.Contains(section.Lines, line => line.IsExpandedContext && line.OldLineNumber == 84 && line.NewLineNumber == 85);
            Capture(window, theme, "diff-context-expanded");
            var visibleContext = view.GetVisualDescendants().OfType<Grid>().Where(row => row.Name == "CodeRow" &&
                row.IsEffectivelyVisible && row.DataContext is RenderedDiffLine { IsExpandedContext: true }).ToArray();
            Assert.NotEmpty(visibleContext);
            var lineHeight = Assert.IsType<double>(Application.Current!.Resources["DiffLineHeight"]);
            Assert.All(visibleContext, row => Assert.Equal(lineHeight, row.Bounds.Height));
            await ClickGap(gaps[2], "Show 7 unchanged lines");
            Assert.DoesNotContain(gaps[2], section.Lines);

            section.WrapCode = true;
            ClickFileAction("Show full file");
            await Until(() => !section.HasCollapsedContext);
            Assert.Equal(Enumerable.Range(1, changed.Count), section.Lines.Where(line => line.NewLineNumber.HasValue).Select(line => line.NewLineNumber!.Value));
            Assert.Equal(Enumerable.Range(1, original.Length), section.Lines.Where(line => line.OldLineNumber.HasValue).Select(line => line.OldLineNumber!.Value));
            Assert.Equal(changed, section.Lines.Where(line => line.NewLineNumber.HasValue).Select(line => line.Text.TrimEnd('\r')));
            Assert.All(section.Lines.Where(line => line.IsExpandedContext), line =>
            {
                Assert.False(line.CanComment);
                Assert.Equal(Avalonia.Media.TextWrapping.Wrap, line.TextWrapping);
            });
            Assert.Equal(patch, section.Patch);

            ClickFileAction("Collapse unchanged lines");
            Assert.False(section.HasExpandedContext);
            Assert.Equal(patchRows, section.Lines.Where(line => !line.IsCollapsedContext));
            Assert.Equal(new[] { 31, 38, 27 }, section.Lines.Where(line => line.IsCollapsedContext).Select(line => line.ContextGap!.Count));

            async Task ClickGap(RenderedDiffLine gap, string label)
            {
                await Dispatcher.UIThread.InvokeAsync(() => list.ScrollIntoView(gap), DispatcherPriority.Background);
                Point? position = null;
                await Until(() =>
                {
                    window.UpdateLayout();
                    var button = view.GetVisualDescendants().OfType<Button>().SingleOrDefault(item =>
                        ReferenceEquals(item.DataContext, gap) && item.IsEffectivelyVisible && Equals(item.Content, label));
                    position = button?.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
                    return position is { Y: >= 0 } point && point.Y < window.Bounds.Height;
                });
                var pointer = position!.Value;
                window.MouseMove(pointer);
                window.MouseDown(pointer, MouseButton.Left);
                window.MouseUp(pointer, MouseButton.Left);
            }

            void ClickFileAction(string label)
            {
                var actions = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "···"));
                var flyout = Assert.IsType<MenuFlyout>(actions.Flyout);
                flyout.ShowAt(actions);
                var item = Assert.Single(flyout.Items.OfType<MenuItem>(), item => Equals(item.Header, label));
                Assert.True(item.IsVisible);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                flyout.Hide();
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task Secondary_screens_fit_and_keep_actions_available(string theme)
    {
        var root = Path.Combine(Path.GetTempPath(), "stacker-ui-" + Guid.NewGuid().ToString("N"));
        using var services = Composition.Build(root);
        var vm = services.GetRequiredService<MainViewModel>();
        vm.Settings.Theme = theme; vm.Settings.WindowWidth = 1060; vm.Settings.WindowHeight = 640;
        await vm.SaveSettingsAsync();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await Until(() => (bool)typeof(MainWindow).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        Application.Current!.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        window.Width = 1060; window.Height = 640;
        try
        {
            Capture(window, theme, "empty");
            vm.IsBusy = true; Assert.False(vm.ShowEmptyState); Capture(window, theme, "loading"); vm.IsBusy = false;
            vm.OpenSearch("Files"); vm.SearchQuery = "Auth"; await vm.SearchAsync();
            Assert.Contains("Select a stack", vm.SearchStatus); Capture(window, theme, "search-no-comparison"); vm.CloseSearch();
            await vm.OpenDemoAsync();
            await vm.SelectGroupAsync(vm.LocalGroups[0]);
            vm.OpenSearch("Files"); vm.SearchQuery = "Auth"; await vm.SearchAsync();
            Assert.NotEmpty(vm.SearchResults); Capture(window, theme, "search-results");
            vm.SearchQuery = "no-match-unique"; await vm.SearchAsync(); Assert.Contains("No matches", vm.SearchStatus); Capture(window, theme, "search-empty");
            vm.SearchRegex = true; vm.SearchQuery = "["; await vm.SearchAsync(); Capture(window, theme, "search-invalid-regex"); vm.CloseSearch();
            vm.Error = "Cannot open the selected repository. The directory is unavailable. Reconnect its drive or use Open repository to choose its current location.\n" + string.Join("\n", Enumerable.Repeat("Git diagnostic details remain selectable and scrollable without hiding workspace actions.", 10));
            Capture(window, theme, "error"); Click(window, "Dismiss"); Assert.False(vm.HasError);
            await vm.ShowHistoryAsync(); Assert.NotNull(vm.ActiveHistory);
            await Until(() => !vm.IsBusy && !vm.ActiveHistory!.IsBusy && !vm.ActiveHistory.LoadLayerCommand.IsRunning);
            var history = vm.ActiveHistory!;
            Assert.NotEmpty(history.ActiveDocumentSection?.Lines ?? throw new InvalidOperationException("History did not open the current diff."));
            Capture(window, theme, "history");
            var historyPanel = window.GetLogicalDescendants().OfType<HistoryPanel>().Single();
            var diffHost = historyPanel.FindControl<ContentControl>("HistoryDiff")!;
            var diffView = Assert.Single(diffHost.GetVisualDescendants().OfType<DiffSectionView>());
            Assert.False(diffView.FindControl<Grid>("FilePane")!.IsVisible);
            var viewport = diffView.FindControl<Grid>("DiffViewport")!;
            Assert.True(viewport.Bounds.Width >= 760 && viewport.Bounds.Height >= 360, $"History diff viewport: {viewport.Bounds.Size}");
            Assert.False(historyPanel.FindControl<ComboBox>("ReviewFiles")!.IsVisible);
            Assert.True(historyPanel.FindControl<ComboBox>("HistoryDiffFiles")!.IsVisible);
            Capture(window, theme, "history-document");
            var tools = historyPanel.FindControl<Button>("HistoryToolsButton")!;
            var flyout = Assert.IsType<Flyout>(tools.Flyout);
            flyout.ShowAt(tools);
            var flyoutBody = Assert.IsType<ScrollViewer>(flyout.Content);
            Assert.Same(history, flyoutBody.DataContext);
            foreach (var expander in flyoutBody.GetLogicalDescendants().OfType<Expander>()) expander.IsExpanded = true;
            Capture(window, theme, "history-tools");
            flyout.Hide();
            await vm.SetWrapCodeAsync(true);
            Assert.True(history.ActiveDocumentSection!.WrapCode);
            await history.ShowCurrentCommand.ExecuteAsync(null);
            Assert.True(history.ActiveDocumentSection!.WrapCode);
            await vm.SetWrapCodeAsync(false);
            history.SelectedNode = null;
            Assert.False(history.MarkReviewedCommand.CanExecute(null));
            Assert.False(history.ShowCurrentCommand.CanExecute(null));
            Assert.False(history.CompareVersionsCommand.CanExecute(null));
            Assert.Null(history.SelectedFile); Assert.Null(history.PreviousVersion); Assert.Null(history.ActiveDocumentSection);
            Assert.Contains("Select a layer", history.Message);
            Capture(window, theme, "history-empty"); vm.CloseHistoryCommand.Execute(null);
            await CaptureDialog(window, "SettingsClick", theme, "settings");
            await CaptureDialog(window, "GitHubSettingsClick", theme, "github");
            await CaptureDialog(window, "HelpClick", theme, "help");
            await CaptureDialog(window, "RelocateRepositoryClick", theme, "relocate");
            var unsavedChoice = (Task<UnsavedCloseChoice>)typeof(MainWindow).GetMethod("AskUnsavedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { new[] { "The review draft could not be saved. The disk is full.", "Free disk space, then retry saving." } })!;
            await Until(() => window.OwnedWindows.Count > 0);
            var unsaved = window.OwnedWindows.Single(); Minimum(unsaved); Capture(unsaved, theme, "unsaved");
            Press(unsaved, Key.Escape); Assert.Equal(UnsavedCloseChoice.ContinueEditing, await unsavedChoice);
            var stack = vm.LocalGroups[0].Definition;
            var add = new AddLayerWindow(stack, (_, _, _) => Task.FromResult((false, "Not executed in visual review")));
            add.Show(window); Minimum(add); Click(add, "Create layer"); Capture(add, theme, "add-layer-validation"); Assert.Contains("Enter a branch", add.FindControl<TextBlock>("ErrorLabel")!.Text); add.Close();
            var edit = new StackEditorWindow(vm.Repository!, stack, _ => Task.FromResult(false)); edit.Show(window); Minimum(edit); Capture(edit, theme, "stack-editor"); edit.Close();
            await vm.RefreshWorkingTreeAsync();
            var commit = new CommitWindow(vm); commit.Show(window); Minimum(commit); Capture(commit, theme, "commit-clean"); Assert.False(commit.FindControl<Button>("CommitButton")!.IsEnabled); commit.Close();
            await File.AppendAllTextAsync(Path.Combine(vm.Repository!.Root, "Auth.cs"), "\n// visual review fixture\n");
            await vm.RefreshWorkingTreeAsync();
            var dirtyCommit = new CommitWindow(vm); dirtyCommit.Show(window); Minimum(dirtyCommit); await Task.Delay(30); Click(dirtyCommit, "Commit");
            Capture(dirtyCommit, theme, "commit-validation"); Assert.Equal("Enter a commit message.", dirtyCommit.FindControl<TextBlock>("ErrorLabel")!.Text); dirtyCommit.Close();
            var plan = await vm.BuildRestackPlanAsync(); Assert.NotNull(plan);
            var sync = new SyncPreviewWindow(plan!, vm, false); sync.Show(window); Minimum(sync); Capture(sync, theme, "sync-preview"); sync.Close();
        }
        finally { await vm.PrepareCloseAsync(); window.Close(); }
    }
    private static void Minimum(Window window)
    {
        window.Width = window.MinWidth > 0 ? window.MinWidth : Math.Min(window.Width, 600);
        window.Height = window.MinHeight > 0 ? window.MinHeight : Math.Min(window.Height, 560);
    }
    private static void Click(Control root, string label) => root.GetLogicalDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void ClickAt(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseMove(point.Value);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
    }
    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, "");
        if (window.IsVisible) window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, "");
    }
    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 300; i++) { if (predicate()) return; await Task.Delay(20); }
        Assert.True(predicate(), "UI state did not become available within six seconds.");
    }
    private static async Task CaptureDialog(MainWindow owner, string handler, string theme, string name)
    {
        typeof(MainWindow).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, new object?[] { null, new RoutedEventArgs() });
        await Until(() => owner.OwnedWindows.Count > 0);
        var dialog = owner.OwnedWindows.Single(); Minimum(dialog); Capture(dialog, theme, name);
        var body = dialog.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (body is not null) { body.Offset = new Vector(0, 10000); Capture(dialog, theme, name + "-bottom"); }
        Press(dialog, Key.Tab);
        Assert.NotNull(dialog.FocusManager?.GetFocusedElement());
        if (name == "settings") Capture(dialog, theme, name + "-focus");
        Press(dialog, Key.Escape); await Until(() => !dialog.IsVisible);
    }
    private static void Capture(Window window, string theme, string name)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.True(window.Bounds.Width > 0 && window.Bounds.Height > 0);
        var directory = Environment.GetEnvironmentVariable("STACKER_UI_CAPTURE_DIR");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame(); Assert.True(frame is not null, $"No rendered frame for {name}; visible={window.IsVisible}.");
        frame!.Save(Path.Combine(directory, theme + "-" + name + ".png"));
    }
}
