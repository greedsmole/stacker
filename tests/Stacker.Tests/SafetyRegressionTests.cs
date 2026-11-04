using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;

namespace Stacker.Tests;

public sealed class SafetyRegressionTests
{
    internal sealed class FailedFiles : IAtomicFile
    {
        public Result<byte[]?> Read(string path) => Result<byte[]?>.Ok(null);
        public Task<Result<Unit>> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            Task.FromResult(Result<Unit>.Fail(ProblemCodes.StoreWrite, "injected disk full"));
        public void SweepTemporaries(string path) { }

    Result<Unit> IAtomicFile.SweepTemporaries(string path) => Boundary.Capture(() => SweepTemporaries(path), ProblemCodes.StoreWrite);
    }

    private sealed class FailedJournalStore : IStateStore<JournalLog>
    {
        public Result<JournalLog?> Read(string identity) => Result<JournalLog?>.Ok(null);
        public Task<Result<Unit>> WriteAsync(string identity, JournalLog value, CancellationToken ct = default) =>
            Task.FromResult(Result<Unit>.Fail(ProblemCodes.StoreWrite, "injected disk full"));
        public Result<string> Quarantine(string identity) => Result<string>.Fail(ProblemCodes.StoreWrite, "disk full");
    }

    private static async Task AdvanceMain(GitFixture f, string file = "main2.txt")
    {
        await f.Git("checkout", "main"); await f.Write(file, "main advanced\n"); await f.Commit("advance main");
    }

    [Fact] public async Task Failed_journal_write_prevents_every_ref_change()
    {
        await using var f = new GitFixture(); await f.Linear(); await AdvanceMain(f);
        var journal = new JsonJournal(new FailedJournalStore(), new SystemClock());
        var writer = new LocalGitClient(f.Runner, new(), f.Reader, journal, new SystemClock());
        var before = await f.Git("show-ref");
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        try { await writer.RestackAsync(plan); } catch (StackerException) { }
        Assert.Equal(before, await f.Git("show-ref"));
    }

    [Theory]
    [InlineData(403)] [InlineData(404)] [InlineData(422)] [InlineData(500)]
    public async Task Json_api_errors_are_not_successful_comments(int status)
    {
        var runner = new ScriptedRunner(_ => new(1, "{\"message\":\"Validation Failed\"}", $"HTTP {status}"));
        await Assert.ThrowsAsync<StackerException>(() => new GitHubCli(runner, new(), new())
            .CommentAsync(FakeGitHub.Context, 42, "keep this text"));
    }

    [Fact] public async Task Startup_lock_does_not_suppress_legacy_import()
    {
        var parent = Path.Combine(Path.GetTempPath(), "stacker-migrate-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new ApplicationStore(Path.Combine(parent, "v2")).WriteAsync("drafts", "key", new ReviewDraft { Composer = "legacy text" });
            using var single = new SingleInstanceLock(Path.Combine(parent, "v3"));
            Assert.True(single.TryAcquire());
            var store = new ApplicationStore(Path.Combine(parent, "v3"));
            Assert.Equal("legacy text", (await store.ReadAsync<ReviewDraft>("drafts", "key"))?.Composer);
        }
        finally { Directory.Delete(parent, true); }
    }

    [AvaloniaFact] public async Task Failed_draft_flush_blocks_publication_and_preserves_text()
    {
        await using var f = new GitFixture(); var hub = new FakeGitHub();
        using var section = new DiffSectionViewModel(f.Reader, f.Root,
            new("feature", "main", hub.Current.BaseSha, hub.Current.HeadSha, null, []))
            { IsPrSnapshot = true, AssociatedPr = hub.Current, GitHubContext = FakeGitHub.Context };
        var vm = new ReviewViewModel(hub, hub, new ApplicationStore(f.Root, new FailedFiles()), f.Reader,
            FakeGitHub.Context, hub.Current, section, false);
        await vm.InitializeAsync(); vm.Composer = "precious text";
        await Assert.ThrowsAsync<StackerException>(() => vm.FlushAsync());
        await vm.PostCommentCommand.ExecuteAsync(null);
        Assert.Equal(0, hub.Writes); Assert.Equal("precious text", vm.Composer);
    }

    [Fact] public async Task Undo_checks_every_layer_before_moving_any_ref()
    {
        await using var f = new GitFixture(); await f.Linear(); await AdvanceMain(f);
        var writer = f.Writer(); var root = await f.ResolvedRoot();
        Assert.True((await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack))).Applied);
        await f.Git("checkout", "C"); await f.Write("new.txt", "new work\n"); await f.Commit("new work");
        await f.Git("checkout", "main");
        var before = await f.Git("show-ref");
        await Assert.ThrowsAsync<StackerException>(() => writer.UndoLastAsync(root));
        Assert.Equal(before, await f.Git("show-ref"));
    }

    [Fact] public async Task Amend_parent_replays_only_the_childs_own_commits()
    {
        await using var f = new GitFixture(); await f.Linear();
        await f.Git("checkout", "A"); await f.Write("A.txt", "amended A\n");
        await f.Git("add", "--all"); await f.Git("commit", "--amend", "--no-edit");
        var writer = f.Writer();
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.Equal(1, plan.Steps.Single(s => s.BranchName == "B").CommitCount);
        var outcome = await writer.RestackAsync(plan);
        Assert.True(outcome.Applied, outcome.Message);
        Assert.Equal("amended A", (await f.Git("show", "refs/heads/C:A.txt", "--")).Trim());
    }

    [Fact] public async Task Another_restack_keeps_unrestored_stash()
    {
        await using var f = new GitFixture(); await f.Linear(); await AdvanceMain(f);
        var writer = f.Writer(); var root = await f.ResolvedRoot();
        await f.Write("base.txt", "work in progress\n");
        var stash = await writer.StashAsync(root, "keep me");
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.True((await writer.RestackAsync(plan with { StashRef = stash })).Applied);
        await AdvanceMain(f, "main3.txt");
        Assert.True((await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack))).Applied);
        Assert.Equal(stash, (await writer.ActiveJournalAsync(root))?.StashRef);
        await writer.RestoreStashAsync(root);
        Assert.Equal("work in progress", File.ReadAllText(Path.Combine(root, "base.txt")).Trim());
    }

    [Fact] public async Task Aborting_a_terminal_merge_does_not_undo_an_earlier_completed_restack()
    {
        await using var f = new GitFixture(); await f.Linear(); await AdvanceMain(f);
        var writer = f.Writer(); var root = await f.ResolvedRoot();
        Assert.True((await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack))).Applied);
        var rewrittenA = await f.Git("rev-parse", "A");
        await f.Git("checkout", "main"); await f.Git("merge", "--no-commit", "--no-ff", "A");
        Assert.True((await writer.StateAsync(root)).HasOperation);
        Assert.True((await writer.AbortAsync(root)).RolledBack);
        Assert.Equal(rewrittenA, await f.Git("rev-parse", "A"));
        Assert.False((await writer.StateAsync(root)).HasOperation);
    }

    [AvaloniaFact] public async Task A_partial_push_never_offers_branch_restoration_or_claims_remote_rollback()
    {
        await using var f = new GitFixture(); await f.Linear(); var root = await f.ResolvedRoot();
        var journal = f.CreateJournal();
        var entry = (await journal.OpenAsync(root, "local.push", [new("A", "push", "refs/heads/A", new string('a', 40), new string('b', 40), StepState.Done)])).OrThrow();
        (await journal.CloseAsync(entry, JournalPhase.NeedsUser, message: "Published A; B failed.")).OrThrow();
        using var vm = new MainViewModel(f.Reader, new YamlStackStore(), new JsonSettingsStore(f.Root + " settings"),
            new GitExecutable(), new StackService(f.Reader), new DiffService(f.Reader), branchWriter: f.Writer());
        await vm.OpenRepositoryAsync(root);
        Assert.True(vm.HasRecovery); Assert.False(vm.CanRestoreRecovery); Assert.Equal("Dismiss", vm.RecoveryAction);
        Assert.False((await vm.RestoreRecoveryAsync()).Ok);
        Assert.Equal(JournalPhase.NeedsUser, (await journal.ByIdAsync(root, entry.Id)).OrThrow()!.Phase);
    }

    [Fact] public async Task Undo_never_resets_a_planned_but_unexecuted_layer()
    {
        await using var f = new GitFixture(); await f.Linear();
        var journal = f.CreateJournal(); var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A"); var oldB = await f.Git("rev-parse", "B");
        var entry = (await journal.OpenAsync(root, "restack",
            [new("original", "original", "main", "main", null, StepState.Planned),
             new("A", "rebase", "refs/heads/A", oldA, null, StepState.Planned),
             new("B", "rebase", "refs/heads/B", oldB, null, StepState.Planned)])).OrThrow();
        await f.Git("checkout", "A"); await f.Git("commit", "--amend", "-m", "rewritten A");
        entry = (await journal.StepAsync(entry, entry.Steps[1].Finished(oldA, await f.Git("rev-parse", "A"), StepState.Done))).OrThrow();
        (await journal.CloseAsync(entry, JournalPhase.Applied)).OrThrow();
        await f.Git("checkout", "B"); await f.Write("new.txt", "new user work\n"); await f.Commit("user work");
        var userB = await f.Git("rev-parse", "B");
        await new LocalGitClient(f.Runner, new(), f.Reader, journal, new SystemClock()).UndoLastAsync(root);
        Assert.Equal(userB, await f.Git("rev-parse", "B"));
    }
}
