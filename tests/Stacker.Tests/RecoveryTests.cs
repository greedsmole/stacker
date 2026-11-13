// Purpose: BDD specs for the write path: an interrupted effect is recoverable, a resolved conflict stays undoable, a stash never hides undo.
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;

public sealed partial class RecoveryTests
{

    /// <summary>main ← A ← B where main and A both touch conflict.txt, so A conflicts on the replay.</summary>
    private static async Task<StackDefinition> Conflicting(GitFixture f)
    {
        await f.Init();
        await f.Git("checkout", "-b", "A");
        await f.Write("conflict.txt", "A\n");
        await f.Commit("A");
        await f.Git("checkout", "-b", "B");
        await f.Write("B.txt", "B\n");
        await f.Commit("B");
        await f.Git("checkout", "main");
        await f.Write("conflict.txt", "main\n");
        await f.Commit("main moved");
        return new("t", "T", "refs/heads/main", ["refs/heads/A", "refs/heads/B"]);
    }

    // Given a cascade that conflicted, when the user resolves and continues, then undo restores every touched branch.
    [Fact]
    public async Task A_resolved_conflict_is_still_fully_undoable()
    {
        await using var f = new GitFixture();
        var stack = await Conflicting(f);
        var writer = f.Writer();
        var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A");
        var oldB = await f.Git("rev-parse", "B");
        var outcome = await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), stack));
        Assert.True(outcome.Conflicted, outcome.Message);
        Assert.True((await writer.ContinueAsync(root, "true")).Conflicted);
        await f.Write("conflict.txt", "resolved\n");
        await writer.StageAsync(root, (await writer.StateAsync(root)).ConflictedFiles);
        var resumed = await writer.ContinueAsync(root, "true");
        Assert.True(resumed.Applied, resumed.Message);
        // The layer the user resolved is rewritten and recorded, so undo can reverse it.
        Assert.NotEqual(oldA, await f.Git("rev-parse", "A"));
        // Continue uses the original replay boundaries to complete the remaining cascade.
        Assert.NotEqual(oldB, await f.Git("rev-parse", "B"));
        var entry = await writer.LastJournalAsync(root);
        Assert.NotNull(entry);
        Assert.Contains(entry!.Moved, step => step.Target == "refs/heads/A");
        var undone = await writer.UndoLastAsync(root);
        Assert.True(undone.RolledBack, undone.Message);
        Assert.Equal(oldA, await f.Git("rev-parse", "A"));
        Assert.Equal(oldB, await f.Git("rev-parse", "B"));
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.False((await writer.StateAsync(root)).HasOperation);
        Assert.Null(await writer.ActiveJournalAsync(root));
    }

    // Given a restack was applied, when a stash is taken afterwards, then undo is still offered and still works.
    [Fact]
    public async Task A_stash_never_hides_undo()
    {
        await using var f = new GitFixture();
        await f.Linear();
        await f.Git("checkout", "main");
        await f.Write("main2.txt", "moved\n");
        await f.Commit("base moved");
        var writer = f.Writer();
        var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A");
        Assert.True((await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack))).Applied);
        await f.Write("base.txt", "work in progress\n");
        var stash = await writer.StashAsync(root, "Stacker: before the next restack");
        Assert.NotNull(stash);
        var entry = await writer.LastJournalAsync(root);
        Assert.NotNull(entry);
        Assert.True(entry!.CanUndo);
        Assert.Equal(stash, entry.StashRef);
        var undone = await writer.UndoLastAsync(root);
        Assert.True(undone.RolledBack, undone.Message);
        Assert.Equal(oldA, await f.Git("rev-parse", "A"));
        // The stash is still recorded on the entry, so it can be restored afterwards.
        Assert.Equal(stash, (await writer.ActiveJournalAsync(root))?.StashRef);
    }

    // Given an effect fails after the first layer moved, when the restack returns, then every branch is restored.
    [Fact]
    public async Task A_failure_mid_cascade_restores_every_branch()
    {
        await using var f = new GitFixture();
        await f.Linear();
        var journal = f.CreateJournal();
        var writer = new LocalGitClient(f.Runner, new GitExecutable(), f.Reader, new FaultyJournal(journal, failOnStep: 2), new SystemClock());
        await f.Git("checkout", "main");
        await f.Write("main2.txt", "moved\n");
        await f.Commit("base moved");
        var before = new Dictionary<string, string>();
        foreach (var branch in new[] { "A", "B", "C" })
            before[branch] = await f.Git("rev-parse", branch);
        var outcome = await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack));
        Assert.True(outcome.RolledBack, outcome.Message);
        foreach (var branch in new[] { "A", "B", "C" })
            Assert.Equal(before[branch], await f.Git("rev-parse", branch));
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
        var root = await f.ResolvedRoot();
        Assert.Null((await journal.UndoableAsync(root)).OrThrow());
        Assert.Equal(JournalPhase.RolledBack, (journal.Recent(root)).OrThrow()[0].Phase);
    }

    // Given a crash left an entry running with one branch already moved, when recovery runs, then that branch is restored.
    [Fact]
    public async Task A_running_entry_from_a_crash_is_recoverable()
    {
        await using var f = new GitFixture();
        await f.Linear();
        var journal = f.CreateJournal();
        var writer = new LocalGitClient(f.Runner, new GitExecutable(), f.Reader, journal, new SystemClock());
        var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A");
        // What a killed process leaves behind: the entry opened, the first effect done, nothing closed.
        var entry = (await journal.OpenAsync(root, "restack",
            [new JournalStep("original", "original", "main", "main", null, StepState.Planned),
             new JournalStep("layer:0", "rebase", "refs/heads/A", oldA, null, StepState.Planned)])).OrThrow();
        (await journal.StepAsync(entry, entry.Steps[1] with
        {
            State = StepState.Started
        })).OrThrow();
        await f.Git("checkout", "A");
        await f.Git("commit", "--amend", "-m", "A rewritten");
        var moved = await f.Git("rev-parse", "A");
        (await journal.StepAsync(entry, entry.Steps[1].Finished(oldA, moved, StepState.Done))).OrThrow();
        Assert.NotEqual(oldA, moved);
        var active = await writer.ActiveJournalAsync(root);
        Assert.NotNull(active);
        Assert.True(active!.NeedsAttention);
        var aborted = await writer.AbortAsync(root);
        Assert.True(aborted.RolledBack, aborted.Message);
        Assert.Equal(oldA, await f.Git("rev-parse", "A"));
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.Null(await writer.LastJournalAsync(root));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cancelling_a_restack_reports_cancellation_after_its_refs_are_restored(bool resume, bool immediatelyAfterGit)
    {
        await using var f = new GitFixture();
        var stack = resume ? await Conflicting(f) : f.Stack;
        if (!resume)
        {
            await f.Linear();
            await f.Git("checkout", "main");
            await f.Write("main2.txt", "moved\n");
            await f.Commit("base moved");
        }
        var originalTips = await Task.WhenAll(stack.Branches.Select(branch => f.Git("rev-parse", branch)));
        var root = await f.ResolvedRoot();
        var journal = f.CreateJournal();
        var bus = new CommandBus(new(journal));
        var reported = new List<CommandOutcome>();
        bus.Reported += reported.Add;
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        var runner = new InterceptedRunner(f.Runner, (request, token) =>
        {
            if (!armed || immediatelyAfterGit || !request.Arguments.Contains("--is-ancestor"))
                return;
            armed = false;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        }, (request, result) =>
        {
            var action = resume ? "--continue" : "--onto";
            if (!armed || !immediatelyAfterGit || !request.Arguments.Contains(action) || result.Value is not { ExitCode: 0 })
                return;
            armed = false;
            cancellation.Cancel();
        });
        var writer = new LocalGitClient(runner, new(), f.Reader, journal, new SystemClock(), bus);
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(root), stack);
        if (resume)
        {
            Assert.True((await writer.RestackAsync(plan)).Conflicted);
            await f.Write("conflict.txt", "resolved\n");
            await f.Git("add", "conflict.txt");
        }

        armed = true;
        await Assert.ThrowsAsync<StackerException>(() => resume
            ? writer.ContinueAsync(root, "true", cancellation.Token)
            : writer.RestackAsync(plan, ct: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(CommandStatus.Cancelled, reported[^1].Status);
        Assert.Equal(originalTips, await Task.WhenAll(stack.Branches.Select(branch => f.Git("rev-parse", branch))));
        Assert.All(journal.Recent(root).OrThrow(), entry => Assert.Equal(JournalPhase.RolledBack, entry.Phase));
        Assert.False((await writer.StateAsync(root)).HasOperation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_continue_without_an_acknowledgement_keeps_the_original_recovery_open(bool loseResponse)
    {
        await using var f = new GitFixture();
        var stack = await Conflicting(f);
        using var cancellation = new CancellationTokenSource();
        var root = await f.ResolvedRoot();
        var journal = f.CreateJournal();
        var oldA = await f.Git("rev-parse", "A");
        var runner = new InterceptedRunner(f.Runner, (request, token) =>
        {
            if (loseResponse || !request.Arguments.Contains("--continue"))
                return;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        }, (request, result) =>
        {
            if (!loseResponse || !request.Arguments.Contains("--continue") || result.Value is not { ExitCode: 0 })
                return;
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        });
        var writer = new LocalGitClient(runner, new(), f.Reader, journal, new SystemClock());
        var conflicted = await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(root), stack));
        Assert.True(conflicted.Conflicted);
        await f.Write("conflict.txt", "resolved\n");
        await f.Git("add", "conflict.txt");

        var outcome = await writer.ContinueAsync(root, "true", cancellation.Token);

        Assert.Equal(RestackStatus.Refused, outcome.Status);
        var pending = Assert.Single(journal.Recent(root).OrThrow(), entry => entry.NeedsAttention);
        Assert.Equal(conflicted.Journal!.Id, pending.Id);
        Assert.Equal(StepState.Started, pending.Pending?.State);
        if (loseResponse)
        {
            var rewritten = await f.Git("rev-parse", "A");
            Assert.NotEqual(oldA, rewritten);
            Assert.Equal(RestackStatus.Refused, (await writer.AbortAsync(root)).Status);
            Assert.Equal(rewritten, await f.Git("rev-parse", "A"));
        }
        else
        {
            Assert.True((await writer.StateAsync(root)).HasOperation);
            Assert.Equal("resolved", await f.Git("show", ":conflict.txt"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_applied_restack_survives_failure_after_commit(bool loseCloseResponse)
    {
        await using var f = new GitFixture();
        await f.Linear();
        await f.Git("checkout", "main");
        await f.Write("main2.txt", "moved\n");
        await f.Commit("base moved");
        var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A");
        var journal = f.CreateJournal();
        var writer = new LocalGitClient(new InterceptedRunner(f.Runner, (request, _) =>
        {
            if (!loseCloseResponse && request.Arguments.TakeLast(3).SequenceEqual(["for-each-ref", "--format=%(refname)", "refs/stacker/backup"]))
                throw new InvalidOperationException("injected backup pruning failure");
        }), new(), f.Reader, new FaultyJournal(journal, failAfterClose: loseCloseResponse ? JournalPhase.Applied : null), new SystemClock());

        var outcome = await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(root), f.Stack));

        Assert.True(outcome.Applied, outcome.Message);
        if (!loseCloseResponse)
            Assert.Contains("could not be pruned", outcome.Message);
        Assert.NotEqual(oldA, await f.Git("rev-parse", "A"));
        Assert.Equal(JournalPhase.Applied, Assert.Single(journal.Recent(root).OrThrow()).Phase);
    }

    [Theory]
    [InlineData(2, StepState.Planned, true)]
    [InlineData(3, StepState.Unknown, true)]
    [InlineData(3, StepState.Started, false)]
    public async Task A_pending_rebase_can_be_continued_and_undone_with_matching_git_state(int schema, StepState state, bool hasPendingId)
    {
        await using var f = new GitFixture();
        await Conflicting(f);
        var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A");
        var store = new JsonStateStore<JournalLog>(new AtomicFile(), f.Root + " journal");
        var now = DateTimeOffset.UtcNow;
        var pending = new JournalEntry(schema, "pending", root, "restack", now, now, hasPendingId ? JournalPhase.NeedsUser : JournalPhase.Running,
            [new("original", "original", "main", "main", null, StepState.Planned),
             new("layer:0", "rebase", "refs/heads/A", oldA, null, state)], null, hasPendingId ? "layer:0" : null, null);
        (await store.WriteAsync(root, new()
        {
            Entries = [pending]
        })).OrThrow();
        Assert.Equal(1, await f.ExitCode("rebase", "main", "A"));
        await f.Write("conflict.txt", "resolved\n");
        await f.Git("add", "conflict.txt");
        var writer = f.Writer();

        var resumed = await writer.ContinueAsync(root, "true");

        Assert.True(resumed.Applied, resumed.Message);
        Assert.Equal(schema, resumed.Journal!.SchemaVersion);
        Assert.Equal(JournalPhase.Applied, resumed.Journal.Phase);
        Assert.Equal(await f.Git("rev-parse", "A"), Assert.Single(resumed.Journal.Moved).NewSha);
        Assert.True((await writer.UndoLastAsync(root)).RolledBack);
        Assert.Equal(oldA, await f.Git("rev-parse", "A"));
    }

    [Fact]
    public async Task Aborting_a_native_operation_leaves_an_unrelated_unfinished_restack_available()
    {
        await using var f = new GitFixture();
        await f.Linear();
        var root = await f.ResolvedRoot();
        var journal = f.CreateJournal();
        var step = new JournalStep("layer:0", "rebase", "refs/heads/A", await f.Git("rev-parse", "A"), null, StepState.Planned);
        var entry = (await journal.OpenAsync(root, "restack", [step])).OrThrow();
        (await journal.StepAsync(entry, step with
        {
            State = StepState.Started
        })).OrThrow();
        await f.Git("checkout", "A");
        await f.Git("commit", "--amend", "-m", "A rewritten");
        var rewritten = await f.Git("rev-parse", "A");
        (await journal.StepAsync(entry, step.Finished(step.OldSha, rewritten, StepState.Done))).OrThrow();
        (await journal.CloseAsync(entry, JournalPhase.NeedsUser)).OrThrow();
        await f.Git("checkout", "main");
        await f.Git("merge", "--no-commit", "--no-ff", "A");
        var writer = f.Writer();

        var restore = (await ((IGitBranchWriter)writer).RestoreInterruptedRestackAsync(root, entry.Id)).OrThrow();
        Assert.Equal(RestackStatus.Refused, restore.Status);
        Assert.True((await writer.StateAsync(root)).HasOperation);
        Assert.Equal(rewritten, await f.Git("rev-parse", "A"));

        var aborted = await writer.AbortAsync(root);

        Assert.True(aborted.RolledBack, aborted.Message);
        Assert.Null(aborted.Journal);
        Assert.Equal(rewritten, await f.Git("rev-parse", "A"));
        Assert.False((await writer.StateAsync(root)).HasOperation);
        Assert.Equal(entry.Id, (await writer.ActiveJournalAsync(root))?.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_recovers_if_the_close_fails_after_refs_are_restored(bool closeWasSaved)
    {
        await using var f = new GitFixture();
        await f.Linear();
        await f.Git("checkout", "main");
        await f.Write("main2.txt", "moved\n");
        await f.Commit("base moved");
        var root = await f.ResolvedRoot();
        var original = await Task.WhenAll(f.Stack.Branches.Select(branch => f.Git("rev-parse", branch)));
        var journal = f.CreateJournal();
        var fault = new FaultyJournal(journal, failAfterClose: closeWasSaved ? JournalPhase.Undone : null,
            failBeforeClose: closeWasSaved ? null : JournalPhase.Undone);
        var writer = new LocalGitClient(f.Runner, new(), f.Reader, fault, new SystemClock());
        var restack = await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(root), f.Stack));
        Assert.True(restack.Applied, restack.Message);

        var first = await writer.UndoLastAsync(root);

        Assert.Equal(original, await Task.WhenAll(f.Stack.Branches.Select(branch => f.Git("rev-parse", branch))));
        Assert.Equal(closeWasSaved, first.RolledBack);
        if (!closeWasSaved)
        {
            Assert.Equal(restack.Journal!.Id, (await writer.ActiveJournalAsync(root))?.Id);
            Assert.True((await writer.UndoLastAsync(root)).RolledBack);
        }
        Assert.Null(await writer.ActiveJournalAsync(root));
        Assert.Equal(JournalPhase.Undone, (await journal.ByIdAsync(root, restack.Journal!.Id)).OrThrow()!.Phase);
    }

    [Fact]
    public async Task Undo_refuses_to_restore_any_ref_when_another_rewrite_was_never_recorded()
    {
        await using var f = new GitFixture();
        await f.Linear();
        var root = await f.ResolvedRoot();
        var journal = f.CreateJournal();
        var first = new JournalStep("layer:0", "rebase", "refs/heads/A", await f.Git("rev-parse", "A"), null, StepState.Planned);
        var second = new JournalStep("layer:1", "rebase", "refs/heads/B", await f.Git("rev-parse", "B"), null, StepState.Planned);
        var entry = (await journal.OpenAsync(root, "restack", [first, second])).OrThrow();
        (await journal.StepAsync(entry, first with
        {
            State = StepState.Started
        })).OrThrow();
        await f.Git("checkout", "A");
        await f.Git("commit", "--amend", "-m", "rewritten A");
        var rewrittenA = await f.Git("rev-parse", "A");
        (await journal.StepAsync(entry, first.Finished(first.OldSha, rewrittenA, StepState.Done))).OrThrow();
        (await journal.StepAsync(entry, second with
        {
            State = StepState.Started
        })).OrThrow();
        await f.Git("checkout", "B");
        await f.Git("commit", "--amend", "-m", "rewritten B");
        var rewrittenB = await f.Git("rev-parse", "B");
        var writer = f.Writer();

        var error = await Assert.ThrowsAsync<StackerException>(() => writer.UndoLastAsync(root));

        Assert.Contains("cannot prove ownership of B", error.Message);
        Assert.Equal(rewrittenA, await f.Git("rev-parse", "A"));
        Assert.Equal(rewrittenB, await f.Git("rev-parse", "B"));
        Assert.Equal(JournalPhase.Running, (await writer.ActiveJournalAsync(root))?.Phase);
        Assert.Equal(JournalPhase.Dismissed, Assert.Single(journal.Recent(root).OrThrow(), attempt => attempt.Kind == "local.undo").Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_rebase_resolution_does_not_advance_an_unrelated_restack(bool skip)
    {
        await using var f = new GitFixture();
        await Conflicting(f);
        var root = await f.ResolvedRoot();
        var journal = f.CreateJournal();
        var otherStep = new JournalStep("layer:0", "rebase", skip ? "refs/heads/A" : "refs/heads/B",
            await f.Git("rev-parse", skip ? "B" : "A"), null, StepState.Planned);
        var pending = (await journal.OpenAsync(root, "restack", [otherStep])).OrThrow();
        (await journal.StepAsync(pending, otherStep with
        {
            State = StepState.Started
        })).OrThrow();
        (await journal.CloseAsync(pending, JournalPhase.NeedsUser, pendingStepId: otherStep.Id)).OrThrow();
        Assert.Equal(1, await f.ExitCode("rebase", "main", "A"));
        if (!skip)
        {
            await f.Write("conflict.txt", "resolved\n");
            await f.Git("add", "conflict.txt");
        }
        var writer = f.Writer();

        var outcome = skip ? await writer.SkipAsync(root) : await writer.ContinueAsync(root, "true");

        Assert.True(outcome.Applied, outcome.Message);
        Assert.Null(outcome.Journal);
        var original = (await journal.ByIdAsync(root, pending.Id)).OrThrow()!;
        Assert.Equal(JournalPhase.NeedsUser, original.Phase);
        Assert.Equal(StepState.Started, original.Pending?.State);
        Assert.Equal(JournalPhase.Applied, Assert.Single(journal.Recent(root).OrThrow(), entry => entry.Kind == "local.resolve").Phase);
    }

    [Fact]
    public async Task A_rebase_onto_a_different_commit_does_not_resume_a_saved_restack()
    {
        await using var f = new GitFixture();
        var stack = await Conflicting(f);
        await f.Git("checkout", "-b", "other");
        await f.Write("conflict.txt", "other\n");
        await f.Commit("other base");
        await f.Git("checkout", "main");
        var root = await f.ResolvedRoot();
        var writer = f.Writer();
        var oldB = await f.Git("rev-parse", "B");
        var conflict = await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(root), stack));
        Assert.True(conflict.Conflicted);
        await f.Git("rebase", "--abort");
        Assert.Equal(1, await f.ExitCode("rebase", "other", "A"));
        await f.Write("conflict.txt", "resolved\n");
        await f.Git("add", "conflict.txt");

        var outcome = await writer.ContinueAsync(root, "true");

        Assert.True(outcome.Applied, outcome.Message);
        Assert.Null(outcome.Journal);
        Assert.Equal(oldB, await f.Git("rev-parse", "B"));
        Assert.Equal(conflict.Journal!.Id, (await writer.ActiveJournalAsync(root))?.Id);
    }
}
