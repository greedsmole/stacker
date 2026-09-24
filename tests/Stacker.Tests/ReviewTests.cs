using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class FakeGitHub : IGitHubReader, IGitHubWriter
{
    public PullRequest Current { get; set; }=DiscoveryTests.Pr(42,"main","feature");
    public bool RejectAccess { get; set; }
    public bool FailAfterSend { get; set; }
    public int Writes { get; private set; }
    public int Verifications { get; private set; }
    public List<DiscussionComment> Published { get; }=[];
    public List<ReviewSummary> Submitted { get; }=[];
    public List<ReviewThread> FileThreads { get; }=[];
    public bool RejectFile { get; set; }
    public Task<GitHubRepositoryContext> ConnectAsync(string root,string? repositoryOverride,CancellationToken ct=default)=>Task.FromResult(Context);
    public static GitHubRepositoryContext Context { get; }=new("github.example",1,"team","repo","https://github.example/team/repo.git","alice","main");
    public Task<GitHubSnapshot> SnapshotAsync(GitHubRepositoryContext context,CancellationToken ct=default)=>Task.FromResult(new GitHubSnapshot(context,[Current],DateTimeOffset.UtcNow));
    public Task<PullRequest> PullRequestAsync(GitHubRepositoryContext context,long number,CancellationToken ct=default)=>Task.FromResult(Current);
    public Task<ReviewDiscussion> DiscussionAsync(GitHubRepositoryContext context,PullRequest pr,CancellationToken ct=default)=>Task.FromResult(new ReviewDiscussion(Published.ToArray(),Submitted.ToArray(),FileThreads.ToArray()));
    public Task VerifyAccessAsync(GitHubRepositoryContext context,CancellationToken ct=default){Verifications++;if(RejectAccess)throw new StackerException("HTTP 403");return Task.CompletedTask;}
    public Task ValidateAnchorsAsync(GitHubRepositoryContext context,PullRequest pr,IReadOnlyList<ReviewAnchor> anchors,CancellationToken ct=default)
    {if(anchors.Any(a=>a.Line!=1 || a.HeadSha!=pr.HeadSha))throw new StackerException("Invalid anchor");return Task.CompletedTask;}
    public Task CommentAsync(GitHubRepositoryContext context,long number,string body,CancellationToken ct=default){Writes++;Published.Add(new("1","alice",body,"",DateTimeOffset.UtcNow));if(FailAfterSend)throw new StackerException("Timed out after send");return Task.CompletedTask;}
    public Task<IReadOnlyList<string>> ChangedFilePathsAsync(GitHubRepositoryContext context,PullRequest pr,CancellationToken ct=default) => Task.FromResult<IReadOnlyList<string>>(["renamed ü.png", "other.cs"]);
    public Task ValidateFileAsync(GitHubRepositoryContext context,PullRequest pr,string path,CancellationToken ct=default)
    { if(RejectFile) throw new StackerException("File disappeared"); return Task.CompletedTask; }
    public Task FileCommentAsync(GitHubRepositoryContext context,PullRequest pr,string path,string body,CancellationToken ct=default)
    { Writes++; FileThreads.Add(new("file-thread",false,false,true,null,[new("2","alice",body,"",DateTimeOffset.UtcNow)],path,true)); if(FailAfterSend)throw new StackerException("Timed out after send"); return Task.CompletedTask; }
    public Task InlineCommentAsync(GitHubRepositoryContext context,DraftComment comment,CancellationToken ct=default)=>CommentAsync(context,comment.Anchor.PullRequest,comment.Body,ct);
    public Task ReplyAsync(GitHubRepositoryContext context,long number,string commentId,string body,CancellationToken ct=default)=>CommentAsync(context,number,body,ct);
    public Task ResolveAsync(GitHubRepositoryContext context,string threadId,bool resolved,CancellationToken ct=default){Writes++;return Task.CompletedTask;}
    public Task SubmitReviewAsync(GitHubRepositoryContext context,ReviewDraft draft,CancellationToken ct=default){Writes++;Submitted.Add(new("1","alice",draft.Summary,draft.Decision,DateTimeOffset.UtcNow));if(FailAfterSend)throw new StackerException("Timed out after send");return Task.CompletedTask;}
}
public sealed class ReviewTests
{
    private static DiffSectionViewModel Section(GitFixture f,PullRequest pr)=>new(f.Reader,f.Root,new("feature","main",pr.BaseSha,pr.HeadSha,null,[])){IsPrSnapshot=true,AssociatedPr=pr,GitHubContext=FakeGitHub.Context};
    [AvaloniaFact] public async Task Draft_summary_composer_and_comments_survive_reopening()
    {
        await using var f=new GitFixture(); var hub=new FakeGitHub();var store=new ApplicationStore(f.Root);using var section=Section(f,hub.Current);
        var vm=new ReviewViewModel(hub,hub,store,f.Reader,FakeGitHub.Context,hub.Current,section,false);await vm.InitializeAsync();
        vm.Summary="Review summary";vm.Composer="Unsent line text";await vm.FlushAsync();
        var reopened=new ReviewViewModel(hub,hub,store,f.Reader,FakeGitHub.Context,hub.Current,section,false);await reopened.InitializeAsync();Assert.Equal("Review summary",reopened.Summary);Assert.Equal("Unsent line text",reopened.Composer);Assert.Equal(0,hub.Writes);await reopened.FlushAsync();
    }
    [AvaloniaFact] public async Task Head_change_and_missing_permissions_prevent_writes_without_losing_text()
    {
        await using var f=new GitFixture();var hub=new FakeGitHub();using var section=Section(f,hub.Current);
        var vm=new ReviewViewModel(hub,hub,new ApplicationStore(f.Root),f.Reader,FakeGitHub.Context,hub.Current,section,false);await vm.InitializeAsync();vm.Composer="Keep this";
        hub.Current=hub.Current with{HeadSha=new('c',40)};await vm.PostCommentCommand.ExecuteAsync(null);Assert.Equal(0,hub.Writes);Assert.Equal("Keep this",vm.Composer);Assert.Contains("changed",vm.Message);
        hub.Current=section.AssociatedPr!;hub.RejectAccess=true;await vm.PostCommentCommand.ExecuteAsync(null);Assert.Equal(0,hub.Writes);Assert.Contains("403",vm.Message);await vm.FlushAsync();
    }
    [AvaloniaFact] public async Task Uncertain_comment_is_reconciled_and_never_automatically_resent()
    {
        await using var f=new GitFixture();var hub=new FakeGitHub{FailAfterSend=true};using var section=Section(f,hub.Current);
        var vm=new ReviewViewModel(hub,hub,new ApplicationStore(f.Root),f.Reader,FakeGitHub.Context,hub.Current,section,false);await vm.InitializeAsync();vm.Composer="Check this";
        await vm.PostCommentCommand.ExecuteAsync(null);Assert.Equal(1,hub.Writes);Assert.False(vm.CanPublish);
        await vm.PostCommentCommand.ExecuteAsync(null);Assert.Equal(1,hub.Writes);
        await vm.ReloadAsync();Assert.True(vm.CanPublish);Assert.Contains("confirmed",vm.Message);Assert.Equal(1,hub.Writes);await vm.FlushAsync();
    }
    [AvaloniaFact] public async Task Review_submission_and_demo_write_guard_are_explicit()
    {
        await using var f=new GitFixture();var hub=new FakeGitHub();using var section=Section(f,hub.Current);var store=new ApplicationStore(f.Root);
        var demo=new ReviewViewModel(hub,hub,store,f.Reader,FakeGitHub.Context,hub.Current,section,true);await demo.InitializeAsync();demo.Composer="Do not send";await demo.PostCommentCommand.ExecuteAsync(null);Assert.Equal(0,hub.Writes);await demo.FlushAsync();
        var real=new ReviewViewModel(hub,hub,store,f.Reader,FakeGitHub.Context,hub.Current,section,false);await real.InitializeAsync();real.Summary="Looks good";real.Decision="APPROVE";await real.SubmitReviewCommand.ExecuteAsync(null);Assert.Equal(1,hub.Writes);Assert.Equal("APPROVE",Assert.Single(hub.Submitted).State);Assert.Equal("",real.Summary);await real.FlushAsync();
    }
}
