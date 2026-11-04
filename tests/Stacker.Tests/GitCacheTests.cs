using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class GitCacheTests
{
    [Fact] public async Task PR_objects_are_fetched_into_the_opened_repository_and_preserve_dirty_work()
    {
        await using var f=new GitFixture();await f.Init();await f.AddOrigin();await f.Git("push","origin","main");
        await using var author=new GitFixture();await author.Init();await author.Git("remote","add","origin",f.RemoteRoot!);await author.Git("fetch","origin");await author.Git("checkout","-B","main","origin/main");
        var basis=await f.Git("rev-parse","main");await author.Git("checkout","-b","feature");await author.Write("feature.txt","remote changes\n");await author.Commit("feature");var head=await author.Git("rev-parse","HEAD");
        await author.Git("push","origin","HEAD:refs/pull/42/head");
        await f.Write("base.txt","staged\n");await f.Git("add","base.txt");await f.Write("base.txt","unstaged\n");await f.Write("unsent.txt","keep me\n");
        await File.WriteAllTextAsync(Path.Combine(f.Root,".git","FETCH_HEAD"),"preserved fetch head\n");
        var before=await UserState(f);var trace=new RepositoryRecordingRunner(f.Runner);var git=new RepositoryGit(trace,new());IPullRequestObjects objects=new LocalPullRequestObjects(git);
        var pr=DiscoveryTests.Pr(42,"main","feature",headRepo:2) with{BaseSha=basis,HeadSha=head};
        Assert.True((await objects.EnsureAsync(f.Identity,FakeGitHub.Context,[pr])).IsOk);
        Assert.Equal("feature.txt",Assert.Single(await f.Reader.FilesAsync(f.Root,basis,head)).NewPath);Assert.Equal(before,await UserState(f));
        var fetch=Assert.Single(trace.Calls,c=>c.Arguments.Contains("fetch"));Assert.Contains("origin",fetch.Arguments);Assert.Contains("--atomic",fetch.Arguments);Assert.Contains("--no-write-fetch-head",fetch.Arguments);
        Assert.All(trace.Calls,c=>Assert.Equal(f.Root,c.WorkingDirectory));Assert.DoesNotContain(trace.Calls,c=>c.Arguments.Contains("clone")||c.Arguments.Contains("init")||c.Arguments.Contains("checkout"));
        trace.Calls.Clear();(await objects.EnsureAsync(f.Identity,FakeGitHub.Context,[pr])).OrThrow();Assert.DoesNotContain(trace.Calls,c=>c.Arguments.Contains("fetch"));
        (await objects.ClearAsync(f.Identity,FakeGitHub.Context)).OrThrow();Assert.Equal(before,await UserState(f));Assert.True(Directory.Exists(Path.Combine(f.Root,".git")));
        Assert.DoesNotContain("refs/stacker/pr/",await f.Git("show-ref"));Assert.Equal(head,await f.Git("rev-parse",head));
    }
    [Fact] public async Task Fetch_uses_native_credentials_and_batches_missing_PRs()
    {
        await using var f=new GitFixture();var downloaded=false;
        var runner=new ScriptedRunner(request=>
        {
            if(request.Arguments.Contains("cat-file"))return new(0,string.Join('\n',request.StandardInput!.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(sha=>sha+(downloaded?" commit":" missing")))+"\n","");
            if(request.Arguments.Contains("get-url"))return new(0,"git@company-alias:team/repo.git\n","");
            if(request.Arguments.Contains("fetch")){downloaded=true;return new(0,"","");}
            if(request.Arguments.Contains("rev-parse"))return new(0,new string(request.Arguments.Last().EndsWith("/base",StringComparison.Ordinal)?'a':'b',40)+"\n","");
            return new(0,"","");
        });
        var objects=new LocalPullRequestObjects(new(runner,new()));
        (await objects.EnsureAsync(f.Identity,FakeGitHub.Context,[DiscoveryTests.Pr(1,"main","A"),DiscoveryTests.Pr(2,"main","B")])).OrThrow();
        var fetch=Assert.Single(runner.Calls,c=>c.Arguments.Contains("fetch"));Assert.Contains("origin",fetch.Arguments);Assert.Equal(2,fetch.StandardInput!.Split('\n').Count(a=>a.StartsWith("+refs/pull/",StringComparison.Ordinal)));
        Assert.DoesNotContain(fetch.Arguments,a=>a.Contains("credential.helper")||a.Contains("http.sslBackend")||a.Contains("sslVerify=false"));
        Assert.DoesNotContain(fetch.UnsetEnvironment!,name=>name.Contains("TOKEN"));Assert.All(runner.Calls,c=>Assert.Equal(f.Root,c.WorkingDirectory));
    }
    [Fact] public async Task A_moving_PR_snapshot_is_rejected_without_switching_working_directory()
    {
        await using var f=new GitFixture();
        var runner=new ScriptedRunner(r=>r.Arguments.Contains("cat-file")?new(0,new string('a',40)+" missing\n"+new string('b',40)+" missing\n",""):
            r.Arguments.Contains("get-url")?new(0,"https://github.example/team/repo.git",""):r.Arguments.Contains("rev-parse")?new(0,new string('c',40)+"\n",""):new(0,"",""));
        var result=await new LocalPullRequestObjects(new(runner,new())).EnsureAsync(f.Identity,FakeGitHub.Context,[DiscoveryTests.Pr(42,"main","feature")]);
        Assert.True(result.IsFailed);Assert.Contains("changed while downloading",result.Problem!.Message);Assert.Single(runner.Calls,c=>c.Arguments.Contains("fetch"));Assert.All(runner.Calls,c=>Assert.Equal(f.Root,c.WorkingDirectory));
    }
    [Theory]
    [InlineData("SSL certificate problem: unable to get local issuer certificate")]
    [InlineData("fatal: Authentication failed")]
    public async Task Native_fetch_failures_retain_diagnostics_and_do_not_retry(string error)
    {
        await using var f=new GitFixture();
        var runner=new ScriptedRunner(r=>r.Arguments.Contains("cat-file")?new(0,"missing\n",""):r.Arguments.Contains("get-url")?new(0,"https://github.example/team/repo.git",""):r.Arguments.Contains("fetch")?new(128,"",error):new(0,"",""));
        var result=await new LocalPullRequestObjects(new(runner,new())).EnsureAsync(f.Identity,FakeGitHub.Context,[DiscoveryTests.Pr(42,"main","feature")]);
        Assert.True(result.IsFailed);Assert.Contains(error,result.Problem!.Message);Assert.Single(runner.Calls,c=>c.Arguments.Contains("fetch"));
    }
    [Fact] public async Task A_different_origin_is_not_used_for_the_selected_GitHub_repository()
    {
        await using var f=new GitFixture();var runner=new ScriptedRunner(r=>r.Arguments.Contains("cat-file")?new(0,"missing\n",""):new(0,"https://other.example/other/repo.git","") );
        var result=await new LocalPullRequestObjects(new(runner,new())).EnsureAsync(f.Identity,FakeGitHub.Context,[DiscoveryTests.Pr(42,"main","feature")]);
        Assert.True(result.IsFailed);Assert.DoesNotContain(runner.Calls,c=>c.Arguments.Contains("fetch"));
    }
    internal static async Task<string> UserState(GitFixture f)=>string.Join("\n---\n",await f.Git("symbolic-ref","HEAD"),await f.Git("for-each-ref","--format=%(refname) %(objectname)","refs/heads","refs/remotes"),
        Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(f.Root,".git","index"))),await f.Git("status","--porcelain=v1","-z"),
        await File.ReadAllTextAsync(Path.Combine(f.Root,".git","config")),File.Exists(Path.Combine(f.Root,".git","FETCH_HEAD"))?await File.ReadAllTextAsync(Path.Combine(f.Root,".git","FETCH_HEAD")):"absent");
}

internal sealed class RepositoryRecordingRunner(IProcessRunner inner):IProcessRunner
{
    public List<ProcessRequest> Calls { get; }=[];
    public Task<Result<ProcessResult>> RunAsync(ProcessRequest request,CancellationToken ct=default)
    {lock(Calls)Calls.Add(request);return inner.RunAsync(request,ct);}
}
