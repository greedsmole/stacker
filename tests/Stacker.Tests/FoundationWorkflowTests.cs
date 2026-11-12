using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed class FoundationWorkflowTests
{
    [AvaloniaFact]
    public async Task Comments_can_be_posted_in_sequence_and_as_one_review_while_discussion_refresh_waits()
    {
        await using var f = new GitFixture();
        var reader = new FakeGitHub();
        var pr = reader.Current;
        var database = new SqliteHistoryStore(f.Root);
        var state = new ReviewApplicationState(new(f.Root), database);
        var journal = new RoutingJournal(f.CreateJournal(), database, new SystemClock());
        var runner = new ScriptedRunner(_ => new(0, "{}", ""));
        var writer = new CommandGitHubWriter(new(runner, new(), new()), new(new(journal)));
        var file = new FileChange("a.cs", "a.cs", FileChangeKind.Modified, 1, 1, "100644", "100644");
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", pr.BaseSha, pr.HeadSha, null, [file])) { IsPrSnapshot = true };
        using var review = new ReviewViewModel(reader, writer, state, f.Reader, FakeGitHub.Context, pr, section, false);
        await review.InitializeAsync();
        var discussion = new TaskCompletionSource<ReviewDiscussion>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.DiscussionGate = discussion;
        var added = new RenderedDiffLine(new(DiffLineKind.Added, null, 1, "+new"));
        var removed = new RenderedDiffLine(new(DiffLineKind.Removed, 1, null, "-old"));

        review.SetSelection([added]); review.Composer = "First inline comment";
        await review.PostInlineCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(review.CanPublish);
        Assert.False(discussion.Task.IsCompleted);
        review.SetSelection([removed]); review.Composer = "Second inline comment";
        await review.PostInlineCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(review.CanPublish);

        review.SetSelection([added]); review.Composer = "First review comment";
        await review.AddToDraftCommand.ExecuteAsync(null);
        review.SetSelection([removed]); review.Composer = "Second review comment";
        await review.AddToDraftCommand.ExecuteAsync(null);
        Assert.Equal("Review (2)", review.ReviewLabel);
        await review.SubmitReviewCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(review.DraftComments);
        Assert.Contains("2 line comments", review.ReviewStatus);

        Assert.Equal(3, runner.Calls.Count);
        Assert.All(runner.Calls.Take(2), call => Assert.Contains("repos/team/repo/pulls/42/comments", call.Arguments));
        using var first = JsonDocument.Parse(runner.Calls[0].StandardInput!);
        using var second = JsonDocument.Parse(runner.Calls[1].StandardInput!);
        Assert.StartsWith("First inline comment", first.RootElement.GetProperty("body").GetString());
        Assert.Equal("RIGHT", first.RootElement.GetProperty("side").GetString());
        Assert.StartsWith("Second inline comment", second.RootElement.GetProperty("body").GetString());
        Assert.Equal("LEFT", second.RootElement.GetProperty("side").GetString());
        Assert.Contains("repos/team/repo/pulls/42/reviews", runner.Calls[2].Arguments);
        using var submission = JsonDocument.Parse(runner.Calls[2].StandardInput!);
        var comments = submission.RootElement.GetProperty("comments").EnumerateArray().ToArray();
        Assert.Equal(new[] { "First review comment", "Second review comment" }, comments.Select(comment => comment.GetProperty("body").GetString()));
        Assert.Equal(new[] { "RIGHT", "LEFT" }, comments.Select(comment => comment.GetProperty("side").GetString()));
        var records = journal.Recent("github:" + FakeGitHub.Context.Identity).OrThrow();
        Assert.Equal(3, records.Select(record => record.Id).Distinct().Count());
        Assert.All(records, record => Assert.Equal(JournalPhase.Applied, record.Phase));

        discussion.SetResult(new([], [], []));
        await review.ReloadAsync();
    }

    [AvaloniaTheory]
    [InlineData("HTTP 422", true)]
    [InlineData("HTTP 401", true)]
    [InlineData("HTTP 503", false)]
    public async Task Publication_distinguishes_a_server_rejection_from_an_unknown_result(string response, bool retryAllowed)
    {
        await using var f = new GitFixture(); var reader = new FakeGitHub();
        var app = new ApplicationStore(f.Root); var database = new SqliteHistoryStore(f.Root);
        var state = new ReviewApplicationState(app, database); var journal = f.CreateJournal();
        var bus = new CommandBus(new(journal));
        var writer = new CommandGitHubWriter(new(new ScriptedRunner(_ => new(1, "{}", response)), new(), new()), bus);
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", reader.Current.BaseSha, reader.Current.HeadSha, null, [])) { IsPrSnapshot = true };
        using (var review = new ReviewViewModel(reader, writer, state, f.Reader, FakeGitHub.Context, reader.Current, section, false))
        {
            await review.InitializeAsync(); review.Composer = "Preserve this text";
            await review.PostCommentCommand.ExecuteAsync(null);
            if (retryAllowed) { await review.PostCommentCommand.ExecuteAsync(null); Assert.Equal(2, journal.Recent("github:" + FakeGitHub.Context.Identity).OrThrow().Count); }
            await review.FlushAsync();
            Assert.Equal(retryAllowed, review.CanPublish); Assert.Equal("Preserve this text", review.Composer);
            Assert.DoesNotContain("Invalid publication transition", review.Message);
        }
        using var reopened = new ReviewViewModel(reader, writer, state, f.Reader, FakeGitHub.Context, reader.Current, section, false);
        await reopened.InitializeAsync(); Assert.Equal(retryAllowed, reopened.CanPublish); Assert.Equal("Preserve this text", reopened.Composer);
        await reopened.FlushAsync();
    }

    [AvaloniaFact]
    public async Task Embedded_history_shares_layer_selection_and_restores_navigation()
    {
        await using var f = new GitFixture(); await f.Linear(); var root = Directory.CreateTempSubdirectory("foundation-ui-").FullName;
        try
        {
            await new YamlStackStore().SaveAsync(f.Root, [f.Stack], null);
            using var services = Composition.Build(root); var vm = services.GetRequiredService<MainViewModel>();
            await vm.OpenRepositoryAsync(f.Root); await vm.ShowHistoryAsync(); var history = vm.ActiveHistory!;
            Assert.True(vm.IsHistoryOpen); await history.ShowCurrentCommand.ExecuteAsync(null);
            Assert.Equal("A.txt", Assert.Single(history.DocumentSections).SelectedFile!.NewPath);
            var selection = vm.Session.Selection;
            history.SelectedNode = history.Nodes[1];
            Assert.Equal(selection, vm.Session.Selection);
            await history.LoadLayerCommand.ExecuteAsync(null); await history.ShowCurrentCommand.ExecuteAsync(null);
            Assert.Equal("B.txt", Assert.Single(vm.Sections).SelectedFile!.NewPath);
            Assert.Equal(history.SelectedNode.Id, vm.Session.Selection.ChangeId);
            await history.BackCommand.ExecuteAsync(null);
            Assert.Equal(history.Nodes[0], history.SelectedNode); Assert.Equal("A.txt", Assert.Single(history.DocumentSections).SelectedFile!.NewPath);
            Assert.Equal("A.txt", Assert.Single(vm.Sections).SelectedFile!.NewPath);
            await history.ForwardCommand.ExecuteAsync(null); Assert.Equal(history.Nodes[1], history.SelectedNode);
            Assert.All(history.DocumentSections.SelectMany(s => s.Lines), line => Assert.False(line.IsLineCommentVisible));
            await vm.ShutdownAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Close_failure_can_return_to_editing_and_then_retry()
    {
        await using var f = new GitFixture(); await f.Linear(); var root = Directory.CreateTempSubdirectory("foundation-close-").FullName;
        try
        {
            using var services = Composition.Build(root); var vm = services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = vm }; window.Show(); await vm.OpenRepositoryAsync(f.Root);
            var blocker = Path.Combine(root, "workspace"); if (Directory.Exists(blocker)) Directory.Delete(blocker, true); await File.WriteAllTextAsync(blocker, "blocked");
            var asked = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.CloseFailureHandler = errors => { asked.TrySetResult(errors); return Task.FromResult(UnsavedCloseChoice.ContinueEditing); };
            window.Close(); var problems = await asked.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Contains(problems, e => e.StartsWith("Workspace:"));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }); Assert.True(window.IsVisible);
            File.Delete(blocker); window.CloseFailureHandler = _ => Task.FromResult(UnsavedCloseChoice.Discard); window.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (window.IsVisible) await Task.Delay(10, timeout.Token);
        }
        finally { Directory.Delete(root, true); }
    }
}
