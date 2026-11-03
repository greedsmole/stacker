using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class FoundationStateTests
{
    private static string Temp()=>Directory.CreateTempSubdirectory("stacker-foundation-").FullName;
    [Fact] public async Task Legacy_database_migrates_with_backup_and_enforces_relationships()
    {
        var root=Temp();try
        {
            var node=new ChangeNode("node","scope","A","A");var id=ApplicationStore.Key("node|base|head");
            var version=new ChangeVersion(id,node.Id,"base","head","base",DateTimeOffset.UtcNow);
            using(var c=new SqliteConnection("Data Source="+Path.Combine(root,"review-history.db")))
            {
                c.Open();using var setup=c.CreateCommand();setup.CommandText="CREATE TABLE documents(kind TEXT,id TEXT,json TEXT,revision INTEGER DEFAULT 1,PRIMARY KEY(kind,id)); PRAGMA user_version=1;";setup.ExecuteNonQuery();
                foreach(var(kind,key,json)in new[]{("node","scope|A",JsonSerializer.Serialize(node)),("version",id,JsonSerializer.Serialize(version)),("draft","key",JsonSerializer.Serialize(new ReviewDraft{Summary="preserved"}))})
                {using var put=c.CreateCommand();put.CommandText="INSERT INTO documents(kind,id,json) VALUES($k,$i,$j)";put.Parameters.AddWithValue("$k",kind);put.Parameters.AddWithValue("$i",key);put.Parameters.AddWithValue("$j",json);put.ExecuteNonQuery();}
            }
            var store=new SqliteHistoryStore(root);Assert.Equal("base",Assert.Single((await store.VersionsAsync("node")).OrThrow()).MergeBase);
            Assert.Equal("preserved",(await store.ReadDraftAsync("key")).OrThrow()!.Draft.Summary);
            Assert.Single(Directory.GetFiles(Path.Combine(root,"backups"),"*.db"));
            Assert.True((await store.SetCheckpointAsync(new("missing","f","missing",DateTimeOffset.UtcNow),false)).IsFailed);
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Observation_batch_rolls_back_on_missing_graph_version_and_rejects_stale_edits()
    {
        var root=Temp();try
        {
            var store=new SqliteHistoryStore(root);var node=new ChangeNode("new-node","s","A","A");
            var graph=new GraphSnapshot("s",DateTimeOffset.UtcNow,[node],[],[]){Versions=new Dictionary<string,string>{{node.Id,"missing-version"}}};
            Assert.True((await store.ApplyObservationBatchAsync(new(graph,[],0))).IsFailed);
            Assert.Null((await store.FindNodeAsync("s","A")).OrThrow());Assert.Null((await store.LatestGraphAsync("s")).OrThrow());
            var saved=(await store.ApplyObservationBatchAsync(new(graph with{Versions=new Dictionary<string,string>()},[],0))).OrThrow();
            Assert.Equal(1,saved.Revision);
            Assert.True((await store.SaveGraphAsync(saved with{Warnings=["new"]})).IsOk);
            Assert.True((await store.SaveGraphAsync(saved with{Warnings=["stale"]})).IsFailed);
            Assert.Equal("new",Assert.Single((await store.LatestGraphAsync("s")).OrThrow()!.Warnings));
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Worktrees_share_repository_identity_and_relocation_keeps_it()
    {
        await using var f=new GitFixture();await f.Init();var root=Temp();
        try
        {
            var service=new ChangeIdentityService(new(root),f.Runner,new());
            var first=(await service.RegisterAsync(await f.ResolvedRoot())).OrThrow();var worktree=await f.AddWorktree();
            var second=(await service.RegisterAsync(worktree)).OrThrow();Assert.Equal(first.Repository,second.Repository);Assert.NotEqual(first.Worktree,second.Worktree);
            Assert.Equal(first.Repository,(await service.RelocateAsync(first.Repository,first.Root)).OrThrow().Repository);
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Bound_local_and_remote_changes_share_checkpoints_but_other_accounts_do_not()
    {
        await using var f=new GitFixture();await f.Init();var root=Temp();
        try
        {
            var store=new SqliteHistoryStore(root);var identities=new ChangeIdentityService(store,f.Runner,new());
            var local=(await store.ResolveNodeAsync("local:r","refs/heads/feature","feature")).OrThrow();
            var version=(await store.ObserveAsync(new("",local.Id,"base","head","base",DateTimeOffset.UtcNow))).OrThrow();
            (await store.SetCheckpointAsync(new(local.Id,"code.cs",version.Id,DateTimeOffset.UtcNow),false)).OrThrow();
            var pr=DiscoveryTests.Pr(42,"main","feature");
            (await identities.BindAsync("local:r","refs/heads/feature",FakeGitHub.Context,pr)).OrThrow();
            var remote=(await store.FindNodeAsync(FakeGitHub.Context.Identity,"feature",42)).OrThrow()!;
            Assert.Equal("code.cs",Assert.Single((await store.CheckpointsAsync(remote.Id)).OrThrow()).Path);
            Assert.Equal(remote.Id,Assert.Single((await store.VersionsAsync(remote.Id)).OrThrow()).ChangeId);
            var other=FakeGitHub.Context with{User="other"};
            Assert.True((await identities.BindAsync("local:r","refs/heads/feature",other,pr)).IsFailed);
            var otherNode=(await store.FindNodeAsync(other.Identity,"feature",42)).OrThrow()!;
            Assert.Empty((await store.CheckpointsAsync(otherNode.Id)).OrThrow());
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public void Workspace_only_applies_pending_snapshot_explicitly_and_invalidates_old_queries()
    {
        var session=new WorkspaceSession();session.Open(new("r",[],0));var ticket=session.Ticket;
        var snapshot=new GitHubSnapshot(FakeGitHub.Context,[],DateTimeOffset.UtcNow);session.Offer(snapshot,null);
        Assert.Null(session.Displayed.Remote);Assert.NotNull(session.Pending);session.ApplyPending();Assert.Equal(snapshot,session.Displayed.Remote);Assert.False(session.IsCurrent(ticket));
        ticket=session.Ticket;session.Select(new("change","comparison","file"));Assert.False(session.IsCurrent(ticket));
    }
    [Fact] public async Task Query_sharing_cancels_a_subscriber_without_cancelling_the_other()
    {
        using var queries=new QueryCoordinator();var gate=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        using var cancelled=new CancellationTokenSource();
        Task<int> Read(CancellationToken ct){Interlocked.Increment(ref calls);return gate.Task.WaitAsync(ct);}
        var first=queries.RunAsync("same",Read,cancelled.Token);var second=queries.RunAsync("same",Read);
        cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);gate.SetResult(42);
        Assert.Equal(42,await second);Assert.Equal(1,calls);
    }
    [Fact] public async Task Autosave_keeps_only_the_latest_pending_snapshot()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var writes=new List<int>();
        var writer=new CoalescingWriter<int>(async n=>{writes.Add(n);if(writes.Count==1){entered.SetResult();await release.Task;}},TimeSpan.Zero);
        _ = writer.Queue(0);await entered.Task;
        for(var i=1;i<=500;i++)_ = writer.Queue(i);
        Assert.Equal(1,writer.PendingCount);release.SetResult();await writer.FlushAsync();Assert.Equal(new[]{0,500},writes);
    }
    [Fact] public async Task Workspace_imports_path_based_navigation_once_and_then_follows_worktree_identity()
    {
        var root=Temp();try
        {
            IApplicationState app=new ApplicationStore(root);
            var original=new WorkspaceState{ActiveGroup="local:stack"};original.Stacks["local:stack"]=new(){Expanded=false,Revision=1};
            var legacy=new WorkspacePersistence(app);(await legacy.SaveAsync("old-path",original)).OrThrow();
            var persistence=new WorkspacePersistence(app);var imported=(await persistence.LoadAsync("worktree:stable",legacyRoot:"old-path")).OrThrow();
            Assert.False(imported.Stacks["local:stack"].Expanded);(await persistence.SaveAsync("worktree:stable",imported)).OrThrow();
            var relocated=(await new WorkspacePersistence(app).LoadAsync("worktree:stable",legacyRoot:"new-path")).OrThrow();
            Assert.Equal("local:stack",relocated.ActiveGroup);Assert.False(relocated.Stacks["local:stack"].Expanded);
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Reappearing_ref_reopens_its_existing_change_identity()
    {
        var root=Temp();try
        {
            var service=new ChangeGraphService(new SqliteHistoryStore(root));var stack=new StackDefinition("one","one","base",["feature"]);
            var repo=new RepositorySnapshot("repo",[new("base","base"),new("feature","head")],0);
            var first=Assert.Single((await service.ObserveLocalAsync(repo,[stack],default)).Nodes);
            var missing=await service.ObserveLocalAsync(repo with{Refs=[new("base","base")]},[stack],default);Assert.Equal("Missing",Assert.Single(missing.Nodes).State);
            var restored=Assert.Single((await service.ObserveLocalAsync(repo,[stack],default)).Nodes);Assert.Equal(first.Id,restored.Id);Assert.Equal("Open",restored.State);
        }finally{Directory.Delete(root,true);}
    }
}
