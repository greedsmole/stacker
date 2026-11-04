using System.IO.Compression;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class LocalRepositoryHistoryTests
{
    [Fact] public async Task Legacy_history_is_imported_from_each_opened_clone_without_commands_in_the_legacy_folder()
    {
        await using var f=new GitFixture();await f.Init();await using var other=new GitFixture();await other.Init();var appRoot=Directory.CreateTempSubdirectory("local-history-import-").FullName;
        try
        {
            var basis=await f.Git("rev-parse","HEAD");await f.Git("checkout","-b","old-feature");await f.Write("old.txt","preserved history\n");await f.Commit("old feature");var old=await f.Git("rev-parse","HEAD");
            var legacy=Path.Combine(appRoot,"history-objects",ApplicationStore.Key("scope"));Directory.CreateDirectory(legacy);
            await f.GitAt(legacy,"init","--bare");await f.GitAt(legacy,"fetch","--no-tags",f.Root,"+refs/heads/old-feature:refs/stacker/history/"+old);
            await f.Git("checkout","main");await f.Git("branch","-D","old-feature");await Collect(f);Assert.NotEqual(0,await f.ExitCode("cat-file","-e",old));
            var trace=new RepositoryRecordingRunner(f.Runner);var git=new RepositoryGit(trace,new());var app=new ApplicationStore(appRoot);var database=new SqliteHistoryStore(appRoot);var history=new HistoryGit(trace,new(),app,git,database);
            (await history.CaptureAsync(f.Identity,"scope",[basis,old])).OrThrow();Assert.Equal(old,await f.Git("rev-parse",old));Assert.All(trace.Calls,c=>Assert.Equal(f.Root,c.WorkingDirectory));Assert.Single(trace.Calls,c=>c.Arguments.Contains("fetch"));
            trace.Calls.Clear();(await history.CaptureAsync(f.Identity,"scope",[basis,old])).OrThrow();Assert.DoesNotContain(trace.Calls,c=>c.Arguments.Contains("fetch"));
            trace.Calls.Clear();(await history.CaptureAsync(other.Identity,"scope",[basis,old])).OrThrow();Assert.All(trace.Calls,c=>Assert.Equal(other.Root,c.WorkingDirectory));Assert.Single(trace.Calls,c=>c.Arguments.Contains("fetch"));
            Assert.True(Directory.Exists(legacy));
            Assert.Equal("old.txt",Assert.Single(await other.Reader.FilesAsync(other.Root,basis,old)).NewPath);
        }finally{GitFixture.DeleteDirectory(appRoot);}
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Bundle_backup_restores_unreachable_history_into_the_opened_repository(int format)
    {
        await using var f=new GitFixture();await f.Init();var root=Directory.CreateTempSubdirectory("local-bundle-").FullName;
        try
        {
            var app=new ApplicationStore(Path.Combine(root,"profile"));var database=new SqliteHistoryStore(app.Root);var git=new RepositoryGit(f.Runner,new());var history=new HistoryGit(f.Runner,new(),app,git,database);
            var basis=await f.Git("rev-parse","HEAD");await f.Git("checkout","-b","old");await f.Write("archived.txt","restore me\n");await f.Commit("old");var old=await f.Git("rev-parse","HEAD");
            (await history.CaptureAsync(f.Identity,"scope",[basis,old])).OrThrow();(await history.CaptureAsync(f.Identity,"second",[basis,old])).OrThrow();(await database.SaveDraftAsync("draft",new ReviewDraft{Summary="unsent"},0)).OrThrow();
            var archive=Path.Combine(root,"workspace.zip");var backup=new WorkspaceBackupService(app,database,new ProcessRunner(),git);
            if(format==2)(await backup.ExportAsync(archive,f.Identity)).OrThrow();
            else
            {
                foreach(var scope in new[]{"scope","second"}){var legacy=Path.Combine(app.Root,"history-objects",ApplicationStore.Key(scope));Directory.CreateDirectory(legacy);await f.GitAt(legacy,"init","--bare");await f.GitAt(legacy,"fetch","--no-tags",f.Root,"+refs/heads/old:refs/stacker/history/"+old);}
                using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(app.Root,"review-history.db")+";Pooling=False"))
                {connection.Open();using var command=connection.CreateCommand();command.CommandText="DROP TABLE repository_contents;DROP TABLE object_imports;PRAGMA user_version=2";command.ExecuteNonQuery();}
                var hashes=new Dictionary<string,string>();foreach(var file in Directory.EnumerateFiles(app.Root,"*",SearchOption.AllDirectories)){await using var stream=File.OpenRead(file);hashes[Path.GetRelativePath(app.Root,file).Replace('\\','/')]=Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));}
                await File.WriteAllTextAsync(Path.Combine(app.Root,"manifest.json"),System.Text.Json.JsonSerializer.Serialize(new{Schema=1,CreatedAt=DateTimeOffset.UtcNow,Files=hashes}));ZipFile.CreateFromDirectory(app.Root,archive);
            }
            using(var zip=ZipFile.OpenRead(archive)){if(format==2){Assert.Contains(zip.Entries,e=>e.FullName.EndsWith(".bundle",StringComparison.Ordinal));Assert.DoesNotContain(zip.Entries,e=>e.FullName.StartsWith("history-objects/",StringComparison.Ordinal));}}
            await f.Git("checkout","main");await f.Git("branch","-D","old");
            foreach(var reference in (await f.Git("for-each-ref","--format=%(refname)","refs/stacker/history")).Split('\n',StringSplitOptions.RemoveEmptyEntries))await f.Git("update-ref","-d",reference);
            await Collect(f);Assert.NotEqual(0,await f.ExitCode("cat-file","-e",old));
            var restored=Path.Combine(root,"restored");(await backup.RestoreAsync(archive,restored)).OrThrow();
            var restoredStore=new SqliteHistoryStore(restored);var trace=new RepositoryRecordingRunner(f.Runner);var restoredGit=new RepositoryGit(trace,new());var restoredHistory=new HistoryGit(trace,new(),new ApplicationStore(restored),restoredGit,restoredStore);
            using var lease=(await restoredHistory.AcquireAsync(f.Identity,"scope")).OrThrow();Assert.Equal(f.Root,lease.Root);Assert.Equal(old,await f.Git("rev-parse",old));Assert.Equal("unsent",(await restoredStore.ReadDraftAsync("draft")).OrThrow()!.Draft.Summary);
            using var second=(await restoredHistory.AcquireAsync(f.Identity,"second")).OrThrow();Assert.Equal(f.Root,second.Root);
            Assert.All(trace.Calls,c=>Assert.Equal(f.Root,c.WorkingDirectory));
        }finally{GitFixture.DeleteDirectory(root);}
    }
    [Fact] public async Task History_cleanup_waits_for_an_underlying_diff_read_and_preserves_backup_refs()
    {
        await using var f=new GitFixture();await f.Linear();var root=Directory.CreateTempSubdirectory("local-read-lease-").FullName;
        try
        {
            var basis=await f.Git("rev-parse","A");var head=await f.Git("rev-parse","B");var git=new RepositoryGit(f.Runner,new());var history=new HistoryGit(f.Runner,new(),new ApplicationStore(root),git);
            (await history.CaptureAsync(f.Identity,"scope",[basis,head])).OrThrow();await f.Git("update-ref","refs/stacker/backup/keep/main",head);
            var gated=new GatedGitRunner(f.Runner);using var reader=new CachingGitReader(new GitRepositoryReader(gated,new()),repositoryGit:git);
            var file=Assert.Single(await f.Reader.FilesAsync(f.Root,basis,head));var patch=reader.PatchAsync(f.Root,basis,head,file);await gated.Entered.Task;
            (await history.ForgetContentsAsync(f.Identity,"scope")).OrThrow();Assert.Contains("/"+head,await f.Git("show-ref"));
            gated.Release.SetResult();await patch;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while((await f.Git("for-each-ref","--format=%(refname)","refs/stacker/history")).Length>0)await Task.Delay(10,timeout.Token);
            Assert.Equal(head,await f.Git("rev-parse","refs/stacker/backup/keep/main"));Assert.True(Directory.Exists(Path.Combine(f.Root,".git")));
        }finally{GitFixture.DeleteDirectory(root);}
    }
    [Fact] public async Task Linked_worktree_uses_its_opened_root_and_the_shared_object_database()
    {
        await using var f=new GitFixture();await f.Linear();var root=Directory.CreateTempSubdirectory("local-worktree-").FullName;
        try
        {
            var worktree=await f.AddWorktree();var identities=new ChangeIdentityService(new(root),f.Runner,new());var identity=(await identities.RegisterAsync(worktree)).OrThrow();
            var before=await f.GitAt(worktree,"rev-parse","HEAD");var head=await f.Git("rev-parse","A");var basis=await f.Git("rev-parse","main");var trace=new RepositoryRecordingRunner(f.Runner);var git=new RepositoryGit(trace,new());
            (await new LocalPullRequestObjects(git).EnsureAsync(identity,FakeGitHub.Context,[DiscoveryTests.Pr(42,"main","A") with{HeadSha=head,BaseSha=basis}])).OrThrow();
            Assert.All(trace.Calls,c=>Assert.Equal(identity.Root,c.WorkingDirectory));Assert.DoesNotContain(trace.Calls,c=>c.Arguments.Contains("fetch"));Assert.Equal(before,await f.GitAt(worktree,"rev-parse","HEAD"));Assert.Contains("refs/stacker/pr/",await f.Git("show-ref"));
        }finally{GitFixture.DeleteDirectory(root);}
    }
    [Fact] public async Task Repository_selection_overrides_redirecting_Git_environment_variables()
    {
        await using var selected=new GitFixture();await selected.Init();await using var other=new GitFixture();await other.Init();
        var request=new ProcessRequest(new GitExecutable().Resolve(),["rev-parse","--show-toplevel"],selected.Root,Environment:new Dictionary<string,string>
        {["GIT_DIR"]=Path.Combine(other.Root,".git"),["GIT_WORK_TREE"]=other.Root,["GIT_INDEX_FILE"]=Path.Combine(other.Root,"other-index")});
        var result=(await ((IProcessRunner)selected.Runner).RunAsync(request)).OrThrow();Assert.Equal(0,result.ExitCode);Assert.Equal(await selected.ResolvedRoot(),result.StdOut.Trim());Assert.False(File.Exists(Path.Combine(other.Root,"other-index")));
    }
    [Fact] public async Task Local_merge_preview_does_not_execute_configured_external_merge_drivers()
    {
        await using var f=new GitFixture();await f.Init();var root=Directory.CreateTempSubdirectory("local-driver-").FullName;
        try
        {
            await f.Write(".gitattributes","base.txt merge=unsafe\n");await f.Commit("attributes");var basis=await f.Git("rev-parse","HEAD");
            await f.Git("checkout","-b","left");await f.Write("base.txt","left\n");await f.Commit("left");var left=await f.Git("rev-parse","HEAD");
            await f.Git("checkout","main");await f.Write("base.txt","right\n");await f.Commit("right");var right=await f.Git("rev-parse","HEAD");
            var marker=Path.Combine(f.Root,"driver-ran");await f.Git("config","merge.unsafe.driver","echo unsafe > \""+marker+"\"");
            var before=await GitCacheTests.UserState(f);var history=new HistoryGit(f.Runner,new(),new ApplicationStore(root));(await history.CaptureAsync(f.Identity,"scope",[basis,left,right])).OrThrow();
            var result=await history.MergeAsync(f.Identity,left,right);Assert.True(result.IsFailed);Assert.Contains("external merge driver",result.Problem!.Message);Assert.False(File.Exists(marker));Assert.Equal(before,await GitCacheTests.UserState(f));
        }finally{GitFixture.DeleteDirectory(root);}
    }
    [Fact] public async Task Binding_a_local_change_moves_its_pins_into_the_remote_scope_and_cleanup_releases_both()
    {
        await using var f=new GitFixture();await f.Init();var root=Directory.CreateTempSubdirectory("local-bound-pins-").FullName;
        try
        {
            var database=new SqliteHistoryStore(root);var identities=new ChangeIdentityService(database,f.Runner,new());var repository=(await identities.RegisterAsync(f.Root)).OrThrow();
            var head=await f.Git("rev-parse","HEAD");(await database.ResolveNodeAsync(repository.Scope,"refs/heads/feature","feature")).OrThrow();
            var history=new HistoryGit(f.Runner,new(),new ApplicationStore(root),database:database);
            (await history.CaptureAsync(repository,repository.Scope,[head])).OrThrow();
            (await identities.BindAsync(repository.Scope,"refs/heads/feature",FakeGitHub.Context,DiscoveryTests.Pr(42,"main","feature"))).OrThrow();
            (await history.CaptureAsync(repository,FakeGitHub.Context.Identity,[head])).OrThrow();
            var source=$"refs/stacker/history/{repository.Repository.Value}/{ApplicationStore.Key(repository.Scope)[..32]}/";
            var destination=$"refs/stacker/history/{repository.Repository.Value}/{ApplicationStore.Key(FakeGitHub.Context.Identity)[..32]}/";
            Assert.Equal("",await f.Git("for-each-ref","--format=%(refname)",source));Assert.Contains(head,await f.Git("for-each-ref","--format=%(refname)",destination));
            (await history.ForgetContentsAsync(repository,FakeGitHub.Context.Identity)).OrThrow();Assert.Equal("",await f.Git("for-each-ref","--format=%(refname)","refs/stacker/history"));
            Assert.Equal(head,await f.Git("rev-parse","HEAD"));
        }finally{GitFixture.DeleteDirectory(root);}
    }
    [Avalonia.Headless.XUnit.AvaloniaFact] public async Task The_real_workspace_executes_git_only_in_its_opened_repository()
    {
        await using var f=new GitFixture();await f.Linear();var root=Directory.CreateTempSubdirectory("local-workspace-audit-").FullName;
        try
        {
            await new YamlStackStore().SaveAsync(f.Root,[f.Stack],null);var before=await GitCacheTests.UserState(f);
            using var services=Stacker.Desktop.Composition.Build(root);
            var vm=Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Stacker.Desktop.ViewModels.MainViewModel>(services);
            await vm.OpenRepositoryAsync(f.Root);await vm.ShowHistoryAsync();Assert.True(vm.IsHistoryOpen,vm.Error);
            await vm.ActiveHistory!.ShowCurrentCommand.ExecuteAsync(null);await vm.ActiveHistory.EvolutionCommand.ExecuteAsync(null);
            var log=Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IGitDiagnosticLog>(services).Snapshot();
            Assert.NotEmpty(log);var resolved=await f.ResolvedRoot();Assert.All(log,entry=>Assert.True(entry.WorkingDirectory==f.Root||entry.WorkingDirectory==resolved,entry.WorkingDirectory));
            Assert.DoesNotContain(log,entry=>entry.Command.Contains("init --bare",StringComparison.Ordinal)||entry.Command.Contains("git clone",StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(root,"objects")));Assert.False(Directory.Exists(Path.Combine(root,"history-objects")));Assert.Equal(before,await GitCacheTests.UserState(f));await vm.ShutdownAsync();
        }finally{GitFixture.DeleteDirectory(root);}
    }
    private static async Task Collect(GitFixture f){await f.Git("reflog","expire","--expire=now","--all");await f.Git("gc","--prune=now");}
    private sealed class GatedGitRunner(IProcessRunner inner):IProcessRunner
    {
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Result<ProcessResult>> RunAsync(ProcessRequest request,CancellationToken ct=default)
        {if(request.Arguments.Contains("--patch")){Entered.TrySetResult();await Release.Task.WaitAsync(ct);}return await inner.RunAsync(request,ct);}
    }
}
