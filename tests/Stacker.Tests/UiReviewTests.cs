using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        var window = new Window
        {
            Content = view,
            Width = 820,
            Height = 440,
            RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark
        };
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
        const string path = "Numbers.razor";
        var original = Enumerable.Range(1, 110).Select(number => $"    private const int value{number} = {number};").ToArray();
        original[0] = "@code {";
        original[^1] = "}";
        original[33] = "";
        await fixture.Write(path, string.Join(newline, original));
        await fixture.Commit("Original file");
        var basis = await fixture.Git("rev-parse", "HEAD");
        var changed = original.ToList();
        changed[34] = "    private const int value35 = 350;";
        changed.Insert(35, "    // The next line keeps its original value.");
        changed[80] = "    private const int value80 = 800;";
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
        Assert.NotEmpty(Assert.Single(patchRows, line => line.IsRemoved && line.OldLineNumber == 35).Tokens);
        Assert.NotEmpty(Assert.Single(patchRows, line => line.IsAdded && line.NewLineNumber == 35).Tokens);

        var view = new DiffSectionView { DataContext = section, ShowFileNavigator = false };
        var window = new Window
        {
            Content = view,
            Width = theme == "Dark" ? 788 : 480,
            Height = 470,
            RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark
        };
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

            var fileActions = OpenFileActions(window, view);
            var collapsePartial = view.FindControl<MenuItem>("CollapseContextButton")!;
            Assert.True(collapsePartial.IsEffectivelyVisible);
            Assert.Equal("Collapse expanded context", collapsePartial.Header);
            fileActions.Hide();

            section.WrapCode = true;
            OpenFileActions(window, view);
            var fullFile = view.FindControl<MenuItem>("FullFileButton")!;
            Assert.Equal("Show full file", fullFile.Header);
            Assert.True(fullFile.IsEffectivelyVisible);
            ClickAt(window, fullFile);
            await Until(() => section.HasExpandedContext && !section.HasCollapsedContext);
            Assert.Equal(Enumerable.Range(1, changed.Count), section.Lines.Where(line => line.NewLineNumber.HasValue).Select(line => line.NewLineNumber!.Value));
            Assert.Equal(Enumerable.Range(1, original.Length), section.Lines.Where(line => line.OldLineNumber.HasValue).Select(line => line.OldLineNumber!.Value));
            Assert.Equal(changed, section.Lines.Where(line => line.NewLineNumber.HasValue).Select(line => line.Text.TrimEnd('\r')));
            Assert.All(section.Lines.Where(line => line.IsExpandedContext), line =>
            {
                Assert.False(line.CanComment);
                Assert.Equal(Avalonia.Media.TextWrapping.Wrap, line.TextWrapping);
            });
            Assert.Equal(patch, section.Patch);
            Capture(window, theme, "diff-full-file");

            OpenFileActions(window, view);
            Assert.Equal("Collapse unchanged lines", fullFile.Header);
            Assert.True(fullFile.Focus());
            Press(TopLevel.GetTopLevel(fullFile)!, Key.Enter);
            await Until(() => !section.HasExpandedContext);
            OpenFileActions(window, view);
            Assert.Equal("Show full file", fullFile.Header);
            Assert.False(section.HasExpandedContext);
            Assert.Equal(patchRows, section.Lines.Where(line => !line.IsCollapsedContext));
            Assert.Equal(new[] { 31, 38, 27 }, section.Lines.Where(line => line.IsCollapsedContext).Select(line => line.ContextGap!.Count));
            fileActions.Hide();

            async Task ClickGap(RenderedDiffLine gap, string label)
            {
                await Dispatcher.UIThread.InvokeAsync(() => list.ScrollIntoView(gap), DispatcherPriority.Background);
                Point? position = null;
                await Until(() =>
                {
                    window.UpdateLayout();
                    var button = view.GetVisualDescendants().OfType<Button>().SingleOrDefault(item =>
                        ReferenceEquals(item.DataContext, gap) && item.IsEffectivelyVisible && Equals(item.Content, label));
                    // ScrollIntoView can leave the bottom of the gap under the overlay scrollbar.
                    position = button?.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 4), window);
                    return position is Point point && window.InputHitTest(point) is Visual hit &&
                        (ReferenceEquals(button, hit) || button!.IsVisualAncestorOf(hit));
                });
                var pointer = position!.Value;
                window.MouseMove(pointer);
                window.MouseDown(pointer, MouseButton.Left);
                window.MouseUp(pointer, MouseButton.Left);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_file_action_explains_display_limits_and_unavailable_source(bool sourceUnavailable)
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        const string path = "Large.txt";
        var tail = "one\ntwo\nthree\nfour\n" + (sourceUnavailable
            ? new string('x', 2 * 1024 * 1024)
            : new string('\n', DiffView.DisplayLineLimit));
        await fixture.Write(path, "before\n" + tail);
        await fixture.Commit("Add file");
        var basis = await fixture.Git("rev-parse", "HEAD");
        await fixture.Write(path, "after\n" + tail);
        await fixture.Commit("Edit first line");
        var head = await fixture.Git("rev-parse", "HEAD");
        var files = await fixture.Reader.FilesAsync(fixture.Root, basis, head);
        using var section = new DiffSectionViewModel(fixture.Reader, fixture.Root, new("head", "base", basis, head, null, files));
        await Until(() => !section.IsBusy && section.Lines.Count > 0);

        var view = new DiffSectionView { DataContext = section, ShowFileNavigator = false };
        var window = new Window { Content = view, Width = 480, Height = 420 };
        window.Show();
        try
        {
            OpenFileActions(window, view);
            var fullFile = view.FindControl<MenuItem>("FullFileButton")!;
            Assert.True(fullFile.IsEffectivelyVisible);
            Assert.Equal("Show full file", fullFile.Header);
            if (sourceUnavailable)
            {
                Assert.False(fullFile.IsEnabled);
                Assert.Equal("Full file source unavailable", ToolTip.GetTip(fullFile));
            }
            else
            {
                var rows = section.Lines.ToArray();
                Assert.True(fullFile.IsEnabled);
                ClickAt(window, fullFile);
                Assert.Contains("exceeds the display limit", section.Message);
                Assert.Equal(rows, section.Lines);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task Shared_files_preserve_the_selected_layer_and_restore_pane_preferences(string theme)
    {
        await using var fixture = new GitFixture();
        await fixture.Linear();
        await fixture.Write("A.txt", "Edited again in the third layer.\n");
        const string binaryPath = "src/feature-modules/review-tooling/shared-resources/desktop-preview-icons/assets/theme/preview.bin";
        var binary = Path.Combine(fixture.Root, binaryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllBytesAsync(binary, [0, 47, 0, 255]);
        await fixture.Commit("Edit the same file in a second layer");
        var store = new YamlStackStore();
        await store.SaveAsync(fixture.Root, [fixture.Stack], null);
        var settings = new JsonSettingsStore(Path.Combine(fixture.Root, "settings"));
        var appData = new ApplicationStore(Path.Combine(fixture.Root, "app-data"));
        await settings.SaveAsync(new AppSettings { Theme = theme, LastRepository = fixture.Root, WindowWidth = 1060, WindowHeight = 640, StacksHidden = false });

        using var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            await Until(() => vm.Sections.Count == 1 && !vm.IsBusy);
            await vm.SelectLayerAsync(vm.Layers[0], false);
            await vm.SelectLayerAsync(vm.Layers[2], true);
            Assert.Equal(2, vm.Sections.Count);
            var pane = window.FindControl<FileNavigatorView>("SharedFiles")!;
            var tree = pane.FindControl<TreeView>("FileTree")!;
            await Until(() => tree.Items.Count == 2);
            var laterLayer = Assert.IsType<FileTreeNode>(tree.Items[1]);
            var samePath = Assert.Single(laterLayer.Children, node => node.Name == "A.txt");
            var section = vm.Sections[1];
            var binaryRow = Assert.Single(laterLayer.Children, node => node.Path == binaryPath);
            Assert.Equal("preview.bin", binaryRow.Name);
            tree.SelectedItem = binaryRow;
            await Until(() => section.SelectedFile is { IsBinary: true } file && file.NewPath == binaryPath && !section.IsBusy);
            Assert.Contains("Binary files", section.Patch);
            tree.SelectedItem = Assert.Single(laterLayer.Children, node => node.Name == "C.txt");
            await Until(() => section.SelectedFile?.NewPath == "C.txt" && !section.IsBusy);
            tree.SelectedItem = samePath;
            await Until(() => section.SelectedFile?.NewPath == "A.txt" && !section.IsBusy);
            var selected = section.SelectedFile;
            Assert.Equal("A.txt", selected?.NewPath);
            Assert.Contains(section.Lines, line => line.Text.StartsWith("Edited again", StringComparison.Ordinal));

            pane.FindControl<TextBox>("FilterBox")!.Text = "no-match";
            await Until(() => tree.Items.Count == 0);
            Assert.Same(selected, section.SelectedFile);
            ClickAt(window, pane.FindControl<Button>("ClearFilter")!);
            await Until(() => tree.Items.Count == 2 && tree.SelectedItem is FileTreeNode { Name: "A.txt" });
            Assert.Same(selected, section.SelectedFile);

            var stacks = window.FindControl<ToggleButton>("StacksToggle")!;
            var files = window.FindControl<ToggleButton>("FilesToggle")!;
            var focus = window.FindControl<ToggleButton>("FocusToggle")!;
            var stackPane = window.FindControl<Grid>("StacksPane")!;
            var area = window.FindControl<Grid>("WorkArea")!;
            Assert.True(area.ColumnDefinitions[2].ActualWidth >= 520);
            var sections = window.FindControl<ScrollViewer>("SectionScroll")!.GetVisualDescendants().OfType<DiffSectionView>().ToArray();
            Assert.Equal(2, sections.Length);
            Assert.All(sections, view => Assert.False(view.FindControl<Grid>("FilePane")!.IsVisible));
            Capture(window, theme, "shared-files");
            window.Width = 1450;
            window.Height = 880;
            window.UpdateLayout();
            Assert.True(stackPane.IsVisible && pane.IsVisible);
            Assert.True(stacks.IsChecked);
            Assert.True(files.IsChecked);
            window.Width = 1060;
            window.Height = 640;
            window.UpdateLayout();
            Assert.True(stackPane.IsVisible && pane.IsVisible);
            Assert.True(stacks.IsChecked);
            Assert.True(files.IsChecked);
            var group = Assert.Single(vm.LocalGroups);
            var disclosure = Assert.Single(stackPane.GetVisualDescendants().OfType<Button>(),
                button => ReferenceEquals(button.DataContext, group) && button.Content is PathIcon);
            ClickAt(window, disclosure);
            Assert.False(group.IsExpanded);
            ClickAt(window, disclosure);
            Assert.True(group.IsExpanded);
            Assert.False(vm.HasError, vm.Error);

            ClickAt(window, files);
            Assert.False(pane.IsVisible);
            Assert.True(stackPane.IsVisible);
            Assert.True(stacks.IsChecked);

            ClickAt(window, focus);
            Assert.False(pane.IsVisible || stackPane.IsVisible);
            Assert.True(focus.Focus());
            Press(window, Key.Space);
            Assert.False(pane.IsVisible);
            Assert.True(stackPane.IsVisible);

            Assert.True(files.Focus());
            Press(window, Key.Space);
            Assert.True(pane.IsVisible);
            Assert.Same(selected, section.SelectedFile);
            Assert.Same(Assert.Single(Assert.IsType<FileTreeNode>(tree.Items[1]).Children, node => node.Name == "A.txt"), tree.SelectedItem);

            ClickAt(window, files);
            ClickAt(window, stacks);
            Assert.False(stackPane.IsVisible || pane.IsVisible);
        }
        finally { window.Close(); await Until(() => !window.IsVisible); }

        using var reopenedVm = CreateViewModel();
        var reopened = new MainWindow { DataContext = reopenedVm };
        reopened.Show();
        try
        {
            await Until(() => reopenedVm.Sections.Count == 2 && !reopenedVm.IsBusy);
            var pane = reopened.FindControl<FileNavigatorView>("SharedFiles")!;
            Assert.False(pane.IsVisible);
            Assert.False(reopened.FindControl<Grid>("StacksPane")!.IsVisible);
            var files = reopened.FindControl<ToggleButton>("FilesToggle")!;
            Assert.False(files.IsChecked);
            ClickAt(reopened, files);
            Assert.True(pane.IsVisible);
            var tree = pane.FindControl<TreeView>("FileTree")!;
            await Until(() => tree.Items.Count == 2 && tree.SelectedItem is FileTreeNode selected &&
                Assert.IsType<FileTreeNode>(tree.Items[1]).Children.Contains(selected));
            Assert.Equal(AppSettings.DefaultFilesWidth, reopened.FindControl<Grid>("WorkArea")!.ColumnDefinitions[0].ActualWidth);
        }
        finally { reopened.Close(); await Until(() => !reopened.IsVisible); }

        MainViewModel CreateViewModel() => new(fixture.Reader, store, settings, new GitExecutable(), new(fixture.Reader), new(fixture.Reader),
            applicationStore: appData);
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task Pr_keeps_code_dominant_until_review_is_opened_and_preserves_a_pending_review_across_focus_and_resize(string theme)
    {
        // Keep generated Git history paths within Windows' path limits.
        var root = Directory.CreateTempSubdirectory("stacker-review-");
        using var services = Composition.Build(root.FullName);
        var vm = services.GetRequiredService<MainViewModel>();
        vm.Settings.Theme = theme;
        vm.Settings.WindowWidth = 1060;
        vm.Settings.WindowHeight = 640;
        await vm.SaveSettingsAsync();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            await Until(() => (bool)typeof(MainWindow).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            await vm.OpenDemoAsync();
            var group = vm.RemoteGroups.First(g => g.Layers.Any(layer => layer.PullRequest?.Number == 102));
            await vm.SelectLayerAsync(group.Layers.Single(layer => layer.PullRequest?.Number == 102), false);
            var review = Assert.IsType<ReviewViewModel>(vm.ActiveReview);
            var section = Assert.Single(vm.Sections);
            await Until(() => !section.IsBusy && section.Lines.Any(row => row.CanComment));
            var selected = section.SelectedFile;
            var inspector = window.FindControl<Border>("ReviewHost")!;
            var workspace = window.FindControl<Grid>("DiffWorkspace")!;
            var stacks = window.FindControl<Grid>("StacksPane")!;
            var files = window.FindControl<FileNavigatorView>("SharedFiles")!;
            var reviewButton = window.FindControl<Button>("ReviewButton")!;
            var focus = window.FindControl<ToggleButton>("FocusToggle")!;
            var diff = window.GetVisualDescendants().OfType<DiffSectionView>().Single(view => ReferenceEquals(view.DataContext, section));
            var viewport = diff.FindControl<Grid>("DiffViewport")!;

            Capture(window, theme, "code-pr-compact");
            AssertDefaultCodeLayout();
            await ResizeWindowAsync(1400, 880);
            Capture(window, theme, "code-pr-default");
            AssertDefaultCodeLayout();
            await ResizeWindowAsync(1450, 880);
            Capture(window, theme, "code-pr-wide");
            AssertDefaultCodeLayout();
            await ResizeWindowAsync(1060, 640);
            window.UpdateLayout();
            AssertDefaultCodeLayout();
            await ResizeWindowAsync(1450, 880);
            window.UpdateLayout();
            AssertDefaultCodeLayout();
            Assert.False(vm.IsReviewOpen);
            Assert.Equal(0, review.PanelTab);
            Assert.False(review.IsPrComposerOpen);
            Assert.False(review.CanPublish);

            OpenFileActions(window, diff);
            var wrapItem = diff.FindControl<MenuItem>("WrapCodeMenuItem")!;
            CaptureMenu(wrapItem, theme, "file-menu");
            Assert.False(wrapItem.IsChecked);
            ClickAt(window, wrapItem);
            await Until(() => vm.Settings.WrapCode);
            Assert.True(section.WrapCode);
            OpenFileActions(window, diff);
            Assert.True(wrapItem.IsChecked);
            Assert.True(wrapItem.Focus());
            Press(TopLevel.GetTopLevel(wrapItem)!, Key.Enter);
            await Until(() => !vm.Settings.WrapCode);
            Assert.False(section.WrapCode);

            var suggestionThread = Assert.Single(diff.GetVisualDescendants().OfType<ThreadView>(), thread =>
                thread.DataContext is ReviewThread item && item.Comments.Any(comment => comment.Body.Contains("```suggestion", StringComparison.Ordinal)));
            var disclosure = suggestionThread.FindControl<ToggleButton>("Disclosure")!;
            Assert.False(disclosure.IsChecked);
            ClickAt(window, disclosure);
            window.UpdateLayout();
            var suggestionActions = suggestionThread.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Content as string == "Copy code" && button.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(suggestionActions);
            Assert.All(suggestionActions, button =>
            {
                var position = button.TranslatePoint(default, viewport)!.Value;
                Assert.InRange(position.X + button.Bounds.Width, 0, viewport.Bounds.Width);
            });
            ClickAt(window, disclosure);
            Assert.False(disclosure.IsChecked);

            ClickAt(window, reviewButton);
            await Until(() => review.PanelTab == 3 && inspector.IsVisible);
            Capture(window, theme, "pr-explicit-review-wide");
            Assert.True(workspace.IsVisible, $"Window {window.ClientSize}; workspace {window.FindControl<Grid>("Workspace")!.Bounds}; files {vm.Settings.FilesWidth}; stacks hidden {vm.Settings.StacksHidden}; review {inspector.Bounds}.");
            Assert.True(files.IsVisible);
            Capture(window, theme, "pr-explicit-review-wide");
            var line = section.Lines.First(row => row.CanComment && row.NewLineNumber is not null);
            review.SetSelection([line]);
            review.Composer = "Keep this local draft when changing the layout.";
            await review.AddToDraftCommand.ExecuteAsync(null);
            Assert.Single(review.DraftComments);
            Assert.Equal("Review (1)", reviewButton.Content);

            ClickAt(window, focus);
            Assert.False(inspector.IsVisible);
            Assert.True(workspace.IsVisible);
            ClickAt(window, focus);
            Assert.True(inspector.IsVisible);
            Assert.True(files.IsVisible);
            Assert.False(stacks.IsVisible);

            await ResizeWindowAsync(1060, 640);
            Capture(window, theme, "pr-explicit-review-compact");
            Assert.True(inspector.IsVisible);
            Assert.False(workspace.IsVisible);
            Assert.True(files.IsVisible);
            Assert.False(stacks.IsVisible);
            Assert.Same(review, vm.ActiveReview);
            Assert.Equal(3, review.PanelTab);
            Assert.Equal("Keep this local draft when changing the layout.", Assert.Single(review.DraftComments).Body);
            Assert.Same(selected, section.SelectedFile);
            Assert.False(review.CanPublish);

            var back = window.FindControl<Button>("CloseReviewButton")!;
            Assert.Equal("Back to diff", back.Content);
            Assert.True(back.Focus());
            Press(window, Key.Space);
            AssertDefaultCodeLayout();
            Assert.Same(review, vm.ActiveReview);

            ClickAt(window, reviewButton);
            Assert.True(inspector.IsVisible);
            ClickAt(window, back);
            AssertDefaultCodeLayout();
            await ResizeWindowAsync(1450, 880);
            window.UpdateLayout();
            AssertDefaultCodeLayout();
            Assert.Equal("Keep this local draft when changing the layout.", Assert.Single(review.DraftComments).Body);

            ClickAt(window, reviewButton);
            await Until(() => inspector.IsVisible);
            var openMenu = OpenFileActions(window, diff);
            Press(TopLevel.GetTopLevel(wrapItem)!, Key.Escape);
            Assert.False(openMenu.IsOpen);
            Assert.True(inspector.IsVisible);
            Press(window, Key.Escape);
            AssertDefaultCodeLayout();
            Assert.True(reviewButton.IsFocused);
            Assert.Equal("Keep this local draft when changing the layout.", Assert.Single(review.DraftComments).Body);

            async Task ResizeWindowAsync(double width, double height)
            {
                window.Width = width;
                window.Height = height;
                await Until(() => window.ClientSize == new Size(width, height));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal(new Size(width, height), window.ClientSize);
            }

            void AssertDefaultCodeLayout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.False(inspector.IsVisible);
                Assert.True(workspace.IsVisible);
                Assert.False(stacks.IsVisible);
                Assert.True(files.IsVisible);
                Assert.False(window.FindControl<ToggleButton>("StacksToggle")!.IsChecked);
                Assert.True(window.FindControl<ToggleButton>("FilesToggle")!.IsChecked);
                var sectionScroll = window.FindControl<ScrollViewer>("SectionScroll")!;
                var visible = new Rect(viewport.TranslatePoint(default, window)!.Value, viewport.Bounds.Size)
                    .Intersect(new Rect(sectionScroll.TranslatePoint(default, window)!.Value, sectionScroll.Bounds.Size))
                    .Intersect(new Rect(window.ClientSize));
                var widthRatio = visible.Width / window.ClientSize.Width;
                var heightRatio = visible.Height / window.ClientSize.Height;
                var details = $"Visible diff {visible.Width:0}×{visible.Height:0}: width {widthRatio:P1}, height {heightRatio:P1}, area {widthRatio * heightRatio:P1}.";
                if (Environment.GetEnvironmentVariable("STACKER_UI_CAPTURE_DIR") is { } directory)
                {
                    var name = $"{theme}-code-{window.ClientSize.Width:0}x{window.ClientSize.Height:0}.json";
                    File.WriteAllText(Path.Combine(directory, name), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        WindowWidth = window.ClientSize.Width,
                        WindowHeight = window.ClientSize.Height,
                        CodeViewportWidth = visible.Width,
                        CodeViewportHeight = visible.Height,
                        WidthRatio = widthRatio,
                        HeightRatio = heightRatio,
                        AreaRatio = widthRatio * heightRatio
                    }));
                }
                Assert.True(widthRatio >= .75, details);
                Assert.True(heightRatio >= .75, details);
            }
        }
        finally
        {
            window.Close();
            await Until(() => !window.IsVisible);
            services.Dispose();
            GitFixture.DeleteDirectory(root.FullName);
        }
    }

    [AvaloniaFact]
    public async Task History_tab_stays_inactive_while_changes_are_pending()
    {
        var root = Path.Combine(Path.GetTempPath(), "stacker-ui-" + Guid.NewGuid().ToString("N"));
        using var services = Composition.Build(root);
        var vm = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            await Until(() => (bool)typeof(MainWindow).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            await vm.OpenDemoAsync();
            await vm.SelectGroupAsync(vm.LocalGroups[0]);
            vm.HasPendingChanges = true;

            var historyTab = window.FindControl<ToggleButton>("HistoryTab")!;
            ClickAt(window, historyTab);

            await Until(() => vm.Error.Contains("Apply Update changes", StringComparison.Ordinal));
            Assert.False(historyTab.IsChecked);
            Assert.True(window.FindControl<ToggleButton>("ChangesTab")!.IsChecked);
        }
        finally { await vm.PrepareCloseAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task Secondary_screens_fit_and_keep_actions_available(string theme)
    {
        var root = Path.Combine(Path.GetTempPath(), "stacker-ui-" + Guid.NewGuid().ToString("N"));
        using var services = Composition.Build(root);
        var vm = services.GetRequiredService<MainViewModel>();
        vm.Settings.Theme = theme;
        vm.Settings.WindowWidth = 1060;
        vm.Settings.WindowHeight = 640;
        await vm.SaveSettingsAsync();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await Until(() => (bool)typeof(MainWindow).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        Application.Current!.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        window.Width = 1060;
        window.Height = 640;
        try
        {
            Capture(window, theme, "empty");
            vm.IsBusy = true;
            Assert.False(vm.ShowEmptyState);
            Capture(window, theme, "loading");
            vm.IsBusy = false;
            vm.OpenSearch("Files");
            vm.SearchQuery = "Auth";
            await vm.SearchAsync();
            Assert.Contains("Select a stack", vm.SearchStatus);
            Capture(window, theme, "search-no-comparison");
            vm.CloseSearch();
            await vm.OpenDemoAsync();
            await vm.SelectGroupAsync(vm.LocalGroups[0]);
            vm.OpenSearch("Files");
            vm.SearchQuery = "Auth";
            await vm.SearchAsync();
            Assert.NotEmpty(vm.SearchResults);
            Capture(window, theme, "search-results");
            vm.SearchQuery = "no-match-unique";
            await vm.SearchAsync();
            Assert.Contains("No matches", vm.SearchStatus);
            Capture(window, theme, "search-empty");
            vm.SearchRegex = true;
            vm.SearchQuery = "[";
            await vm.SearchAsync();
            Capture(window, theme, "search-invalid-regex");
            vm.CloseSearch();
            vm.Error = "Cannot open the selected repository. The directory is unavailable. Reconnect its drive or use Open repository to choose its current location.\n" + string.Join("\n", Enumerable.Repeat("Git diagnostic details remain selectable and scrollable without hiding workspace actions.", 10));
            Capture(window, theme, "error");
            Click(window, "Dismiss");
            Assert.False(vm.HasError);
            await vm.ShowHistoryAsync();
            Assert.NotNull(vm.ActiveHistory);
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
            var browse = historyPanel.FindControl<Button>("HistoryFilesButton")!;
            Assert.True(browse.IsEffectivelyVisible);
            var filePicker = Assert.IsType<Flyout>(browse.Flyout);
            filePicker.ShowAt(browse);
            var navigator = Assert.IsType<FileNavigatorView>(filePicker.Content);
            Assert.NotEmpty(navigator.FindControl<ListBox>("FileList")!.Items);
            filePicker.Hide();
            Capture(window, theme, "history-document");
            var tools = historyPanel.FindControl<Button>("HistoryToolsButton")!;
            var flyout = Assert.IsType<Flyout>(tools.Flyout);
            flyout.ShowAt(tools);
            var flyoutBody = Assert.IsType<ScrollViewer>(flyout.Content);
            Assert.Same(history, flyoutBody.DataContext);
            foreach (var expander in flyoutBody.GetLogicalDescendants().OfType<Expander>())
                expander.IsExpanded = true;
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
            Assert.Null(history.SelectedFile);
            Assert.Null(history.PreviousVersion);
            Assert.Null(history.ActiveDocumentSection);
            Assert.Contains("Select a layer", history.Message);
            Capture(window, theme, "history-empty");
            vm.CloseHistoryCommand.Execute(null);
            await CaptureDialog(window, "SettingsClick", theme, "settings");
            await CaptureDialog(window, "GitHubSettingsClick", theme, "github");
            await CaptureDialog(window, "HelpClick", theme, "help");
            await CaptureDialog(window, "RelocateRepositoryClick", theme, "relocate");
            var unsavedChoice = (Task<UnsavedCloseChoice>)typeof(MainWindow).GetMethod("AskUnsavedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { new[] { "The review draft could not be saved. The disk is full.", "Free disk space, then retry saving." } })!;
            await Until(() => window.OwnedWindows.Count > 0);
            var unsaved = window.OwnedWindows.Single();
            Minimum(unsaved);
            Capture(unsaved, theme, "unsaved");
            Press(unsaved, Key.Escape);
            Assert.Equal(UnsavedCloseChoice.ContinueEditing, await unsavedChoice);
            OpenComparisonActions(window);
            ClickAt(window, window.FindControl<MenuItem>("AddLayerButton")!);
            await Until(() => window.OwnedWindows.OfType<AddLayerWindow>().Any());
            var add = window.OwnedWindows.OfType<AddLayerWindow>().Single();
            Minimum(add);
            Click(add, "Create layer");
            Capture(add, theme, "add-layer-validation");
            Assert.Contains("Enter a branch", add.FindControl<SelectableTextBlock>("ErrorLabel")!.Text);
            add.Close();
            OpenComparisonActions(window);
            var editButton = window.FindControl<MenuItem>("EditStackButton")!;
            Assert.True(editButton.Focus());
            Press(TopLevel.GetTopLevel(editButton)!, Key.Enter);
            await Until(() => window.OwnedWindows.OfType<StackEditorWindow>().Any());
            var edit = window.OwnedWindows.OfType<StackEditorWindow>().Single();
            Minimum(edit);
            Capture(edit, theme, "stack-editor");
            var editorBody = Assert.Single(Assert.IsType<Grid>(edit.Content).Children.OfType<ScrollViewer>());
            editorBody.Offset = new Vector(0, 10000);
            Capture(edit, theme, "stack-editor-delete");
            var delete = edit.FindControl<Button>("DeleteButton")!;
            Assert.True(delete.IsEffectivelyVisible);
            Assert.InRange(delete.TranslatePoint(default, edit)!.Value.Y, 0, edit.ClientSize.Height - delete.Bounds.Height);
            edit.Close();
            OpenComparisonActions(window);
            ClickAt(window, window.FindControl<MenuItem>("CommitStackButton")!);
            await Until(() => window.OwnedWindows.OfType<CommitWindow>().Any());
            var commit = window.OwnedWindows.OfType<CommitWindow>().Single();
            Minimum(commit);
            Capture(commit, theme, "commit-clean");
            Assert.False(commit.FindControl<Button>("CommitButton")!.IsEnabled);
            commit.Close();
            await File.AppendAllTextAsync(Path.Combine(vm.Repository!.Root, "Auth.cs"), "\n// visual review fixture\n");
            await vm.RefreshWorkingTreeAsync();
            var dirtyCommit = new CommitWindow(vm);
            dirtyCommit.Show(window);
            Minimum(dirtyCommit);
            await Task.Delay(30);
            Click(dirtyCommit, "Commit");
            Capture(dirtyCommit, theme, "commit-validation");
            Assert.Equal("Enter a commit message.", dirtyCommit.FindControl<SelectableTextBlock>("ErrorLabel")!.Text);
            dirtyCommit.Close();
            OpenComparisonActions(window);
            ClickAt(window, window.FindControl<MenuItem>("PreviewRestackButton")!);
            await Until(() => window.OwnedWindows.OfType<SyncPreviewWindow>().Any());
            var sync = window.OwnedWindows.OfType<SyncPreviewWindow>().Single();
            Minimum(sync);
            Capture(sync, theme, "sync-preview");
            sync.Close();
        }
        finally { await vm.PrepareCloseAsync(); window.Close(); }
    }
    private static void Minimum(Window window)
    {
        window.Width = window.MinWidth > 0 ? window.MinWidth : Math.Min(window.Width, 600);
        window.Height = window.MinHeight > 0 ? window.MinHeight : Math.Min(window.Height, 560);
    }
    private static MenuFlyout OpenFileActions(Window window, DiffSectionView view) =>
        OpenActions(window, view.FindControl<Button>("FileActionsButton")!);
    private static MenuFlyout OpenComparisonActions(MainWindow window) =>
        OpenActions(window, window.FindControl<Button>("ComparisonActionsButton")!);
    private static MenuFlyout OpenActions(Window window, Button button)
    {
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        ClickAt(window, button);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.IsOpen);
        return flyout;
    }
    private static void Click(Control root, string label) => root.GetLogicalDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void ClickAt(Window window, Control control)
    {
        window.UpdateLayout();
        var inputRoot = TopLevel.GetTopLevel(control)!;
        inputRoot.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), inputRoot);
        Assert.NotNull(point);
        inputRoot.MouseMove(point.Value);
        inputRoot.MouseDown(point.Value, MouseButton.Left);
        inputRoot.MouseUp(point.Value, MouseButton.Left);
    }
    private static void Press(TopLevel window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, "");
        if (window.IsVisible)
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, "");
    }
    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 300; i++)
        {
            if (predicate())
                return;
            await Task.Delay(20);
        }
        Assert.True(predicate(), "UI state did not become available within six seconds.");
    }
    private static async Task CaptureDialog(MainWindow owner, string handler, string theme, string name)
    {
        typeof(MainWindow).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, new object?[] { null, new RoutedEventArgs() });
        await Until(() => owner.OwnedWindows.Count > 0);
        var dialog = owner.OwnedWindows.Single();
        Minimum(dialog);
        Capture(dialog, theme, name);
        var body = dialog.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (body is not null)
        {
            body.Offset = new Vector(0, 10000);
            Capture(dialog, theme, name + "-bottom");
        }
        Press(dialog, Key.Tab);
        Assert.NotNull(dialog.FocusManager?.GetFocusedElement());
        if (name == "settings")
            Capture(dialog, theme, name + "-focus");
        Press(dialog, Key.Escape);
        await Until(() => !dialog.IsVisible);
    }
    private static void CaptureMenu(MenuItem item, string theme, string name)
    {
        if (Environment.GetEnvironmentVariable("STACKER_UI_CAPTURE_DIR") is not { } directory)
            return;
        var surface = TopLevel.GetTopLevel(item)!;
        surface.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = surface.CaptureRenderedFrame();
        frame?.Save(Path.Combine(directory, theme + "-" + name + ".png"));
    }

    private static void Capture(Window window, string theme, string name)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.Bounds.Width > 0 && window.Bounds.Height > 0);
        var directory = Environment.GetEnvironmentVariable("STACKER_UI_CAPTURE_DIR");
        if (directory is null)
            return;
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame();
        Assert.True(frame is not null, $"No rendered frame for {name}; visible={window.IsVisible}.");
        frame!.Save(Path.Combine(directory, theme + "-" + name + ".png"));
    }
}
