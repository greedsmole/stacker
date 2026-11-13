using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class HistoryTests
{
    private static string Temp() => Directory.CreateTempSubdirectory("stacker-history-test-").FullName;
    [Fact]
    public async Task Versions_deduplicate_and_preserve_original_observation_after_restart()
    {
        var root = Temp();
        try
        {
            IHistoryStore store = new SqliteHistoryStore(root);
            var node = (await store.ResolveNodeAsync("scope", "A", "A")).OrThrow();
            var now = DateTimeOffset.UtcNow;
            var a = (await store.ObserveAsync(new("", node.Id, "b", "h", null, now))).OrThrow();
            var b = (await store.ObserveAsync(new("", node.Id, "b", "h", "m", now.AddMinutes(1)))).OrThrow();
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(now, b.ObservedAt);
            Assert.Equal("m", b.MergeBase);
            store = new SqliteHistoryStore(root);
            Assert.Single((await store.VersionsAsync(node.Id)).OrThrow());
            Assert.Equal(node.Id, (await store.ResolveNodeAsync("scope", "A", "Renamed title")).OrThrow().Id);
            Assert.NotEqual(node.Id, (await store.ResolveNodeAsync("other account", "A", "A")).OrThrow().Id);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Draft_migration_is_idempotent_and_concurrent_writers_are_rejected()
    {
        var root = Temp();
        try
        {
            var legacy = new ApplicationStore(root);
            var history = new SqliteHistoryStore(root);
            await legacy.WriteAsync("drafts", "key", new ReviewDraft { Summary = "original" });
            IApplicationState a = new ReviewApplicationState(legacy, history), b = new ReviewApplicationState(legacy, history);
            var draft = (await a.ReadAsync<ReviewDraft>("drafts", "key")).OrThrow()!;
            Assert.Equal("original", (await b.ReadAsync<ReviewDraft>("drafts", "key")).OrThrow()!.Summary);
            draft.Summary = "first writer";
            Assert.True((await a.WriteAsync("drafts", "key", draft)).IsOk);
            var conflict = await b.WriteAsync("drafts", "key", new ReviewDraft { Summary = "lost update" });
            Assert.Equal(ProblemCodes.StoreStaleRevision, conflict.Problem?.Code);
            Assert.Equal("original", (await legacy.ReadAsync<ReviewDraft>("drafts", "key"))!.Summary);
            var reopened = new ReviewApplicationState(legacy, history);
            Assert.Equal("first writer", (await reopened.ReadAsync<ReviewDraft>("drafts", "key")).OrThrow()!.Summary);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Malformed_legacy_draft_is_preserved_and_never_imported_as_empty()
    {
        var root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "drafts"));
            var file = Path.Combine(root, "drafts", ApplicationStore.Key("key") + ".json");
            await File.WriteAllTextAsync(file, "{broken");
            var state = new ReviewApplicationState(new(root), new(root));
            Assert.True((await state.ReadAsync<ReviewDraft>("drafts", "key")).IsFailed);
            Assert.Equal("{broken", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Shared_foundation_is_one_node_and_old_graph_survives_removed_definition()
    {
        var root = Temp();
        try
        {
            var store = new SqliteHistoryStore(root);
            var service = new ChangeGraphService(store);
            var repo = new RepositorySnapshot("repo", [], 0);
            var graph = await service.ObserveLocalAsync(repo,
                [new("ab", "AB", "main", ["A", "B"]), new("ac", "AC", "main", ["A", "C"])], default);
            Assert.Equal(3, graph.Nodes.Count);
            Assert.Equal(2, graph.Edges.Count);
            Assert.Equal(2, ChangeGraphService.Descendants(graph.Nodes.Single(n => n.Reference == "A").Id, graph.Edges).Count);
            await service.ObserveLocalAsync(repo, [], default);
            Assert.Equal(2, (await store.GraphsAsync("local:repo")).OrThrow().Count);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Rebase_normalization_keeps_own_patch_and_detects_new_edit_without_mutating_checkout()
    {
        await using var f = new GitFixture();
        await f.Init();
        var root = Temp();
        try
        {
            var basis = await f.Git("rev-parse", "HEAD");
            await f.Git("checkout", "-b", "A");
            await f.Write("own.txt", "own\n");
            await f.Commit("own");
            var oldHead = await f.Git("rev-parse", "HEAD");
            var objects = new HistoryGit(f.Runner, new(), new ApplicationStore(root));
            await objects.CaptureAsync(f.Identity, "scope", [basis, oldHead]);
            await f.Git("checkout", "main");
            await f.Write("upstream.txt", "upstream\n");
            await f.Commit("upstream");
            var nextBase = await f.Git("rev-parse", "HEAD");
            await f.Git("checkout", "A");
            await f.Git("rebase", "main");
            var nextHead = await f.Git("rev-parse", "HEAD");
            (await objects.CaptureAsync(f.Identity, "scope", [nextBase, nextHead])).OrThrow();
            var privateRoot = f.Root;
            var service = new VersionComparisonService(f.Reader, objects);
            var old = new ChangeVersion("old", "A", basis, oldHead, basis, DateTimeOffset.UtcNow);
            var current = new ChangeVersion("current", "A", nextBase, nextHead, nextBase, DateTimeOffset.UtcNow);
            var refs = await f.Git("show-ref");
            var index = await f.Git("write-tree");
            var comparison = await service.CompareAsync(f.Identity, "scope", old, current);
            Assert.Null(comparison.Unavailable);
            Assert.Empty(comparison.CodeChanges);
            Assert.Single(comparison.BaseChanges);
            Assert.Equal(refs, await f.Git("show-ref"));
            Assert.Equal(index, await f.Git("write-tree"));
            Assert.Equal("", await f.Git("status", "--porcelain"));
            await f.Write("own.txt", "own\nfix\n");
            await f.Commit("fix");
            var fixedHead = await f.Git("rev-parse", "HEAD");
            (await objects.CaptureAsync(f.Identity, "scope", [fixedHead])).OrThrow();
            var fixedComparison = await service.CompareAsync(f.Identity, "scope", old, current with
            {
                HeadSha = fixedHead
            });
            Assert.Equal("own.txt", Assert.Single(fixedComparison.CodeChanges).NewPath);
            // A force-updated local ref cannot destroy captured historical objects.
            await f.Git("update-ref", "refs/heads/A", nextBase);
            Assert.Contains("/" + oldHead, await f.GitAt(privateRoot, "show-ref"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Conflicting_replay_is_explicitly_unavailable_and_trial_merge_lists_paths()
    {
        await using var f = new GitFixture();
        await f.Init();
        var root = Temp();
        try
        {
            var basis = await f.Git("rev-parse", "HEAD");
            await f.Git("checkout", "-b", "A");
            await f.Write("base.txt", "left\n");
            await f.Commit("left");
            var head = await f.Git("rev-parse", "HEAD");
            await f.Git("checkout", "main");
            await f.Write("base.txt", "right\n");
            await f.Commit("right");
            var next = await f.Git("rev-parse", "HEAD");
            var objects = new HistoryGit(f.Runner, new(), new ApplicationStore(root));
            (await objects.CaptureAsync(f.Identity, "scope", [basis, head, next])).OrThrow();
            var cache = f.Root;
            var comparison = await new VersionComparisonService(f.Reader, objects).CompareAsync(f.Identity, "scope",
                new("old", "A", basis, head, basis, DateTimeOffset.UtcNow), new("new", "A", next, next, next, DateTimeOffset.UtcNow));
            Assert.False(comparison.IsReliable);
            Assert.Contains("conflicts", comparison.Unavailable);
            Assert.Contains("base.txt", (await objects.MergeAsync(f.Identity, head, next)).OrThrow());
            Assert.Null((await objects.TransplantAsync(f.Identity, basis, head, next)).OrThrow());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Anchor_mapping_requires_unique_context_and_explicit_new_anchor()
    {
        var anchor = new ReviewAnchor(1, "old", "base", "a.cs", "RIGHT", 3);
        var pr = new PullRequest(1, "A", 1, "main", "newbase", 1, "A", "newhead", false, "");
        var old = "a\nb\nc\nd\ne";
        var proposal = AnchorMapping.Propose(anchor, pr, "renamed.cs", old, "prefix\n" + old);
        Assert.NotNull(proposal);
        Assert.Equal(4, proposal.Proposed.Line);
        Assert.Equal("old", proposal.Original.HeadSha);
        Assert.Null(AnchorMapping.Propose(anchor, pr, "a.cs", old, old + "\n" + old));
        Assert.Null(AnchorMapping.Propose(anchor, pr, "a.cs", old, "changed"));
    }
    [Fact]
    public void Publication_payload_is_immutable_and_transitions_reject_resend()
    {
        var draft = new ReviewDraft { PendingBody = "first" };
        var context = new GitHubRepositoryContext("host", 1, "owner", "repo", "", "user", "main");
        var pr = new PullRequest(1, "A", 1, "main", "base", 1, "A", "head", false, "");
        ReviewSessionService.Prepare(draft, "id", "comment", context, pr);
        draft.PendingBody = "later";
        Assert.Contains("first", draft.Operation!.Payload);
        Assert.DoesNotContain("later", draft.Operation.Payload);
        ReviewSessionService.Transition(draft, PublicationPhase.Sending);
        ReviewSessionService.Transition(draft, PublicationPhase.Uncertain);
        Assert.Throws<StackerException>(() => ReviewSessionService.Prepare(draft, "again", "comment", context, pr));
        ReviewSessionService.Transition(draft, PublicationPhase.Confirmed);
        Assert.Null(draft.Operation);
        Assert.Single(draft.PublicationHistory);
    }
}
