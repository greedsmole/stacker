using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed partial class HistorySafetyTests
{
    [Fact]
    public async Task Closed_parent_is_retained_and_explicit_edges_survive_refresh()
    {
        var root = Directory.CreateTempSubdirectory("graph-safety-").FullName;
        try
        {
            var store = new SqliteHistoryStore(root);
            var remote = new ClosedReader();
            var service = new ChangeGraphService(store, remote);
            var prs = new[] { DiscoveryTests.Pr(1, "main", "A"), DiscoveryTests.Pr(2, "A", "B"), DiscoveryTests.Pr(3, "main", "C") };
            var snapshot = new GitHubSnapshot(FakeGitHub.Context, prs, DateTimeOffset.UtcNow);
            var initial = await service.ObserveRemoteAsync(snapshot, ["main"], default);
            var b = initial.Nodes.Single(n => n.PullRequest == 2);
            var c = initial.Nodes.Single(n => n.PullRequest == 3);
            (await store.SaveGraphAsync(initial with
            {
                Edges = [new(c.Id, b.Id, "explicit")]
            })).OrThrow();
            var next = await service.ObserveRemoteAsync(snapshot, ["main"], default);
            Assert.Equal(c.Id, Assert.Single(next.Edges).Parent);
            var discovery = GraphDiscovery.Discover(snapshot, next);
            Assert.Equal(new long[] { 3, 2 }, Assert.Single(discovery.Stacks).Layers.Select(p => p.Number));
            next = await service.ObserveRemoteAsync(snapshot with
            {
                PullRequests = prs.Skip(1).ToArray()
            }, ["main"], default);
            Assert.Equal("Merged", next.Nodes.Single(n => n.PullRequest == 1).State);
            await service.ObserveRemoteAsync(snapshot with
            {
                PullRequests = prs.Skip(1).ToArray()
            }, ["main"], default);
            Assert.Equal(1, remote.Reads);
        }
        finally { GitFixture.DeleteDirectory(root); }
    }
    [Fact]
    public async Task Rebinding_local_ref_preserves_identity_and_rejects_independent_history()
    {
        var root = Directory.CreateTempSubdirectory("rename-history-").FullName;
        try
        {
            var store = new SqliteHistoryStore(root);
            var old = (await store.ResolveNodeAsync("local:repo", "refs/heads/old", "old")).OrThrow();
            Assert.True((await store.RebindLocalNodeAsync(old, "refs/heads/new")).IsOk);
            Assert.Equal(old.Id, (await store.ResolveNodeAsync("local:repo", "refs/heads/new", "new")).OrThrow().Id);
            var independent = (await store.ResolveNodeAsync("local:repo", "refs/heads/independent", "other")).OrThrow();
            Assert.True((await store.RebindLocalNodeAsync(independent, "refs/heads/new")).IsFailed);
        }
        finally { GitFixture.DeleteDirectory(root); }
    }
    [Fact]
    public async Task Future_schema_is_not_downgraded_or_replaced()
    {
        var root = Directory.CreateTempSubdirectory("future-history-").FullName;
        try
        {
            var path = Path.Combine(root, "review-history.db");
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA user_version=99
                    """;
                command.ExecuteNonQuery();
            }
            Assert.True((await new SqliteHistoryStore(root).ResolveNodeAsync("s", "A", "A")).IsFailed);
            using var verify = new SqliteConnection("Data Source=" + path);
            verify.Open();
            using var version = verify.CreateCommand();
            version.CommandText = """
                PRAGMA user_version
                """;
            Assert.Equal(99L, version.ExecuteScalar());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task Offline_demo_contains_a_reviewed_older_version_and_no_publication()
    {
        var root = Directory.CreateTempSubdirectory("history-demo-").FullName;
        try
        {
            using var services = Composition.Build(root);
            var vm = services.GetRequiredService<MainViewModel>();
            await vm.OpenDemoAsync();
            Assert.False(vm.HasError, vm.Error);
            using var history = await vm.OpenHistoryAsync();
            Assert.NotNull(history);
            history.SelectedNode = history.Nodes.Single(n => n.Reference == "refs/heads/auth/api");
            await history.LoadLayerCommand.ExecuteAsync(null);
            Assert.NotEmpty(history.Versions);
            Assert.Equal(FileReviewState.CodeChanged, Assert.Single(history.Files).State);
            await history.SinceReviewedCommand.ExecuteAsync(null);
            Assert.Contains("Own changes after replay", history.Preview);
            Assert.Contains(history.DocumentSections, section => section.Patch.Contains("member") && section.Patch.Contains("user"));
            history.ConcernText = "Check authorization invariant";
            await history.FlagBlockerCommand.ExecuteAsync(null);
            await history.AttentionCommand.ExecuteAsync(null);
            Assert.Contains(history.Attention, item => item.Label.Contains("BLOCKER"));
            var store = services.GetRequiredService<IHistoryStore>();
            var node = history.SelectedNode!;
            (await store.SetConcernAsync(new(node.Id, "no-longer-changed.cs", "Still needs a decision"), false)).OrThrow();
            await history.AttentionCommand.ExecuteAsync(null);
            history.SelectedAttention = history.Attention.Single(i => i.Path == "no-longer-changed.cs");
            Assert.True(history.SelectedAttention.HasConcern);
            await history.ClearQueuedBlockerCommand.ExecuteAsync(null);
            await history.AttentionCommand.ExecuteAsync(null);
            Assert.DoesNotContain(history.Attention, i => i.Path == "no-longer-changed.cs");
            Assert.True(vm.IsDemo);
            Assert.True(vm.ActiveReview is null || !vm.ActiveReview.CanPublish);
            await vm.ShutdownAsync();
        }
        finally { GitFixture.DeleteDirectory(root); }
    }
}
