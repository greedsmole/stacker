using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class GitCacheTests
{
    [Fact] public async Task Existing_objects_support_offline_comparison_without_touching_source_repository()
    {
        await using var repo=new GitFixture();await repo.Linear();await using var cacheFolder=new GitFixture();
        var store=new ApplicationStore(cacheFolder.Root);var context=FakeGitHub.Context;
        var cachePath=Path.Combine(store.Root,"objects",ApplicationStore.Key(context.Identity));Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var clone=await repo.Runner.RunAsync(new(new GitExecutable().Resolve(),["clone","--bare",repo.Root,cachePath]));Assert.Equal(0,clone.ExitCode);
        var head=await repo.Git("rev-parse","B");var parent=await repo.Git("rev-parse","A");
        var pr=new PullRequest(42,"Test",1,"A",parent,1,"B",head,false,"");
        var beforeRefs=await repo.Git("show-ref");var beforeIndex=await File.ReadAllBytesAsync(Path.Combine(repo.Root,".git","index"));var beforeConfig=await File.ReadAllTextAsync(Path.Combine(repo.Root,".git","config"));
        var cache=new GitObjectCache(repo.Runner,new(),new(),store);
        var snapshot=await cache.PrepareAsync(context,[pr]);Assert.Equal(cachePath,snapshot.Root);
        var files=await repo.Reader.FilesAsync(snapshot.Root,parent,head);Assert.Equal("B.txt",Assert.Single(files).NewPath);
        Assert.Equal(beforeRefs,await repo.Git("show-ref"));Assert.Equal(beforeIndex,await File.ReadAllBytesAsync(Path.Combine(repo.Root,".git","index")));Assert.Equal(beforeConfig,await File.ReadAllTextAsync(Path.Combine(repo.Root,".git","config")));
        await cache.ClearAsync(context);Assert.False(Directory.Exists(cachePath));Assert.True(Directory.Exists(Path.Combine(repo.Root,".git")));
    }
    [Fact] public async Task Download_uses_private_refs_scoped_helper_and_rejects_moving_snapshot()
    {
        await using var f=new GitFixture();var store=new ApplicationStore(f.Root);
        var runner=new ScriptedRunner(r=>r.Arguments.Contains("cat-file")?new(1,"",""):r.Arguments.Contains("rev-parse")?new(0,new string('c',40)+"\n",""):new(0,"",""));
        var error=await Assert.ThrowsAsync<StackerException>(()=>new GitObjectCache(runner,new(),new(),store).PrepareAsync(FakeGitHub.Context,[DiscoveryTests.Pr(42,"main","feature")]));
        Assert.Contains("changed while downloading",error.Message);
        var fetch=Assert.Single(runner.Calls,c=>c.Arguments.Contains("fetch"));Assert.Contains("--no-write-fetch-head",fetch.Arguments);Assert.Contains("+refs/pull/42/head:refs/stacker/pr/42",fetch.Arguments);Assert.Contains("credential.helper=",fetch.Arguments);Assert.StartsWith(Path.Combine(store.Root,"objects"),fetch.WorkingDirectory);
        Assert.DoesNotContain(runner.Calls,c=>c.Arguments.Contains("setup-git"));Assert.DoesNotContain(fetch.Environment!.Keys,k=>k.Contains("TOKEN"));
        Assert.Equal(OperatingSystem.IsWindows(), fetch.Arguments.Contains("http.sslBackend=schannel"));
        Assert.DoesNotContain(runner.Calls.Where(c => !c.Arguments.Contains("fetch")), c => c.Arguments.Contains("http.sslBackend=schannel"));
        Assert.DoesNotContain(fetch.Arguments, a => a.Contains("sslVerify=false") || a.Contains("schannelCheckRevoke=false"));
        Assert.DoesNotContain("GIT_SSL_NO_VERIFY", fetch.Environment.Keys);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("config"));
    }

    [Theory]
    [InlineData("SSL certificate problem: unable to get local issuer certificate")]
    [InlineData("SSL certificate problem: self-signed certificate in certificate chain")]
    [InlineData("schannel: SEC_E_UNTRUSTED_ROOT (0x80090325)")]
    [InlineData("schannel: CertGetCertificateChain trust error CERT_TRUST_IS_PARTIAL_CHAIN")]
    public async Task Untrusted_certificate_explains_remediation_preserves_details_and_does_not_retry(string stderr)
    {
        await using var folder = new GitFixture();
        var runner = new ScriptedRunner(request => request.Arguments.Contains("cat-file") ? new(1, "", "")
            : request.Arguments.Contains("fetch") ? new(128, "", stderr) : new(0, "", ""));
        var cache = new GitObjectCache(runner, new(), new() { UseSavedCredentials = true }, new ApplicationStore(folder.Root));

        var error = await Assert.ThrowsAsync<StackerException>(() => cache.PrepareAsync(FakeGitHub.Context, [DiscoveryTests.Pr(42, "main", "feature")]));

        Assert.Contains("certificate chain is not trusted", error.Message);
        Assert.Contains(OperatingSystem.IsWindows() ? "Windows certificate store" : "Git certificate trust store", error.Message);
        Assert.Contains(stderr, error.Message);
        var fetch = Assert.Single(runner.Calls, request => request.Arguments.Contains("fetch"));
        Assert.Contains("GITHUB_TOKEN", fetch.UnsetEnvironment!);
        Assert.DoesNotContain(runner.Calls, request => request.Arguments.Contains("rev-parse"));
    }

    [Fact]
    public async Task Other_fetch_failures_keep_the_original_error()
    {
        await using var folder = new GitFixture();
        const string stderr = "fatal: Authentication failed";
        var runner = new ScriptedRunner(request => request.Arguments.Contains("cat-file") ? new(1, "", "")
            : request.Arguments.Contains("fetch") ? new(128, "", stderr) : new(0, "", ""));

        var error = await Assert.ThrowsAsync<StackerException>(() => new GitObjectCache(runner, new(), new(), new ApplicationStore(folder.Root))
            .PrepareAsync(FakeGitHub.Context, [DiscoveryTests.Pr(42, "main", "feature")]));

        Assert.Equal("Git cache: " + stderr, error.Message);
        Assert.Single(runner.Calls, request => request.Arguments.Contains("fetch"));
    }
}
