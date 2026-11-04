using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed class HistoryWorkflowTests
{
    [Fact] public async Task Progress_preserves_unmodified_files_and_invalidates_base_context()
    {
        await using var f = new GitFixture(); await f.Init(); var root = Directory.CreateTempSubdirectory("review-progress-").FullName;
        try
        {
            var basis = await f.Git("rev-parse", "HEAD"); await f.Git("checkout", "-b", "A");
            await f.Write("a.txt", "a\n"); await f.Write("b.txt", "b\n"); await f.Commit("both"); var head = await f.Git("rev-parse", "HEAD");
            var store = new SqliteHistoryStore(root); var objects = new HistoryGit(f.Runner, new(), new ApplicationStore(root));
            (await objects.CaptureAsync(f.Identity,"scope", [basis, head])).OrThrow(); var cache=f.Root;
            var node = (await store.ResolveNodeAsync("scope", "A", "A")).OrThrow();
            var old = (await store.ObserveAsync(new("", node.Id, basis, head, basis, DateTimeOffset.UtcNow))).OrThrow();
            var progress = new ReviewProgressService(store, new(f.Reader, objects));
            foreach (var path in new[] { "a.txt", "b.txt" }) (await store.SetCheckpointAsync(new(node.Id, path, old.Id, DateTimeOffset.UtcNow), false)).OrThrow();
            await f.Write("a.txt", "fix\n"); await f.Commit("fix"); head = await f.Git("rev-parse", "HEAD");
            (await objects.CaptureAsync(f.Identity,"scope", [head])).OrThrow();
            var current = (await store.ObserveAsync(new("", node.Id, basis, head, basis, DateTimeOffset.UtcNow))).OrThrow();
            var files = (await f.Reader.FilesAsync(cache, basis, head));
            var status = await progress.StatusAsync(f.Identity,"scope", current, files);
            Assert.Equal(FileReviewState.CodeChanged, status.Single(s => s.Change.NewPath == "a.txt").State);
            Assert.Equal(FileReviewState.Reviewed, status.Single(s => s.Change.NewPath == "b.txt").State);
            await f.Git("checkout", "main"); await f.Write("upstream", "upstream\n"); await f.Commit("upstream"); var nextBase = await f.Git("rev-parse", "HEAD");
            await f.Git("checkout", "A"); await f.Git("rebase", "main"); head = await f.Git("rev-parse", "HEAD");
            (await objects.CaptureAsync(f.Identity,"scope", [head, nextBase])).OrThrow();
            current = (await store.ObserveAsync(new("", node.Id, nextBase, head, nextBase, DateTimeOffset.UtcNow))).OrThrow();
            status = await progress.StatusAsync(f.Identity,"scope", current, await f.Reader.FilesAsync(cache, nextBase, head));
            Assert.Equal(FileReviewState.BaseChanged, status.Single(s => s.Change.NewPath == "b.txt").State);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task File_evolution_follows_rename_and_attributes_latest_added_line()
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
    [Fact] public async Task Stack_analysis_uses_graph_versions_even_after_new_metadata_arrives()
    {
        await using var f = new GitFixture(); await f.Init(); var root = Directory.CreateTempSubdirectory("stack-analysis-").FullName;
        try
        {
            var basis = await f.Git("rev-parse", "HEAD"); await f.Git("checkout", "-b", "A"); await f.Write("base.txt", "A\n"); await f.Commit("A"); var a = await f.Git("rev-parse", "HEAD");
            await f.Git("checkout", "-b", "B", basis); await f.Write("base.txt", "B\n"); await f.Commit("B"); var b = await f.Git("rev-parse", "HEAD");
            var store = new SqliteHistoryStore(root); var objects = new HistoryGit(f.Runner, new(), new ApplicationStore(root));
            (await objects.CaptureAsync(f.Identity,"s", [basis, a, b])).OrThrow();
            var na = (await store.ResolveNodeAsync("s", "A", "A")).OrThrow(); var nb = (await store.ResolveNodeAsync("s", "B", "B")).OrThrow();
            var va = (await store.ObserveAsync(new("", na.Id, basis, a, basis, DateTimeOffset.UtcNow))).OrThrow();
            var vb = (await store.ObserveAsync(new("", nb.Id, basis, b, basis, DateTimeOffset.UtcNow))).OrThrow();
            var graph = new GraphSnapshot("s", DateTimeOffset.UtcNow, [na, nb], [new(na.Id, nb.Id, "explicit")], [])
                { Versions = new Dictionary<string, string> { [na.Id] = va.Id, [nb.Id] = vb.Id } };
            (await store.ObserveAsync(new("", nb.Id, basis, new string('f', 40), null, DateTimeOffset.UtcNow.AddMinutes(1)))).OrThrow();
            var result = await new StackAnalysisService(store, objects, f.Reader).AnalyzeAsync(f.Identity,graph);
            Assert.Contains(result.Issues, i => i.Kind == "StaleParent"); Assert.Contains(result.Issues, i => i.Kind == "Overlap"); Assert.Contains(result.Issues, i => i.Kind == "Conflict");
            Assert.DoesNotContain(result.Issues, i => i.Kind == "Unavailable");
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact] public async Task History_window_opens_captured_layers_marks_reviews_and_survives_restart()
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
                Assert.NotNull(panel.FindControl<ListBox>("ReviewFiles"));
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
