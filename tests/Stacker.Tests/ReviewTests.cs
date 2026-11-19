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
    public async Task Publication_buttons_follow_text_destinations_and_review_decision_without_writes()
    {
        await using var f = new GitFixture();
        await f.Init();
        await f.Git("checkout", "-b", "feature");
        await f.Write("other.cs", "first line\n");
        await f.Commit("add review file");
        var hub = new FakeGitHub
        {
            Current = DiscoveryTests.Pr(42, "main", "feature") with
            {
                BaseSha = await f.Git("rev-parse", "main"),
                HeadSha = await f.Git("rev-parse", "feature")
            }
        };
        using var section = new DiffSectionViewModel(f.Reader, f.Root,
            new("feature", "main", hub.Current.BaseSha, hub.Current.HeadSha, null,
                [new("other.cs", "other.cs", FileChangeKind.Added, 1, 0, "000000", "100644")]))
        {
            IsPrSnapshot = true,
            AssociatedPr = hub.Current,
            GitHubContext = FakeGitHub.Context
        };
        await section.LoadAsync(section.SelectedFile);
        var thread = new ReviewThread("thread", false, false, true, null,
            [new("comment", "reviewer", "Please check this file.", "", DateTimeOffset.UtcNow)], "other.cs", true);
        using (var review = new ReviewViewModel(hub, hub, new ApplicationStore(f.Root), f.Reader,
            FakeGitHub.Context, hub.Current, section, false))
        {
            await review.InitializeAsync();
            var changes = new List<string?>();
            review.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            Assert.True(review.CanPublish);
            AssertBlocked(review);
            review.Composer = " \t\n";
            review.FileComposer = " \n";
            review.Summary = " \t";
            AssertBlocked(review);

            changes.Clear();
            review.Composer = "Pull request feedback";
            Assert.True(review.CanPostComment);
            Assert.Contains(nameof(ReviewViewModel.CanPostComment), changes);
            Assert.False(review.CanReply);
            review.SelectedThread = thread with { Comments = [] };
            Assert.False(review.CanReply);
            changes.Clear();
            review.SelectedThread = thread;
            Assert.True(review.CanReply);
            Assert.Contains(nameof(ReviewViewModel.CanReply), changes);
            review.SelectedThread = null;
            Assert.False(review.CanReply);

            changes.Clear();
            review.FileComposer = "File feedback";
            Assert.True(review.CanPostFileComment);
            Assert.Contains(nameof(ReviewViewModel.CanPostFileComment), changes);
            review.SelectedReviewFile = null;
            Assert.False(review.CanPostFileComment);
            review.SelectedReviewFile = "not-a-current-file.cs";
            review.FileComposer = "Feedback for an unavailable file";
            Assert.False(review.CanPostFileComment);
            changes.Clear();
            review.SelectedReviewFile = "other.cs";
            Assert.True(review.CanPostFileComment);
            Assert.Contains(nameof(ReviewViewModel.CanPostFileComment), changes);

            changes.Clear();
            review.Summary = "Review summary";
            Assert.True(review.CanSubmitReview);
            Assert.Contains(nameof(ReviewViewModel.CanSubmitReview), changes);
            review.Summary = " ";
            Assert.False(review.CanSubmitReview);
            review.Decision = "APPROVE";
            Assert.True(review.CanSubmitReview);
            review.Decision = "COMMENT";
            Assert.False(review.CanSubmitReview);

            review.SetSelection([Assert.Single(section.Lines, line => line.Kind == DiffLineKind.Added)]);
            review.Composer = "Line feedback";
            changes.Clear();
            await review.AddToDraftCommand.ExecuteAsync(null);
            var saved = Assert.Single(review.DraftComments);
            Assert.True(review.CanSubmitReview);
            Assert.Contains(nameof(ReviewViewModel.CanSubmitReview), changes);
            review.Decision = "REQUEST_CHANGES";
            Assert.False(review.CanSubmitReview);
            review.Summary = "Explain the requested changes.";
            Assert.True(review.CanSubmitReview);
            review.Summary = "\n ";
            Assert.False(review.CanSubmitReview);
            review.Decision = "COMMENT";
            changes.Clear();
            review.RemoveDraftCommentCommand.Execute(saved);
            Assert.False(review.CanSubmitReview);
            Assert.Contains(nameof(ReviewViewModel.CanSubmitReview), changes);

            review.Composer = "Ready to reply";
            review.SelectedThread = thread;
            review.Summary = "Ready to review";
            Assert.True(review.CanPostComment && review.CanPostFileComment && review.CanReply && review.CanSubmitReview);
            changes.Clear();
            review.IsBusy = true;
            AssertBlocked(review);
            Assert.Contains(nameof(ReviewViewModel.CanPostComment), changes);
            Assert.Contains(nameof(ReviewViewModel.CanPostFileComment), changes);
            Assert.Contains(nameof(ReviewViewModel.CanReply), changes);
            Assert.Contains(nameof(ReviewViewModel.CanSubmitReview), changes);
            review.IsBusy = false;
            Assert.True(review.CanPostComment && review.CanPostFileComment && review.CanReply && review.CanSubmitReview);
            await review.FlushAsync();
            Assert.Equal(0, hub.Writes);
        }

        var pendingStore = new ApplicationStore(Path.Combine(f.Root, "pending-draft"));
        var pending = new ReviewDraft
        {
            ContextIdentity = FakeGitHub.Context.Identity,
            PullRequest = hub.Current.Number,
            BaseSha = hub.Current.BaseSha,
            HeadSha = hub.Current.HeadSha,
            EditorVersion = 1,
            Summary = "Ready to review",
            ContextComposers = new() { ["pr"] = "Ready to reply" },
            FileComposers = new() { ["other.cs"] = "File feedback" },
            PendingOperation = PendingOperation.For("comment", "unconfirmed-send").Encode()
        };
        (await new ReviewSessionService(pendingStore, hub).SaveAsync(FakeGitHub.Context.Identity + "/" + hub.Current.Number, pending)).OrThrow();
        using var blocked = new ReviewViewModel(hub, hub, pendingStore, f.Reader, FakeGitHub.Context, hub.Current, section, false);
        await blocked.InitializeAsync();
        blocked.SelectedThread = thread;
        Assert.True(blocked.HasPendingPublication);
        AssertBlocked(blocked);
        await blocked.FlushAsync();
        Assert.Equal(0, hub.Writes);

        static void AssertBlocked(ReviewViewModel review)
        {
            Assert.False(review.CanPostComment);
            Assert.False(review.CanPostFileComment);
            Assert.False(review.CanReply);
            Assert.False(review.CanSubmitReview);
        }
    }
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
