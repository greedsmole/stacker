using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Infrastructure;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Xunit.Abstractions;
namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed class FoundationLoadTests(ITestOutputHelper output)
{
    [Fact]
    public void Discovery_handles_one_thousand_prs_in_twenty_layer_paths()
    {
        var prs = Enumerable.Range(0, 1000).Select(i => DiscoveryTests.Pr(i + 1, i % 20 == 0 ? "main" : "branch-" + (i - 1), "branch-" + i)).ToArray();
        _ = StackDiscovery.Discover(prs, ["main"]); var timings = new List<double>();
        for (var i = 0; i < 20; i++) { var watch = Stopwatch.StartNew(); var result = StackDiscovery.Discover(prs, ["main"]); timings.Add(watch.Elapsed.TotalMilliseconds); Assert.Equal(50, result.Stacks.Count); Assert.All(result.Stacks, s => Assert.Equal(20, s.Layers.Count)); }
        output.WriteLine($"1000 PR / 20 layers discovery p95: {P95(timings):F2} ms");
    }
    [Fact]
    public async Task Latest_graph_lookup_does_not_load_ten_thousand_versions()
    {
        var root = Directory.CreateTempSubdirectory("foundation-load-").FullName;
        try
        {
            var store = new SqliteHistoryStore(root); var node = new ChangeNode("change", "scope", "feature", "refs/heads/feature");
            var versions = Enumerable.Range(1, 10000).Select(i => new ChangeVersion(StableIdentity.For(node.Id + "|base|" + i), node.Id, "base", i.ToString(), null, DateTimeOffset.UnixEpoch.AddSeconds(i))).ToArray();
            var graph = new GraphSnapshot("scope", DateTimeOffset.UtcNow, [node], [], []) { Versions = new Dictionary<string, string> { { node.Id, versions[^1].Id } } };
            var watch = Stopwatch.StartNew(); (await store.ApplyObservationBatchAsync(new(graph, versions, 0))).OrThrow(); output.WriteLine($"10k versions atomic import: {watch.Elapsed.TotalMilliseconds:F2} ms");
            var timings = new List<double>();
            for (var i = 0; i < 20; i++) { watch.Restart(); var latest = (await store.LatestGraphAsync("scope")).OrThrow(); timings.Add(watch.Elapsed.TotalMilliseconds); Assert.Equal(versions[^1].Id, latest!.Versions[node.Id]); }
            output.WriteLine($"Latest graph with 10k versions p95: {P95(timings):F2} ms");
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task Large_diff_uses_a_single_batch_and_remains_virtualized()
    {
        await using var f = new GitFixture(); var file = new FileChange("large.txt", "large.txt", FileChangeKind.Added, 49998, 0, "000000", "100644");
        var patch = "diff --git a/large.txt b/large.txt\n@@ -0,0 +1,49998 @@\n" + string.Concat(Enumerable.Range(0, 49998).Select(i => "+line " + i + "\n"));
        var runner = new ScriptedRunner(_ => new(0, patch, "")); using var cache = new CachingGitReader(new GitRepositoryReader(runner, new()));
        using var section = new DiffSectionViewModel(cache, f.Root, new("layer", "base", new('a', 40), new('b', 40), null, [file]), readOnly: true);
        var notifications = 0; section.Lines.CollectionChanged += (_, _) => notifications++;
        var watch = Stopwatch.StartNew(); await section.LoadAsync(file); var cold = watch.Elapsed.TotalMilliseconds;
        var view = new DiffSectionView { DataContext = section }; var window = new Window { Width = 1200, Height = 800, Content = view }; window.Show();
        Assert.False(section.HasLoadError, section.Message); Assert.Equal(49999, section.Lines.Count); Assert.InRange(notifications, 1, 3);
        var list = view.FindControl<ListBox>("DiffLines")!;
        window.UpdateLayout(); view.Measure(new Avalonia.Size(1200, 800)); view.Arrange(new Avalonia.Rect(0, 0, 1200, 800)); view.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); list.UpdateLayout(); Assert.InRange(list.GetRealizedContainers().Count(), 1, 199); var realized = list.GetRealizedContainers().Count();
        var timings = new List<double>(); for (var i = 0; i < 10; i++) { watch.Restart(); await section.LoadAsync(file); view.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); list.UpdateLayout(); timings.Add(watch.Elapsed.TotalMilliseconds); }
        output.WriteLine($"50k diff rows cold: {cold:F2} ms; warm render p95: {P95(timings):F2} ms; realized controls: {realized}"); window.Close();
    }
    private static double P95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1);
}
