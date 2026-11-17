// Purpose: Review workflow scenarios using a fake GitHub service to verify draft preservation and safe publication behavior.
using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;
[Collection(HeadlessApp.CollectionName)]
public sealed class ReviewTests
{
    private static DiffSectionViewModel Section(GitFixture f, PullRequest pr) => new(f.Reader, f.Root, new("feature", "main", pr.BaseSha, pr.HeadSha, null, [])) { IsPrSnapshot = true, AssociatedPr = pr, GitHubContext = FakeGitHub.Context };
    [AvaloniaFact]
    public async Task Draft_summary_composer_and_comments_survive_reopening()
    {
        await using var f = new GitFixture();
        var hub = new FakeGitHub();
        var store = new ApplicationStore(f.Root);
        using var section = Section(f, hub.Current);
        var vm = new ReviewViewModel(hub, hub, store, f.Reader, FakeGitHub.Context, hub.Current, section, false);
        await vm.InitializeAsync();
        vm.Summary = "Review summary";
        vm.Composer = "Unsent line text";
        await vm.FlushAsync();
        var reopened = new ReviewViewModel(hub, hub, store, f.Reader, FakeGitHub.Context, hub.Current, section, false);
        await reopened.InitializeAsync();
        Assert.Equal("Review summary", reopened.Summary);
        Assert.Equal("Unsent line text", reopened.Composer);
        Assert.Equal(0, hub.Writes);
        await reopened.FlushAsync();
    }
    [AvaloniaFact]
    public async Task Head_change_and_missing_permissions_prevent_writes_without_losing_text()
    {
        await using var f = new GitFixture();
        var hub = new FakeGitHub();
        using var section = Section(f, hub.Current);
        var vm = new ReviewViewModel(hub, hub, new ApplicationStore(f.Root), f.Reader, FakeGitHub.Context, hub.Current, section, false);
        await vm.InitializeAsync();
        vm.Composer = "Keep this";
        hub.Current = hub.Current with
        {
            HeadSha = new('c', 40)
        };
        await vm.PostCommentCommand.ExecuteAsync(null);
        Assert.Equal(0, hub.Writes);
        Assert.Equal("Keep this", vm.Composer);
        Assert.Contains("changed", vm.Message);
        hub.Current = section.AssociatedPr!;
        hub.RejectAccess = true;
        await vm.PostCommentCommand.ExecuteAsync(null);
        Assert.Equal(0, hub.Writes);
        Assert.Contains("403", vm.Message);
        await vm.FlushAsync();
    }
    [AvaloniaFact]
    public async Task Uncertain_comment_is_reconciled_and_never_automatically_resent()
    {
        await using var f = new GitFixture();
        var hub = new FakeGitHub { FailAfterSend = true };
        using var section = Section(f, hub.Current);
        var vm = new ReviewViewModel(hub, hub, new ApplicationStore(f.Root), f.Reader, FakeGitHub.Context, hub.Current, section, false);
        await vm.InitializeAsync();
        vm.Composer = "Check this";
        await vm.PostCommentCommand.ExecuteAsync(null);
        Assert.Equal(1, hub.Writes);
        Assert.False(vm.CanPublish);
        await vm.PostCommentCommand.ExecuteAsync(null);
        Assert.Equal(1, hub.Writes);
        var posted = Assert.Single(hub.Published);
        hub.Published.Clear();
        await vm.ReloadAsync();
        Assert.Contains("still uncertain", vm.ReviewStatus);
        Assert.Equal("Check this", vm.Composer);
        hub.Published.Add(posted);
        await vm.ReloadAsync();
        Assert.True(vm.CanPublish);
        Assert.Contains("confirmed", vm.Message);
        Assert.Equal(1, hub.Writes);
        await vm.FlushAsync();
    }
    [AvaloniaFact]
    public async Task Review_submission_and_demo_write_guard_are_explicit()
    {
        await using var f = new GitFixture();
        var hub = new FakeGitHub();
        using var section = Section(f, hub.Current);
        var store = new ApplicationStore(f.Root);
        var demo = new ReviewViewModel(hub, hub, store, f.Reader, FakeGitHub.Context, hub.Current, section, true);
        await demo.InitializeAsync();
        demo.Composer = "Do not send";
        await demo.PostCommentCommand.ExecuteAsync(null);
        Assert.Equal(0, hub.Writes);
        await demo.FlushAsync();
        var real = new ReviewViewModel(hub, hub, store, f.Reader, FakeGitHub.Context, hub.Current, section, false);
        await real.InitializeAsync();
        real.Summary = "Looks good";
        real.Decision = "APPROVE";
        await real.SubmitReviewCommand.ExecuteAsync(null);
        Assert.Equal(1, hub.Writes);
        Assert.Equal("APPROVE", Assert.Single(hub.Submitted).State);
        Assert.Equal("", real.Summary);
        await real.FlushAsync();
    }
}
