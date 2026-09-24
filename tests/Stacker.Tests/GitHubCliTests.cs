using System.Text.Json;
using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class ScriptedRunner(Func<ProcessRequest,ProcessResult> handler) : IProcessRunner
{
    public List<ProcessRequest> Calls { get; } = [];
    public Task<ProcessResult> RunAsync(ProcessRequest request,CancellationToken ct=default) { ct.ThrowIfCancellationRequested(); Calls.Add(request); return Task.FromResult(handler(request)); }
}
public sealed class GitHubCliTests
{
    private static readonly GitHubRepositoryContext Context = new("enterprise.example",1,"team","repo","https://enterprise.example/team/repo.git","alice","main");
    [Theory]
    [InlineData("https://github.com/team/repo.git","github.com")]
    [InlineData("git@github.com:team/repo.git","github.com")]
    [InlineData("ssh://git@enterprise.example/team/repo.git","enterprise.example")]
    public void Origin_parsing_supports_https_ssh_and_enterprise(string url,string host)
    {
        var result=GitHubCli.ParseOrigin(url); Assert.Equal(host,result.Host); Assert.Equal("team",result.Owner); Assert.Equal("repo",result.Name);
    }
    [Theory]
    [InlineData("/local/repo")]
    [InlineData("https://github.com/team/repo/extra")]
    [InlineData("https://github.com/team/repo?not-a-repo")]
    public void Invalid_origin_is_not_guessed(string url)
    {
        Assert.Throws<StackerException>(()=>GitHubCli.ParseOrigin(url));
    }
    [Fact] public async Task Json_auth_failure_with_exit_zero_is_rejected()
    {
        var runner=new ScriptedRunner(_=>new(0,"{\"hosts\":{\"enterprise.example\":[{\"active\":true,\"state\":\"error\",\"login\":\"alice\"}]}}",""));
        var cli=new GitHubCli(runner,new(),new());
        var error=await Assert.ThrowsAsync<StackerException>(()=>cli.ConnectAsync(".","enterprise.example/team/repo")); Assert.Contains("gh auth login --hostname enterprise.example",error.Message);
        Assert.Single(runner.Calls); Assert.DoesNotContain("--show-token",runner.Calls[0].Arguments);
    }
    [Fact] public async Task Invalid_environment_token_explains_saved_account_override()
    {
        var runner = new ScriptedRunner(_ => new(0, """{"hosts":{"github.com":[{"active":true,"state":"error","tokenSource":"GITHUB_TOKEN"}]}}""", ""));
        var error = await Assert.ThrowsAsync<StackerException>(() => new GitHubCli(runner, new(), new()).ConnectAsync(".", "github.com/team/repo"));
        Assert.Contains("GITHUB_TOKEN is invalid", error.Message);
        Assert.Contains("Use saved gh credentials", error.Message);
    }
    [Fact] public async Task Saved_credentials_policy_applies_to_auth_and_api_without_switching_accounts()
    {
        var runner = new ScriptedRunner(r => r.Arguments[0] == "auth"
            ? new(0, """{"hosts":{"enterprise.example":[{"active":true,"state":"success","login":"alice","tokenSource":"keyring"}]}}""", "")
            : new(0, """{"id":1,"owner":{"login":"team"},"name":"repo","clone_url":"https://enterprise.example/team/repo.git","default_branch":"main"}""", ""));
        await new GitHubCli(runner, new(), new() { UseSavedCredentials = true }).ConnectAsync(".", "enterprise.example/team/repo");
        Assert.Equal(2, runner.Calls.Count);
        foreach (var call in runner.Calls) { Assert.Contains("GITHUB_TOKEN", call.UnsetEnvironment!); Assert.Contains("GH_ENTERPRISE_TOKEN", call.UnsetEnvironment!); Assert.DoesNotContain("switch", call.Arguments); }
    }
    [Fact] public async Task Authenticated_host_still_requires_repository_access()
    {
        var runner=new ScriptedRunner(r=>r.Arguments[0]=="auth" ? new(0,"{\"hosts\":{\"enterprise.example\":[{\"active\":true,\"state\":\"success\",\"login\":\"alice\"}]}}","") : new(1,"","HTTP 404"));
        await Assert.ThrowsAsync<StackerException>(()=>new GitHubCli(runner,new(),new()).ConnectAsync(".","enterprise.example/team/repo")); Assert.Equal(2,runner.Calls.Count);
    }
    [Fact] public async Task Snapshot_flattens_all_pages_and_keeps_drafts_and_repository_ids()
    {
        object Pr(long n,bool draft)=>new { number=n,title="Title",@base=new {repo=new{id=1},@ref="main",sha=new string('a',40)},head=new {repo=new{id=2},@ref="feature",sha=new string('b',40)},draft,html_url="https://example.invalid"};
        var runner=new ScriptedRunner(_=>new(0,JsonSerializer.Serialize(new[]{new[]{Pr(1,false)},new[]{Pr(2,true)}}),""));
        var result=await new GitHubCli(runner,new(),new()).SnapshotAsync(Context); Assert.Equal(2,result.PullRequests.Count); Assert.True(result.PullRequests[1].IsDraft); Assert.Equal(2,result.PullRequests[0].HeadRepositoryId);
        Assert.Contains("--paginate",runner.Calls[0].Arguments); Assert.Contains(Context.Host,runner.Calls[0].Arguments);
    }
    [Fact] public async Task Inline_payload_uses_stdin_line_side_and_ranges_never_position()
    {
        var runner=new ScriptedRunner(_=>new(0,"{}","")); var cli=new GitHubCli(runner,new(),new());
        await cli.InlineCommentAsync(Context,new(new(42,new('b',40),new('a',40),"src/ü file.cs","LEFT",12,10,"LEFT"),"literal $(text)\nsecond line"));
        var call=Assert.Single(runner.Calls); Assert.Contains("--input",call.Arguments); Assert.DoesNotContain(call.Arguments,a=>a.Contains("literal"));
        var body=JsonDocument.Parse(call.StandardInput!).RootElement; Assert.Equal(12,body.GetProperty("line").GetInt32()); Assert.Equal(10,body.GetProperty("start_line").GetInt32()); Assert.Equal("LEFT",body.GetProperty("side").GetString()); Assert.False(body.TryGetProperty("position",out _));
    }
    [Fact] public async Task Server_patch_validation_rejects_unavailable_lines()
    {
        var runner=new ScriptedRunner(_=>new(0,"[[{\"filename\":\"a.cs\",\"patch\":\"@@ -1 +1 @@\\n-old\\n+new\\n\"}]]","")); var cli=new GitHubCli(runner,new(),new()); var pr=DiscoveryTests.Pr(1,"main","a");
        await cli.ValidateAnchorsAsync(Context,pr,[new(1,pr.HeadSha,pr.BaseSha,"a.cs","RIGHT",1)]);
        await Assert.ThrowsAsync<StackerException>(()=>cli.ValidateAnchorsAsync(Context,pr,[new(1,pr.HeadSha,pr.BaseSha,"a.cs","RIGHT",2)]));
    }
}
