using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

namespace Stacker.Tests;

public sealed partial class FoundationLoadTests
{
    [AvaloniaFact]
    public async Task File_patch_is_visible_before_full_source_and_a_new_selection_cancels_enrichment()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        await fixture.Write("other.txt", "old\n");
        await fixture.Commit("two files");
        var basis = await fixture.Git("rev-parse", "HEAD");
        await fixture.Write("base.txt", "first patch\n");
        await fixture.Write("other.txt", "second patch\n");
        await fixture.Commit("changes");
        var head = await fixture.Git("rev-parse", "HEAD");
        var files = await fixture.Reader.FilesAsync(fixture.Root, basis, head);
        var paused = new PausedGitRead(fixture.Runner, request => request.Arguments.Contains("cat-file"));
        var reader = new GitRepositoryReader(paused, new());
        using var section = new DiffSectionViewModel(reader, fixture.Root, new("layer", "base", basis, head, null, files),
            selectFirstFile: false);
        var timeout = TimeSpan.FromSeconds(10);
        var first = section.LoadAsync(files.Single(file => file.NewPath == "base.txt"));
        await paused.Started.Task.WaitAsync(timeout);
        Assert.False(first.IsCompleted);
        Assert.False(section.IsBusy);
        Assert.Contains(section.Lines, row => row.IsAdded && row.Text == "first patch");

        await section.LoadAsync(files.Single(file => file.NewPath == "other.txt")).WaitAsync(timeout);
        await first.WaitAsync(timeout);
        Assert.Contains(section.Lines, row => row.IsAdded && row.Text == "second patch");
        Assert.DoesNotContain(section.Lines, row => row.Text == "first patch");
    }

    [AvaloniaFact]
    public async Task History_opens_while_the_selected_layer_is_blocked_and_can_be_closed()
    {
        await using var fixture = new GitFixture();
        await fixture.Linear();
        var settingsRoot = Directory.CreateTempSubdirectory("history-open-").FullName;
        try
        {
            var stacks = new YamlStackStore();
            await stacks.SaveAsync(fixture.Root, [fixture.Stack], null);
            var state = new ApplicationStore(settingsRoot);
            var store = new SqliteHistoryStore(settingsRoot);
            var paused = new PausedGitRead(fixture.Runner, request => request.Arguments.Contains("cat-file"));
            var git = new GitExecutable();
            var objects = new HistoryGit(paused, git, state);
            var comparisons = new VersionComparisonService(fixture.Reader, objects);
            using var vm = new MainViewModel(fixture.Reader, stacks, new JsonSettingsStore(settingsRoot), git,
                new(fixture.Reader), new(fixture.Reader), applicationStore: state, historyStore: store,
                historyObjects: objects, historyGraph: new(store), historyComparisons: comparisons, historyProgress: new(store, comparisons));
            await vm.OpenRepositoryAsync(fixture.Root);
            var opening = vm.ShowHistoryAsync();
            Assert.True(vm.IsHistoryOpen);
            var timeout = TimeSpan.FromSeconds(10);
            await paused.Started.Task.WaitAsync(timeout);
            Assert.NotNull(vm.ActiveHistory);
            Assert.True(vm.ActiveHistory.IsBusy);
            Assert.False(vm.IsBusy);

            vm.CloseHistoryCommand.Execute(null);
            await opening.WaitAsync(timeout);
            Assert.False(vm.IsHistoryOpen);
            Assert.Null(vm.ActiveHistory);
            Assert.Empty(vm.HistoryError);
            await vm.ShutdownAsync();
        }
        finally { GitFixture.DeleteDirectory(settingsRoot); }
    }

    [AvaloniaFact]
    public async Task Refresh_reuses_comparison_and_scans_worktree_once_when_inputs_match()
    {
        await using var fixture = new GitFixture();
        await fixture.Linear();
        await new YamlStackStore().SaveAsync(fixture.Root, [fixture.Stack], null);
        var settingsRoot = Directory.CreateTempSubdirectory("refresh-reads-").FullName;
        try
        {
            using var services = Composition.Build(settingsRoot);
            var vm = services.GetRequiredService<MainViewModel>();
            var log = services.GetRequiredService<IGitDiagnosticLog>();
            await vm.OpenRepositoryAsync(fixture.Root);
            var view = Assert.Single(vm.Sections);
            var before = log.Snapshot().Select(entry => entry.Id).ToHashSet();
            await vm.RefreshAsync();
            var refresh = log.Snapshot().Where(entry => !before.Contains(entry.Id)).ToArray();
            Assert.Same(view, Assert.Single(vm.Sections));
            Assert.Single(refresh, entry => entry.Command.Contains(" --porcelain=v2", StringComparison.Ordinal));
            Assert.DoesNotContain(refresh, entry => entry.Command.Contains(" merge-base ", StringComparison.Ordinal) ||
                entry.Command.Contains(" --numstat ", StringComparison.Ordinal));
            await vm.ShutdownAsync();
        }
        finally { GitFixture.DeleteDirectory(settingsRoot); }
    }

    [Fact]
    public async Task Status_read_rejects_a_same_commit_branch_switch_in_a_linked_worktree()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        await fixture.Git("branch", "linked");
        var linked = await fixture.AddWorktree();
        var journal = fixture.CreateJournal();
        var paused = new PausedGitRead(fixture.Runner, request => request.Arguments.Contains("--cached"));
        var author = new WorkingTreeAuthoring(paused, new(), fixture.Reader, new(new(journal)), journal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = author.ReadAsync(linked, timeout.Token);
        await paused.Started.Task.WaitAsync(timeout.Token);
        await fixture.GitAt(linked, "symbolic-ref", "HEAD", "refs/heads/linked");
        paused.Resume();
        var result = await reading;
        Assert.True(result.IsFailed);
        Assert.Contains("checkout changed", result.Problem!.Message);
        Assert.Equal("linked", (await author.ReadAsync(linked)).OrThrow().State.Branch);
        Assert.Equal("main", (await author.ReadAsync(fixture.Root)).OrThrow().State.Branch);
    }
}
