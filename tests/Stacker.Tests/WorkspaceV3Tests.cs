using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class WorkspaceV3Tests
{
    private static MainViewModel Create(GitFixture f, ApplicationStore store, FakeGitHub? hub = null) =>
        new(f.Reader, new YamlStackStore(), new JsonSettingsStore(Path.Combine(f.Root, "settings")), new(), new(f.Reader), new(f.Reader), hub, hub, new SeededCache(f.Root), store);
    private sealed class SeededCache(string root) : IGitObjectCache
    {
        public Task<RepositorySnapshot> PrepareAsync(GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default) =>
            Task.FromResult(new RepositorySnapshot(root, prs.SelectMany(p => new[] { new BranchRef(GitObjectCache.HeadRef(p), p.HeadSha), new BranchRef(GitObjectCache.BaseRef(p), p.BaseSha) }).ToArray(), 0));
        public Task ClearAsync(GitHubRepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
    }
    [AvaloniaFact] public async Task Clean_diff_preserves_patch_numbers_and_newline_annotation()
    {
        await using var f = new GitFixture(); await f.Linear();
        var a = await f.Git("rev-parse", "A"); var b = await f.Git("rev-parse", "B");
        var files = await f.Reader.FilesAsync(f.Root, a, b);
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("B", "A", a, b, null, files));
        await section.LoadAsync(section.SelectedFile);
        Assert.Contains("diff --git", section.Patch);
        Assert.DoesNotContain(section.Lines, l => l.Text.StartsWith("diff --git") || l.Text.StartsWith("index ") || l.Text.StartsWith("@@") || l.Text.StartsWith("+++"));
        Assert.Contains(section.Lines, l => l.Text == "B" && l.NewLineNumber == 1 && l.Marker == "+");
        var parsed = PatchParser.Parse(files[0], "diff --git a/b b/b\n--- a/b\n+++ b/b\n@@ -1,2 +2,3 @@ method\n x\n-y\n+z\n\\ No newline at end of file\n");
        var h = Assert.Single(parsed.Hunks); Assert.Equal(1, h.OldStart); Assert.Equal(2, h.OldCount); Assert.Equal(2, h.NewStart); Assert.Equal(3, h.NewCount); Assert.Equal("method", h.Context);
        Assert.Contains(h.Lines, l => l.Kind == DiffLineKind.Notice);
    }
    [AvaloniaFact] public async Task Workspace_restores_selection_file_filter_and_scroll_across_restart()
    {
        await using var f = new GitFixture(); await f.Linear(); var store = new ApplicationStore(Path.Combine(f.Root, "app"));
        await new YamlStackStore().SaveAsync(f.Root, [f.Stack], null);
        using (var first = Create(f, store))
        {
            await first.OpenRepositoryAsync(f.Root); await first.SelectLayerAsync(first.LocalGroups[0].Layers[1], false);
            var section = Assert.Single(first.Sections); await section.LoadAsync(section.SelectedFile); section.Filter = "B"; section.ScrollY = 123;
            await first.SaveWorkspaceAsync();
        }
        using var next = Create(f, store); await next.OpenRepositoryAsync(f.Root);
        Assert.Equal(DiffMode.Layer, next.Mode); Assert.Equal(new[] { 1 }, next.SelectedPositions);
        var restored = Assert.Single(next.Sections); Assert.Equal("B", restored.Filter); Assert.Equal("B.txt", restored.SelectedFile?.NewPath); Assert.Equal(123, restored.ScrollY);
        await next.SelectOverviewAsync(next.LocalGroups[0]); Assert.Equal(DiffMode.FullStack, next.Mode);
    }
    [AvaloniaFact] public async Task Background_changes_wait_for_explicit_application_and_no_change_keeps_sections()
    {
        await using var f = new GitFixture(); await f.Linear();
        var hub = new FakeGitHub { Current = DiscoveryTests.Pr(42, "main", "A") with { BaseSha = await f.Git("rev-parse", "main"), HeadSha = await f.Git("rev-parse", "A") } };
        using var vm = Create(f, new ApplicationStore(Path.Combine(f.Root, "app")), hub);
        await vm.OpenRepositoryAsync(f.Root); await vm.SelectLayerAsync(vm.OtherGroups[0].Layers[0], false);
        var section = Assert.Single(vm.Sections); await section.LoadAsync(section.SelectedFile); section.ScrollY = 50;
        vm.ActiveReview!.Summary = "Keep the review";
        await vm.PollAsync(); Assert.Same(section, Assert.Single(vm.Sections)); Assert.False(vm.HasPendingChanges);
        hub.Current = hub.Current with { HeadSha = await f.Git("rev-parse", "B") };
        await vm.PollAsync(); Assert.True(vm.HasPendingChanges); Assert.Same(section, Assert.Single(vm.Sections)); Assert.Equal(50, section.ScrollY);
        await vm.UpdateChangesAsync(); Assert.False(vm.HasPendingChanges); Assert.Equal(hub.Current.HeadSha, vm.Sections[0].Result.HeadSha);
        Assert.Equal("Keep the review", vm.ActiveReview!.Summary); Assert.True(vm.ActiveReview.HasStaleDraft);
        await vm.FlushReviewsAsync();
    }
    [AvaloniaFact] public async Task Line_editors_are_separate_from_PR_and_drafts_are_visible_inline()
    {
        await using var f = new GitFixture(); await f.Linear();
        var hub = new FakeGitHub { Current = DiscoveryTests.Pr(42, "main", "A") with { BaseSha = await f.Git("rev-parse", "main"), HeadSha = await f.Git("rev-parse", "A") } };
        using var vm = Create(f, new ApplicationStore(Path.Combine(f.Root, "app")), hub);
        await vm.OpenRepositoryAsync(f.Root); await vm.SelectLayerAsync(vm.OtherGroups[0].Layers[0], false);
        var section = vm.Sections[0]; await section.LoadAsync(section.SelectedFile); var line = section.Lines.Single(l => l.Kind == DiffLineKind.Added);
        var review = vm.ActiveReview!; review.BeginPrComment(); review.Composer = "PR draft";
        review.SetSelection([line]); review.Composer = "Line draft";
        review.BeginPrComment(); Assert.Equal("PR draft", review.Composer);
        review.SetSelection([line]); Assert.Equal("Line draft", review.Composer);
        await review.AddToDraftCommand.ExecuteAsync(null); Assert.Single(line.Drafts); Assert.Equal("Review (1)", review.ReviewLabel);
        review.DecisionLabel = "Request changes"; await review.SubmitReviewCommand.ExecuteAsync(null); Assert.Equal(0, hub.Writes);
        review.Summary = "Please improve this"; await review.SubmitReviewCommand.ExecuteAsync(null); Assert.Equal(1, hub.Writes); Assert.Empty(line.Drafts);
        review.BeginPrComment(); Assert.Equal("PR draft", review.Composer); await review.FlushAsync();
    }
    [AvaloniaFact] public async Task Discussion_refresh_keeps_unchanged_objects_and_unsent_reply()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub(); var store = new ApplicationStore(f.Root);
        hub.Published.Add(new("1", "alice", "Existing comment", "", DateTimeOffset.UtcNow));
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", hub.Current.BaseSha, hub.Current.HeadSha, null, [])) { AssociatedPr = hub.Current, GitHubContext = FakeGitHub.Context };
        var review = new ReviewSession(hub, hub, store, f.Reader, FakeGitHub.Context, hub.Current, section, false); await review.InitializeAsync();
        var original = review.Comments[0]; review.Composer = "Unsent"; await review.RefreshDiscussionAsync();
        Assert.Same(original, review.Comments[0]); Assert.Equal("Unsent", review.Composer);
        hub.Published.Add(new("2", "bob", "New comment", "", DateTimeOffset.UtcNow)); await review.RefreshDiscussionAsync();
        Assert.Same(original, review.Comments[0]); Assert.Equal(2, review.Comments.Count); Assert.Equal("Unsent", review.Composer); await review.FlushAsync();
    }
    [AvaloniaFact] public async Task Main_window_embeds_review_and_exposes_contextual_actions()
    {
        await using var f = new GitFixture(); await f.Linear();
        var hub = new FakeGitHub { Current = DiscoveryTests.Pr(42, "main", "A") with { BaseSha = await f.Git("rev-parse", "main"), HeadSha = await f.Git("rev-parse", "A") } };
        using var vm = Create(f, new ApplicationStore(Path.Combine(f.Root, "app")), hub);
        var window = new MainWindow { DataContext = vm }; window.Show(); await vm.OpenRepositoryAsync(f.Root);
        await vm.SelectLayerAsync(vm.OtherGroups[0].Layers[0], false); await vm.ShowReviewAsync(); window.UpdateLayout();
        Assert.True(vm.IsReviewOpen); Assert.NotNull(window.GetVisualDescendants().OfType<ReviewPanel>().FirstOrDefault());
        await vm.Sections[0].LoadAsync(vm.Sections[0].SelectedFile); window.UpdateLayout();
        var add = window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is RenderedDiffLine { IsCode: true } && b.Content?.ToString() == "+");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Yield(); window.UpdateLayout();
        Assert.Single(vm.Sections[0].Lines, l => l.HasEditor);
        var inline = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Watermark == "Leave a review comment…" && t.IsEffectivelyVisible && ReferenceEquals(t.DataContext, vm.ActiveReview));
        inline.Text = "Comment entered in the inline editor";
        var addDraft = window.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == "Add to review" && b.IsEffectivelyVisible);
        Assert.Equal("Comment entered in the inline editor", vm.ActiveReview!.Composer);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)addDraft.Command!).ExecuteAsync(addDraft.CommandParameter);
        await vm.ActiveReview.FlushAsync(); window.UpdateLayout();
        Assert.Equal("Comment entered in the inline editor", Assert.Single(vm.ActiveReview.DraftComments).Body);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "Submit review");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsVisible && b.Content?.ToString() == "Restore layer selection");
        await vm.FlushReviewsAsync(); window.Close();
    }
    [Fact] public async Task Coordinator_delays_on_activation_pauses_and_backs_off_without_overlapping()
    {
        var clock = new ManualClock(); var calls = 0;
        using var coordinator = new RefreshCoordinator(_ => { calls++; throw new StackerException("network unavailable"); }, clock);
        coordinator.SetActive(true); Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(10); Assert.Equal(1, calls); Assert.Equal(TimeSpan.FromMinutes(2), coordinator.Interval);
        coordinator.SetActive(false); clock.Advance(TimeSpan.FromHours(1)); await Task.Delay(10); Assert.Equal(1, calls);
        coordinator.SetActive(true); Assert.Equal(1, calls); clock.Advance(TimeSpan.FromMinutes(2)); await Task.Delay(10); Assert.Equal(2, calls);
    }
    [Fact] public async Task Coordinator_suspends_auth_errors_until_reset()
    {
        var clock = new ManualClock(); var calls = 0;
        using var coordinator = new RefreshCoordinator(_ => { calls++; throw new GitHubAuthenticationException("Sign in"); }, clock);
        coordinator.SetActive(true); clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(10); Assert.True(coordinator.Suspended);
        coordinator.SetActive(true); clock.Advance(TimeSpan.FromHours(1)); Assert.Equal(1, calls);
        coordinator.Reset(); coordinator.SetActive(true); clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(10); Assert.Equal(2, calls);
    }
    [AvaloniaFact] public async Task Quiet_discussion_read_does_not_disable_editor_or_lose_typing()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub();
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", hub.Current.BaseSha, hub.Current.HeadSha, null, []));
        var review = new ReviewSession(hub, hub, new ApplicationStore(f.Root), f.Reader, FakeGitHub.Context, hub.Current, section, false); await review.InitializeAsync();
        hub.DiscussionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = review.RefreshDiscussionAsync(); Assert.False(review.IsBusy);
        review.Composer = "Typed while loading";
        hub.DiscussionGate.SetResult(new([], [], [])); await read;
        Assert.Equal("Typed while loading", review.Composer); await review.FlushAsync();
    }
    [AvaloniaFact] public async Task Old_composer_is_recovered_without_guessing_its_destination()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub(); var store = new ApplicationStore(f.Root);
        await store.WriteAsync("drafts", FakeGitHub.Context.Identity + "/42", new ReviewDraft { HeadSha = hub.Current.HeadSha, BaseSha = hub.Current.BaseSha, Composer = "Old ambiguous text" });
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", hub.Current.BaseSha, hub.Current.HeadSha, null, []));
        var review = new ReviewSession(hub, hub, store, f.Reader, FakeGitHub.Context, hub.Current, section, false); await review.InitializeAsync();
        Assert.True(review.HasRecoveredText); Assert.Equal("", review.Composer); Assert.Equal("Old ambiguous text", review.RecoveredText);
        review.RestoreRecoveredTextCommand.Execute(null); Assert.Equal("Old ambiguous text", review.Composer); Assert.False(review.HasRecoveredText); await review.FlushAsync();
    }
    [AvaloniaFact] public async Task Reconciliation_preserves_text_written_after_an_uncertain_review()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub { FailAfterSend = true };
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", hub.Current.BaseSha, hub.Current.HeadSha, null, []));
        var review = new ReviewSession(hub, hub, new ApplicationStore(f.Root), f.Reader, FakeGitHub.Context, hub.Current, section, false); await review.InitializeAsync();
        review.Summary = "First review"; await review.SubmitReviewCommand.ExecuteAsync(null); Assert.True(review.HasPendingPublication);
        review.Summary = "New unsent summary"; await review.ReloadAsync();
        Assert.False(review.HasPendingPublication); Assert.Equal("New unsent summary", review.Summary); Assert.Equal(1, hub.Writes); await review.FlushAsync();
    }
    [AvaloniaFact] public async Task Left_side_range_can_include_context_but_not_added_lines()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub();
        var file = new FileChange("old.cs", "new.cs", FileChangeKind.Renamed, 1, 1, "100644", "100644");
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", hub.Current.BaseSha, hub.Current.HeadSha, null, [file])) { IsPrSnapshot = true };
        var review = new ReviewSession(hub, hub, new ApplicationStore(f.Root), f.Reader, FakeGitHub.Context, hub.Current, section, false); await review.InitializeAsync();
        var context = new RenderedDiffLine(new(DiffLineKind.Context, 1, 1, " same"));
        var removed = new RenderedDiffLine(new(DiffLineKind.Removed, 2, null, "-old"));
        review.SetSelection([context, removed], "LEFT"); review.Composer = "Check old lines"; await review.AddToDraftCommand.ExecuteAsync(null);
        var anchor = Assert.Single(review.DraftComments).Anchor; Assert.Equal("LEFT", anchor.Side); Assert.Equal(1, anchor.StartLine); Assert.Equal(2, anchor.Line); Assert.Equal("new.cs", anchor.Path);
        review.SetSelection([removed, new(new(DiffLineKind.Added, null, 2, "+new"))]); Assert.False(review.HasSelection); await review.FlushAsync();
    }
    [AvaloniaFact] public async Task Canceled_initialization_does_not_overwrite_an_existing_draft()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub(); var store = new ApplicationStore(f.Root);
        var key = FakeGitHub.Context.Identity + "/42";
        await store.WriteAsync("drafts", key, new ReviewDraft { Summary = "Must survive cancellation" });
        using var section = new DiffSectionViewModel(f.Reader, f.Root, new("feature", "main", hub.Current.BaseSha, hub.Current.HeadSha, null, []));
        var review = new ReviewSession(hub, hub, store, f.Reader, FakeGitHub.Context, hub.Current, section, false);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => review.InitializeAsync(ct: cancel.Token));
        await review.FlushAsync(); Assert.Equal("Must survive cancellation", (await store.ReadAsync<ReviewDraft>("drafts", key))!.Summary);
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(this, callback, state); timer.Change(dueTime, period); lock (_timers) _timers.Add(timer); return timer; }
        public void Advance(TimeSpan delta)
        {
            _now += delta; ManualTimer[] timers; lock (_timers) timers = _timers.ToArray();
            foreach (var timer in timers) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime; return true; }
            public void Fire() { if (_due is { } due && due <= clock.GetUtcNow()) { _due = null; callback(state); } }
            public void Dispose() { _due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
