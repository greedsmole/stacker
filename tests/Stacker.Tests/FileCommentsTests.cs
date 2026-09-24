using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class FileCommentsTests
{
    private static DiffSectionViewModel Section(GitFixture f, PullRequest pr, bool snapshot = true) => new(f.Reader, f.Root,
        new("feature", "main", pr.BaseSha, pr.HeadSha, null,
        [new("before ü.png", "renamed ü.png", FileChangeKind.Renamed, null, null, "100644", "100644"),
         new("other.cs", "other.cs", FileChangeKind.Modified, 1, 0, "100644", "100644")]))
        { IsPrSnapshot = snapshot, AssociatedPr = pr, GitHubContext = FakeGitHub.Context };
    private static ReviewViewModel View(FakeGitHub hub, ApplicationStore store, GitFixture f, DiffSectionViewModel section, bool demo = false) =>
        new(hub, hub, store, f.Reader, FakeGitHub.Context, section.AssociatedPr!, section, demo);

    [Fact] public async Task File_and_PR_posts_use_distinct_endpoints_and_file_has_no_line_anchor()
    {
        var runner = new ScriptedRunner(_ => new(0, "{}", "")); var cli = new GitHubCli(runner, new(), new()); var pr = DiscoveryTests.Pr(42, "main", "feature");
        await cli.FileCommentAsync(FakeGitHub.Context, pr, "renamed ü.png", "File comment\nwith text");
        var file = runner.Calls[0]; var payload = JsonDocument.Parse(file.StandardInput!).RootElement;
        Assert.Contains("repos/team/repo/pulls/42/comments", file.Arguments); Assert.Contains("--input", file.Arguments);
        Assert.Equal("file", payload.GetProperty("subject_type").GetString()); Assert.Equal("renamed ü.png", payload.GetProperty("path").GetString());
        Assert.Equal(pr.HeadSha, payload.GetProperty("commit_id").GetString()); Assert.False(payload.TryGetProperty("line", out _)); Assert.False(payload.TryGetProperty("position", out _));
        await cli.CommentAsync(FakeGitHub.Context, 42, "PR comment");
        Assert.Contains("repos/team/repo/issues/42/comments", runner.Calls[1].Arguments);
        Assert.Equal("PR comment", JsonDocument.Parse(runner.Calls[1].StandardInput!).RootElement.GetProperty("body").GetString());
    }
    [Fact] public async Task File_validation_accepts_binary_and_new_rename_path_but_rejects_old_or_absent_path()
    {
        var runner = new ScriptedRunner(_ => new(0, """[[{"filename":"renamed ü.png","previous_filename":"before ü.png","status":"renamed"}]]""", ""));
        var cli = new GitHubCli(runner, new(), new()); var pr = DiscoveryTests.Pr(42, "main", "feature");
        await cli.ValidateFileAsync(FakeGitHub.Context, pr, "renamed ü.png");
        await Assert.ThrowsAsync<StackerException>(() => cli.ValidateFileAsync(FakeGitHub.Context, pr, "before ü.png"));
        Assert.Contains("--paginate", runner.Calls[0].Arguments);
    }
    [Fact] public async Task Discussion_preserves_file_subject_path_replies_and_outdated_line_identity()
    {
        var page = 0;
        object Comment(int id) => new { id = "node" + id, databaseId = id, body = "Body " + id, url = "https://example.invalid", createdAt = "2026-09-24T00:00:00Z", author = new { login = "alice" }, commit = new { oid = new string('b', 40) } };
        object Thread(string id, string subject, bool outdated) => new { id, subjectType = subject, path = "old ü.cs", isResolved = false, isOutdated = outdated, viewerCanResolve = true, viewerCanUnresolve = false, line = (int?)null, startLine = (int?)null, diffSide = "RIGHT", startDiffSide = (string?)null, comments = new { nodes = new[] { Comment(1), Comment(2) }, pageInfo = new { hasNextPage = false, endCursor = (string?)null } } };
        var runner = new ScriptedRunner(r =>
        {
            if (!r.Arguments.Contains("graphql")) return new(0, "[[]]", "");
            page++; var connection = new { nodes = new[] { Thread(page.ToString(), page == 1 ? "FILE" : "LINE", page == 2) }, pageInfo = new { hasNextPage = page == 1, endCursor = "next" } };
            return new(0, JsonSerializer.Serialize(new { data = new { repository = new { pullRequest = new { reviewThreads = connection } } } }), "");
        });
        var result = await new GitHubCli(runner, new(), new()).DiscussionAsync(FakeGitHub.Context, DiscoveryTests.Pr(42, "main", "feature"));
        Assert.Equal(2, result.Threads.Count); var file = result.Threads[0]; Assert.True(file.IsFileLevel); Assert.Null(file.Anchor); Assert.Equal("old ü.cs", file.Path); Assert.Equal(2, file.Comments.Count);
        var outdated = result.Threads[1]; Assert.False(outdated.IsFileLevel); Assert.True(outdated.IsOutdated); Assert.Contains("old ü.cs", outdated.Label); Assert.Null(outdated.Anchor);
        Assert.Contains("subjectType", runner.Calls[2].StandardInput!); Assert.Contains("next", runner.Calls[3].StandardInput!);
    }
    [AvaloniaFact] public async Task File_drafts_survive_file_switch_and_restart_and_publish_separately_from_PR()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub(); var store = new ApplicationStore(f.Root); using var section = Section(f, hub.Current);
        var vm = View(hub, store, f, section); await vm.InitializeAsync(); vm.Composer = "PR draft"; vm.FileComposer = "First file";
        vm.SelectedReviewFile = "other.cs"; vm.FileComposer = "Second file"; await vm.FlushAsync();
        var reopened = View(hub, store, f, section); await reopened.InitializeAsync(); Assert.Equal("First file", reopened.FileComposer); Assert.Equal("PR draft", reopened.Composer);
        await reopened.PostFileCommentCommand.ExecuteAsync(null); Assert.Equal(1, hub.Writes); Assert.Equal("", reopened.FileComposer); Assert.Equal("PR draft", reopened.Composer); Assert.Single(reopened.FileThreads);
        reopened.SelectedReviewFile = "other.cs"; Assert.Equal("Second file", reopened.FileComposer); Assert.Empty(reopened.FileThreads);
        await reopened.PostCommentCommand.ExecuteAsync(null); Assert.Equal(2, hub.Writes); Assert.Equal("Second file", reopened.FileComposer); Assert.Single(reopened.Comments); await reopened.FlushAsync();
    }
    [AvaloniaFact] public async Task File_send_rechecks_version_membership_and_preserves_text_on_failure()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub(); var store = new ApplicationStore(f.Root); using var section = Section(f, hub.Current);
        var vm = View(hub, store, f, section); await vm.InitializeAsync(); vm.FileComposer = "Keep file draft";
        hub.Current = hub.Current with { HeadSha = new('c', 40) }; await vm.PostFileCommentCommand.ExecuteAsync(null); Assert.Equal(0, hub.Writes); Assert.Contains("changed", vm.Message);
        hub.Current = section.AssociatedPr!; hub.RejectFile = true; await vm.PostFileCommentCommand.ExecuteAsync(null); Assert.Equal(0, hub.Writes); Assert.Contains("disappeared", vm.Message); Assert.Equal("Keep file draft", vm.FileComposer); await vm.FlushAsync();
        var demo = View(hub, store, f, section, true); await demo.InitializeAsync(); await demo.PostFileCommentCommand.ExecuteAsync(null); Assert.Equal(0, hub.Writes); Assert.False(demo.CanPostFile); await demo.FlushAsync();
    }
    [AvaloniaFact] public async Task Uncertain_file_send_reconciles_after_restart_without_duplicate_or_clearing_other_file()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub { FailAfterSend = true }; var store = new ApplicationStore(f.Root); using var section = Section(f, hub.Current);
        var vm = View(hub, store, f, section); await vm.InitializeAsync(); vm.FileComposer = "Only once"; await vm.PostFileCommentCommand.ExecuteAsync(null);
        Assert.False(vm.CanPublish); Assert.Equal(1, hub.Writes); await vm.PostFileCommentCommand.ExecuteAsync(null); Assert.Equal(1, hub.Writes);
        vm.SelectedReviewFile = "other.cs"; vm.FileComposer = "Keep other draft"; await vm.FlushAsync();
        var reopened = View(hub, store, f, section); await reopened.InitializeAsync(); Assert.True(reopened.CanPublish); Assert.Equal("", reopened.FileComposer); Assert.Equal(1, hub.Writes);
        reopened.SelectedReviewFile = "other.cs"; Assert.Equal("Keep other draft", reopened.FileComposer); await reopened.FlushAsync();
    }
    [AvaloniaFact] public async Task PR_discussion_can_post_file_comments_without_downloading_git_objects()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub(); var store = new ApplicationStore(f.Root);
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", hub.Current.BaseSha, hub.Current.HeadSha, null, [])) { AssociatedPr = hub.Current };
        var vm = View(hub, store, f, section); await vm.InitializeAsync(); Assert.True(vm.CanPostFile); Assert.Equal(2, vm.ReviewFiles.Count);
        vm.FileComposer = "Discuss file without a local checkout"; await vm.PostFileCommentCommand.ExecuteAsync(null);
        Assert.Equal(1, hub.Writes); Assert.Single(vm.FileThreads); await vm.FlushAsync();
    }
    [AvaloniaFact] public async Task Review_window_displays_file_and_PR_tabs_without_line_selection()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub(); using var section = Section(f, hub.Current);
        var vm = View(hub, new ApplicationStore(f.Root), f, section, true); await vm.InitializeAsync();
        var window = new ReviewWindow { DataContext = vm }; window.Show();
        var headers = window.GetVisualDescendants().OfType<TabItem>().Select(t => t.Header?.ToString()).ToArray();
        Assert.Contains("PR comments", headers); Assert.Contains("File comments", headers); Assert.False(vm.CanPostFile);
        vm.SelectedReviewFile = "Auth.cs"; Assert.Single(vm.FileThreads); Assert.Contains(vm.CodeThreads, t => t.IsOutdated); window.Close(); await vm.FlushAsync();
    }
}
