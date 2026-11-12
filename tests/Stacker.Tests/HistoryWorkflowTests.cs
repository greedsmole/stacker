using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed class HistoryWorkflowTests
{
    [AvaloniaFact]
    public async Task File_checkbox_updates_the_shared_checkpoint_without_changing_selection()
    {
        await using var fixture = new GitFixture();
        await fixture.Linear();
        await fixture.Write("src/extra.txt", "extra\n");
        await fixture.Commit("second file in C");
        await new YamlStackStore().SaveAsync(fixture.Root, [fixture.Stack], null);
        var root = Directory.CreateTempSubdirectory("file-review-").FullName;
        try
        {
            using var services = Composition.Build(root);
            var vm = services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                await vm.OpenRepositoryAsync(fixture.Root);
                await vm.SelectLayerAsync(Assert.Single(vm.LocalGroups).Layers[2], false);
                var section = Assert.Single(vm.Sections);
                await section.LoadAsync(section.SelectedFile);
                var selected = section.SelectedFile;
                Assert.Equal("C.txt", selected?.NewPath);
                window.UpdateLayout();
                var checkbox = Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), control =>
                    control.DataContext is DiffFileRow row && row.Change.NewPath == "src/extra.txt");
                var file = Assert.IsType<DiffFileRow>(checkbox.DataContext);
                Assert.False(file.IsReviewed);

                var position = checkbox.TranslatePoint(new Point(checkbox.Bounds.Width / 2, checkbox.Bounds.Height / 2), window)!.Value;
                window.MouseMove(position);
                window.MouseDown(position, MouseButton.Left);
                window.MouseUp(position, MouseButton.Left);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (!file.IsReviewed) await Task.Delay(10, timeout.Token);
                Assert.Equal(selected, section.SelectedFile);
                Assert.True(checkbox.IsChecked);

                using var history = await vm.OpenHistoryAsync();
                Assert.NotNull(history);
                history.SelectedNode = history.Nodes[2];
                await history.LoadLayerCommand.ExecuteAsync(null);
                Assert.Equal(FileReviewState.Reviewed, history.Files.Single(item => item.Change == file.Change).State);
                await vm.SetGroupFilesByFolderAsync(true);
                Assert.Single(section.FolderFiles, node => node.Name == "src").IsExpanded = true;
                window.UpdateLayout();
                var treeCheckbox = Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), control =>
                    ReferenceEquals(control.DataContext, file) && control.IsEffectivelyVisible);
                var treePosition = treeCheckbox.TranslatePoint(new Point(treeCheckbox.Bounds.Width / 2, treeCheckbox.Bounds.Height / 2), window)!.Value;
                window.MouseMove(treePosition);
                window.MouseDown(treePosition, MouseButton.Left);
                window.MouseUp(treePosition, MouseButton.Left);
                while (file.IsReviewed) await Task.Delay(10, timeout.Token);
                Assert.Equal(selected, section.SelectedFile);
            }
            finally
            {
                window.Close();
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (window.IsVisible) await Task.Delay(10, closing.Token);
            }
        }
        finally { GitFixture.DeleteDirectory(root); }
    }

    [Fact]
    public async Task Progress_preserves_unmodified_files_and_invalidates_base_context()
    {
        await using var f = new GitFixture(); await f.Init(); var root = Directory.CreateTempSubdirectory("review-progress-").FullName;
        try
        {
            var basis = await f.Git("rev-parse", "HEAD"); await f.Git("checkout", "-b", "A");
            await f.Write("a.txt", "a\n"); await f.Write("b.txt", "b\n"); await f.Commit("both"); var head = await f.Git("rev-parse", "HEAD");
            var store = new SqliteHistoryStore(root); var objects = new HistoryGit(f.Runner, new(), new ApplicationStore(root));
            (await objects.CaptureAsync(f.Identity, "scope", [basis, head])).OrThrow(); var cache = f.Root;
            var node = (await store.ResolveNodeAsync("scope", "A", "A")).OrThrow();
            var old = (await store.ObserveAsync(new("", node.Id, basis, head, basis, DateTimeOffset.UtcNow))).OrThrow();
            var progress = new ReviewProgressService(store, new(f.Reader, objects));
            foreach (var path in new[] { "a.txt", "b.txt" }) (await store.SetCheckpointAsync(new(node.Id, path, old.Id, DateTimeOffset.UtcNow), false)).OrThrow();
            await f.Write("a.txt", "fix\n"); await f.Commit("fix"); head = await f.Git("rev-parse", "HEAD");
            (await objects.CaptureAsync(f.Identity, "scope", [head])).OrThrow();
            var current = (await store.ObserveAsync(new("", node.Id, basis, head, basis, DateTimeOffset.UtcNow))).OrThrow();
            var files = (await f.Reader.FilesAsync(cache, basis, head));
            var status = await progress.StatusAsync(f.Identity, "scope", current, files);
            Assert.Equal(FileReviewState.CodeChanged, status.Single(s => s.Change.NewPath == "a.txt").State);
            Assert.Equal(FileReviewState.Reviewed, status.Single(s => s.Change.NewPath == "b.txt").State);
            await f.Git("checkout", "main"); await f.Write("upstream", "upstream\n"); await f.Commit("upstream"); var nextBase = await f.Git("rev-parse", "HEAD");
            await f.Git("checkout", "A"); await f.Git("rebase", "main"); head = await f.Git("rev-parse", "HEAD");
            (await objects.CaptureAsync(f.Identity, "scope", [head, nextBase])).OrThrow();
            current = (await store.ObserveAsync(new("", node.Id, nextBase, head, nextBase, DateTimeOffset.UtcNow))).OrThrow();
            status = await progress.StatusAsync(f.Identity, "scope", current, await f.Reader.FilesAsync(cache, nextBase, head));
            Assert.Equal(FileReviewState.BaseChanged, status.Single(s => s.Change.NewPath == "b.txt").State);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task File_evolution_follows_rename_and_attributes_latest_added_line()
    {
        await using var f = new GitFixture(); await f.Init();
        var basis = await f.Git("rev-parse", "HEAD"); await f.Git("checkout", "-b", "A");
        await f.Write("a.txt", "one\ntwo\nthree\nfour\nfive\n"); await f.Commit("add"); var a = await f.Git("rev-parse", "HEAD");
        await f.Git("checkout", "-b", "B"); await f.Git("mv", "a.txt", "b.txt"); await f.Commit("rename"); var b = await f.Git("rev-parse", "HEAD");
        await f.Git("checkout", "-b", "C"); await f.Write("b.txt", "one\ntwo\nthree\nfour\nfive\nsix\n"); await f.Commit("append"); var c = await f.Git("rev-parse", "HEAD");
        var path = new[] { (new ChangeNode("A", "s", "A", "A"), new ChangeVersion("a", "A", basis, a, basis, DateTimeOffset.UtcNow)),
            (new ChangeNode("B", "s", "B", "B"), new ChangeVersion("b", "B", a, b, a, DateTimeOffset.UtcNow)),
            (new ChangeNode("C", "s", "C", "C"), new ChangeVersion("c", "C", b, c, b, DateTimeOffset.UtcNow)) };
        var service = new FileEvolutionService(f.Reader); var evolution = await service.BuildAsync(f.Root, path, "b.txt");
        Assert.Equal(3, evolution.Steps.Count); Assert.Equal(FileChangeKind.Renamed, evolution.Steps[1].File!.Kind);
        Assert.Contains("in C", await service.TraceLineAsync(f.Root, evolution, 6));
        Assert.Contains("in A", await service.TraceLineAsync(f.Root, evolution, 1));
    }
    [Fact]
    public async Task Stack_analysis_uses_graph_versions_even_after_new_metadata_arrives()
    {
        await using var f = new GitFixture(); await f.Init(); var root = Directory.CreateTempSubdirectory("stack-analysis-").FullName;
        try
        {
            var basis = await f.Git("rev-parse", "HEAD"); await f.Git("checkout", "-b", "A"); await f.Write("base.txt", "A\n"); await f.Commit("A"); var a = await f.Git("rev-parse", "HEAD");
            await f.Git("checkout", "-b", "B", basis); await f.Write("base.txt", "B\n"); await f.Commit("B"); var b = await f.Git("rev-parse", "HEAD");
            var store = new SqliteHistoryStore(root); var objects = new HistoryGit(f.Runner, new(), new ApplicationStore(root));
            (await objects.CaptureAsync(f.Identity, "s", [basis, a, b])).OrThrow();
            var na = (await store.ResolveNodeAsync("s", "A", "A")).OrThrow(); var nb = (await store.ResolveNodeAsync("s", "B", "B")).OrThrow();
            var va = (await store.ObserveAsync(new("", na.Id, basis, a, basis, DateTimeOffset.UtcNow))).OrThrow();
            var vb = (await store.ObserveAsync(new("", nb.Id, basis, b, basis, DateTimeOffset.UtcNow))).OrThrow();
            var graph = new GraphSnapshot("s", DateTimeOffset.UtcNow, [na, nb], [new(na.Id, nb.Id, "explicit")], [])
            { Versions = new Dictionary<string, string> { [na.Id] = va.Id, [nb.Id] = vb.Id } };
            (await store.ObserveAsync(new("", nb.Id, basis, new string('f', 40), null, DateTimeOffset.UtcNow.AddMinutes(1)))).OrThrow();
            var result = await new StackAnalysisService(store, objects, f.Reader).AnalyzeAsync(f.Identity, graph);
            Assert.Contains(result.Issues, i => i.Kind == "StaleParent"); Assert.Contains(result.Issues, i => i.Kind == "Overlap"); Assert.Contains(result.Issues, i => i.Kind == "Conflict");
            Assert.DoesNotContain(result.Issues, i => i.Kind == "Unavailable");
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task History_picker_opens_base_only_files_and_restores_the_comparison_section()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        var oldBase = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("checkout", "-b", "A");
        await fixture.Write("own.txt", "original\n");
        await fixture.Commit("Original layer");
        var oldHead = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("checkout", "main");
        await fixture.Write("upstream/config/upstream.txt", "base-only change\n");
        await fixture.Commit("Upstream change");
        var currentBase = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("checkout", "A");
        await fixture.Git("rebase", "main");
        await fixture.Write("own.txt", "revised\n");
        await fixture.Commit("Revise layer");
        var currentHead = await fixture.Git("rev-parse", "HEAD");

        var root = Directory.CreateTempSubdirectory("history-picker-").FullName;
        try
        {
            const string scope = "history-picker";
            var store = new SqliteHistoryStore(root);
            var objects = new HistoryGit(fixture.Runner, new(), new ApplicationStore(root));
            (await objects.CaptureAsync(fixture.Identity, scope, [oldBase, oldHead, currentBase, currentHead])).OrThrow();
            var node = (await store.ResolveNodeAsync(scope, "refs/heads/A", "A")).OrThrow();
            (await store.ObserveAsync(new("", node.Id, oldBase, oldHead, oldBase, DateTimeOffset.UtcNow.AddDays(-1)))).OrThrow();
            var version = (await store.ObserveAsync(new("", node.Id, currentBase, currentHead, currentBase, DateTimeOffset.UtcNow))).OrThrow();
            var graph = new GraphSnapshot(scope, DateTimeOffset.UtcNow, [node], [], []);
            var comparisons = new VersionComparisonService(fixture.Reader, objects);
            using var history = new HistoryWorkspaceViewModel(store, objects, fixture.Reader, comparisons,
                new ReviewProgressService(store, comparisons), graph, fixture.Identity, [(node, version)]);
            await history.InitializeAsync();
            var panel = new HistoryPanel { DataContext = history };
            var window = new Window { Content = panel, Width = 780, Height = 520 };
            window.Show();
            try
            {
                await history.ShowCurrentCommand.ExecuteAsync(null);
                await history.CompareVersionsCommand.ExecuteAsync(null);
                Assert.Equal("Own changes after replay", history.ActiveDocumentSection?.Title);
                var baseFile = Assert.Single(history.DocumentFiles, file => file.File.NewPath == "upstream/config/upstream.txt");
                Assert.Equal("Base changes · upstream/config/upstream.txt", baseFile.Label);
                var picker = panel.FindControl<ComboBox>("HistoryDiffFiles")!;
                var folders = panel.FindControl<Button>("HistoryFolderButton")!;
                var flyout = Assert.IsType<Flyout>(folders.Flyout);
                flyout.ShowAt(folders);
                var tree = Assert.Single(Assert.IsType<Grid>(flyout.Content).Children.OfType<TreeView>());
                var baseSection = Assert.Single(tree.Items.OfType<FileTreeNode>(), node => node.Name == "Base changes");
                var folder = Assert.Single(baseSection.Children);
                Assert.Equal("upstream", folder.Name);
                var config = Assert.Single(folder.Children);
                Assert.Equal("config", config.Name);
                tree.SelectedItem = Assert.Single(config.Children);
                tree.RaiseEvent(new KeyEventArgs { Key = Key.Enter, RoutedEvent = InputElement.KeyDownEvent });
                Assert.False(flyout.IsOpen);
                Assert.Equal(baseFile, picker.SelectedItem);
                var section = Assert.IsType<DiffSectionViewModel>(history.ActiveDocumentSection);
                Assert.Equal("upstream/config/upstream.txt", section.SelectedFile?.NewPath);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (section.IsBusy) await Task.Delay(10, timeout.Token);
                Assert.Contains("base-only change", section.Patch);
                Assert.Null(history.SelectedFile);
                Assert.False(history.MarkReviewedCommand.CanExecute(null));
                Assert.True(history.ShowCurrentCommand.CanExecute(null));

                await history.ShowCurrentCommand.ExecuteAsync(null);
                Assert.Equal("own.txt", history.ActiveDocumentSection?.SelectedFile?.NewPath);
                await history.BackCommand.ExecuteAsync(null);
                Assert.Equal("Base changes", history.ActiveDocumentSection?.Title);
                Assert.Equal("upstream/config/upstream.txt", history.ActiveDocumentSection?.SelectedFile?.NewPath);
                Assert.Equal(baseFile.Label, Assert.IsType<HistoryDocumentFile>(picker.SelectedItem).Label);
                await history.ForwardCommand.ExecuteAsync(null);
                Assert.Equal("own.txt", history.ActiveDocumentSection?.SelectedFile?.NewPath);
                Assert.True(history.MarkReviewedCommand.CanExecute(null));
            }
            finally { window.Close(); }
        }
        finally { GitFixture.DeleteDirectory(root); }
    }

    [AvaloniaFact]
    public async Task History_window_opens_captured_layers_marks_reviews_and_survives_restart()
    {
        await using var f = new GitFixture(); await f.Linear(); var root = Directory.CreateTempSubdirectory("history-ui-").FullName;
        try
        {
            await new YamlStackStore().SaveAsync(f.Root, [f.Stack], null);
            using (var services = Composition.Build(root))
            {
                var vm = services.GetRequiredService<MainViewModel>(); await vm.OpenRepositoryAsync(f.Root);
                using var history = await vm.OpenHistoryAsync(); Assert.NotNull(history);
                var panel = new HistoryPanel { DataContext = history };
                var window = new Window { Content = panel }; window.Show();
                Assert.Equal(3, history.Nodes.Count); Assert.Single(history.Files);
                await history.MarkReviewedCommand.ExecuteAsync(null);
                Assert.Equal(FileReviewState.Reviewed, Assert.Single(history.Files).State);
                await history.ShowCurrentCommand.ExecuteAsync(null); Assert.Contains(history.DocumentSections, section => section.Result.Files.Any(file => file.NewPath == "A.txt"));
                await history.AttentionCommand.ExecuteAsync(null); Assert.Equal(2, history.Attention.Count);
                Assert.NotNull(panel.FindControl<ComboBox>("ReviewFiles"));
                window.Close(); await vm.ShutdownAsync();
            }
            using (var services = Composition.Build(root))
            {
                var vm = services.GetRequiredService<MainViewModel>(); await vm.OpenRepositoryAsync(f.Root);
                using var history = await vm.OpenHistoryAsync(); Assert.NotNull(history);
                Assert.Equal(FileReviewState.Reviewed, Assert.Single(history.Files).State);
                await vm.ShutdownAsync();
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
