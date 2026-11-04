using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Core.Features.Review;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;

public sealed class FoundationRecoveryTests
{
    private static string Temp()=>Directory.CreateTempSubdirectory("stacker-recovery-").FullName;
    [Fact] public async Task Clearing_leased_contents_waits_until_last_reader_releases_it()
    {
        var root=Temp();try
        {
            var path=Path.Combine(root,"history-objects","one");var objects=new ObjectStore(new ApplicationStore(root));
            var a=(await objects.AcquireAsync(path,true)).OrThrow();var b=(await objects.AcquireAsync(path)).OrThrow();
            Assert.False((await objects.RemoveAsync(path)).OrThrow());a.Dispose();Assert.True(Directory.Exists(path));b.Dispose();(await objects.TrimAsync()).OrThrow();Assert.False(Directory.Exists(path));
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Disposable_budget_never_evicts_active_or_historical_contents()
    {
        var root=Temp();try
        {
            var objects=new ObjectStore(new ApplicationStore(root),10);
            foreach(var name in new[]{"a","b","c"}){var path=Path.Combine(root,"objects",name);Directory.CreateDirectory(path);await File.WriteAllTextAsync(Path.Combine(path,"blob"),"12345678");}
            var history=Path.Combine(root,"history-objects","old");Directory.CreateDirectory(history);await File.WriteAllTextAsync(Path.Combine(history,"blob"),"historical contents");
            using var lease=(await objects.AcquireAsync(Path.Combine(root,"objects","a"))).OrThrow();
            var status=(await objects.TrimAsync()).OrThrow();Assert.True(status.DisposableBytes<=10);Assert.True(Directory.Exists(history));Assert.True(Directory.Exists(lease.Root));
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Backup_roundtrip_preserves_drafts_and_refuses_overwriting_existing_workspace()
    {
        var root=Temp();var target=Temp();var archive=Path.Combine(target,"workspace.zip");var restored=Path.Combine(target,"restored");
        try
        {
            var state=new ApplicationStore(root);var store=new SqliteHistoryStore(root);
            (await store.SaveDraftAsync("key",new ReviewDraft{Summary="unsent"},0)).OrThrow();
            var backup=new WorkspaceBackupService(state,store,new ProcessRunner());
            (await backup.ExportAsync(archive)).OrThrow();(await backup.RestoreAsync(archive,restored)).OrThrow();
            Assert.Equal("unsent",(await new SqliteHistoryStore(restored).ReadDraftAsync("key")).OrThrow()!.Draft.Summary);
            Assert.True((await backup.RestoreAsync(archive,restored)).IsFailed);
        }finally{Directory.Delete(root,true);Directory.Delete(target,true);}
    }
    [Fact] public async Task Bus_refusal_before_dispatch_is_certainly_not_started()
    {
        var root=Temp();try
        {
            var journal=new JsonJournal(new JsonStateStore<JournalLog>(new AtomicFile(),root),new SystemClock());var bus=new CommandBus(new(journal));
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running=bus.RunRemoteAsync(FakeGitHub.Context,new CommentCommand(1,async(_,_)=>{entered.SetResult();await release.Task;return Result.Ok;}));await entered.Task;
            IGitHubWriter writer=new CommandGitHubWriter(new GitHubCli(new ProcessRunner(),new(),new()),bus);
            var result=await writer.CommentAsync(FakeGitHub.Context,42,"should not send");Assert.Equal(EffectCertainty.NotStarted,result.Problem?.Certainty);
            release.SetResult();await running;
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Publication_payload_and_journal_share_operation_id_and_confirmation_transaction()
    {
        var root=Temp();try
        {
            var store=new SqliteHistoryStore(root);var journal=new RoutingJournal(new JsonJournal(new JsonStateStore<JournalLog>(new AtomicFile(),Path.Combine(root,"journal")),new SystemClock()),store,new SystemClock());
            var bus=new CommandBus(new(journal));var draft=new ReviewDraft{PendingBody="sent",PendingOperation="comment|"+new string('a',32)};
            var pr=DiscoveryTests.Pr(42,"main","feature");ReviewSessionService.Prepare(draft,new string('a',32),"comment",FakeGitHub.Context,pr);
            (await store.SaveDraftAsync("key",draft,0)).OrThrow();ReviewSessionService.Transition(draft,PublicationPhase.Sending);
            (await store.SaveDraftAsync("key",draft,1)).OrThrow();
            using(OperationCorrelation.Enter(new string('a',32)))Assert.True((await bus.RunRemoteAsync(FakeGitHub.Context,new CommentCommand(42,(_,_)=>Task.FromResult(Result.Fail(ProblemCodes.GitHubRequest,"Connection lost"))))).NeedsUser);
            var entry=Assert.Single(journal.Recent("github:"+FakeGitHub.Context.Identity).OrThrow());Assert.Equal(new string('a',32),entry.Id);Assert.True(entry.NeedsAttention);Assert.Equal(StepState.Unknown,entry.Steps[0].State);
            ReviewSessionService.Transition(draft,PublicationPhase.Confirmed);draft.PendingOperation=null;(await store.SaveDraftAsync("key",draft,2)).OrThrow();
            using var c=new SqliteConnection("Data Source="+Path.Combine(root,"review-history.db")+";Pooling=False");c.Open();using var check=c.CreateCommand();check.CommandText="SELECT phase FROM publication_operations";
            Assert.Equal((long)PublicationPhase.Confirmed,check.ExecuteScalar());Assert.Null((await store.ReadDraftAsync("key")).OrThrow()!.Draft.PendingOperation);
            Assert.Equal(JournalPhase.Applied,Assert.Single(journal.Recent("github:"+FakeGitHub.Context.Identity).OrThrow()).Phase);
        }finally{Directory.Delete(root,true);}
    }

    [Fact] public async Task Prepared_but_not_dispatched_publication_does_not_block_after_restart()
    {
        var root=Temp();try
        {
            var database=new SqliteHistoryStore(root);var app=new ReviewApplicationState(new(root),database);var draft=new ReviewDraft{Composer="keep",PendingOperation="comment|"+new string('c',32)};
            ReviewSessionService.Prepare(draft,new string('c',32),"comment",FakeGitHub.Context,DiscoveryTests.Pr(42,"main","feature"));(await database.SaveDraftAsync("key",draft,0)).OrThrow();
            var session=new ReviewSessionService(app,new FakeGitHub());var loaded=(await session.LoadAsync("key")).OrThrow()!;
            Assert.Null(loaded.PendingOperation);Assert.Null(loaded.Operation);Assert.Equal("keep",loaded.Composer);Assert.Equal(PublicationPhase.Rejected,Assert.Single(loaded.PublicationHistory).Phase);
            Assert.True((await session.SaveAsync("key",loaded)).IsOk);
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Operation_payload_cannot_be_changed_under_the_same_id()
    {
        var root=Temp();try
        {
            var database=new SqliteHistoryStore(root);var draft=new ReviewDraft{PendingBody="original"};
            ReviewSessionService.Prepare(draft,new string('d',32),"comment",FakeGitHub.Context,DiscoveryTests.Pr(42,"main","feature"));(await database.SaveDraftAsync("key",draft,0)).OrThrow();
            draft.Operation=draft.Operation! with{Payload="changed"};Assert.True((await database.SaveDraftAsync("key",draft,1)).IsFailed);
            var loaded=(await database.ReadDraftAsync("key")).OrThrow()!;Assert.Equal(1,loaded.Revision);Assert.Contains("original",loaded.Draft.Operation!.Payload);
        }finally{Directory.Delete(root,true);}
    }
}
