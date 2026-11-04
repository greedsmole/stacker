using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed class SessionLifecycleTests
{
    [AvaloniaFact] public async Task Review_eviction_keeps_the_recent_session_and_flushes_the_actual_oldest()
    {
        await using var f = new GitFixture(); var github = new FakeGitHub();
        var state = new MemoryApplicationState();
        using var vm = new MainViewModel(f.Reader, new YamlStackStore(), new JsonSettingsStore(f.Root + " settings"),
            new GitExecutable(), new StackService(f.Reader), new DiffService(f.Reader), github: github, writer: github,
            applicationStore: state);
        var sections = Enumerable.Range(1, 13).Select(number => new DiffSectionViewModel(f.Reader, f.Root,
            new("feature", "main", github.Current.BaseSha, github.Current.HeadSha, null, []))
            { AssociatedPr = github.Current with { Number = number }, GitHubContext = FakeGitHub.Context, IsPrSnapshot = true }).ToArray();
        try
        {
            var sessions = new List<ReviewViewModel>();
            foreach (var section in sections.Take(12))
            {
                var review = (await vm.EnsureReviewAsync(section))!;
                review.Composer = "draft " + review.PullRequest.Number; sessions.Add(review);
            }
            Assert.Same(sessions[0], await vm.EnsureReviewAsync(sections[0])); // Touch first, so second is now oldest.
            await vm.EnsureReviewAsync(sections[12]);
            Assert.Null(sections[1].Review); // Eviction detaches the old view instead of retaining it.
            Assert.Same(sessions[0], await vm.EnsureReviewAsync(sections[0]));
            var reopened = (await vm.EnsureReviewAsync(sections[1]))!;
            Assert.NotSame(sessions[1], reopened); Assert.Equal("draft 2", reopened.Composer);
            await vm.FlushReviewsAsync();
        }
        finally { foreach (var section in sections) section.Dispose(); }
    }
}
