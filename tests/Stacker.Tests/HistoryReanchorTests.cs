using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed class HistoryReanchorTests
{
    [AvaloniaFact]
    public async Task Renamed_draft_requires_confirmation_and_preserves_original_anchor()
    {
        await using var f = new GitFixture();
        await f.Init();
        var root = Directory.CreateTempSubdirectory("reanchor-").FullName;
        try
        {
            var basis = await f.Git("rev-parse", "HEAD");
            await f.Git("checkout", "-b", "feature");
            await f.Write("old.cs", "one\ntwo\nthree\nfour\nfive\nsix\n");
            await f.Commit("initial");
            var oldHead = await f.Git("rev-parse", "HEAD");
            await f.Git("mv", "old.cs", "new.cs");
            await f.Write("new.cs", "one\ntwo\nthree\nfour\nfive\nfix\n");
            await f.Commit("rename");
            var currentHead = await f.Git("rev-parse", "HEAD");
            var state = new ApplicationStore(root);
            var objects = new HistoryGit(f.Runner, new(), state);
            (await objects.CaptureAsync(f.Identity, FakeGitHub.Context.Identity, [basis, oldHead, currentHead])).OrThrow();
            var anchor = new ReviewAnchor(42, oldHead, basis, "old.cs", "RIGHT", 1);
            await state.WriteAsync("drafts", FakeGitHub.Context.Identity + "/42", new ReviewDraft
            {
                EditorVersion = 1,
                BaseSha = basis,
                HeadSha = oldHead,
                Comments = [new(anchor, "Check contract")]
            });
            var hub = new FakeGitHub { Current = DiscoveryTests.Pr(42, "main", "feature") with { BaseSha = basis, HeadSha = currentHead } };
            using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", basis, currentHead, null, await f.Reader.FilesAsync(f.Root, basis, currentHead)))
            {
                IsPrSnapshot = true,
                AssociatedPr = hub.Current,
                GitHubContext = FakeGitHub.Context
            };
            using var review = new ReviewViewModel(hub, hub, state, f.Reader, FakeGitHub.Context, hub.Current, section, false) { HistoryObjects = objects, HistoryRepository = f.Identity };
            await review.InitializeAsync();
            var original = Assert.Single(review.DraftComments);
            await review.ProposeReanchorAsync(original);
            Assert.True(review.HasAnchorProposal, review.Message);
            Assert.Equal(oldHead, Assert.Single(review.DraftComments).Anchor.HeadSha);
            Assert.Equal(0, hub.Writes);
            await review.ConfirmReanchorCommand.ExecuteAsync(null);
            var changed = Assert.Single(review.DraftComments);
            Assert.Equal("new.cs", changed.Anchor.Path);
            Assert.Equal(currentHead, changed.Anchor.HeadSha);
            var saved = await state.ReadAsync<ReviewDraft>("drafts", FakeGitHub.Context.Identity + "/42");
            Assert.Equal(anchor, Assert.Single(saved!.PreviousAnchors).Anchor);
            Assert.Equal(0, hub.Writes);
        }
        finally { Directory.Delete(root, true); }
    }
}
