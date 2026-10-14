// Purpose: BDD specs for the write path: an interrupted effect is recoverable, a resolved conflict stays undoable, a stash never hides undo.
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;
public sealed class RecoveryTests
{
    /// <summary>A journal that fails one step, standing in for any error between an effect and its record.</summary>
    private sealed class FaultyJournal(IJournal inner, int failOnStep) : IJournal
    {
        private int _steps;
        public Task<JournalEntry> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default) => inner.OpenAsync(repository, kind, planned, ct);
        public Task<JournalEntry> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default)
        {
            _steps++;
            if (_steps == failOnStep) throw new InvalidOperationException("injected journal failure");
            return inner.StepAsync(entry, step, ct);
        }
        public Task<JournalEntry> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default)
            => inner.CloseAsync(entry, phase, stashRef, pendingStepId, message, ct);
        public Task<JournalEntry> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default) => inner.SetStashAsync(entry, stashRef, ct);
        public Task<JournalEntry?> ActiveAsync(string repository, CancellationToken ct = default) => inner.ActiveAsync(repository, ct);
        public Task<JournalEntry?> UndoableAsync(string repository, CancellationToken ct = default) => inner.UndoableAsync(repository, ct);
        public Task<JournalEntry?> ByIdAsync(string repository, string id, CancellationToken ct = default) => inner.ByIdAsync(repository, id, ct);
        public IReadOnlyList<JournalEntry> Recent(string repository, int count = 20) => inner.Recent(repository, count);
    }

    /// <summary>main ← A ← B where main and A both touch conflict.txt, so A conflicts on the replay.</summary>
    private static async Task<StackDefinition> Conflicting(GitFixture f)
    {
        await f.Init();
        await f.Git("checkout", "-b", "A"); await f.Write("conflict.txt", "A\n"); await f.Commit("A");
        await f.Git("checkout", "-b", "B"); await f.Write("B.txt", "B\n"); await f.Commit("B");
        await f.Git("checkout", "main"); await f.Write("conflict.txt", "main\n"); await f.Commit("main moved");
        return new("t", "T", "refs/heads/main", ["refs/heads/A", "refs/heads/B"]);
    }

    // Given a cascade that conflicted, when the user resolves and continues, then undo restores every touched branch.
    [Fact] public async Task A_resolved_conflict_is_still_fully_undoable()
    {
        await using var f = new GitFixture();
        var stack = await Conflicting(f);
        var writer = f.Writer();
        var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A"); var oldB = await f.Git("rev-parse", "B");
        var outcome = await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), stack));
        Assert.True(outcome.Conflicted, outcome.Message);
        await f.Write("conflict.txt", "resolved\n");
        await writer.StageAsync(root, (await writer.StateAsync(root)).ConflictedFiles);
        var resumed = await writer.ContinueAsync(root, "true");
        Assert.True(resumed.Applied, resumed.Message);
        // The layer the user resolved is rewritten and recorded, so undo can reverse it.
        Assert.NotEqual(oldA, await f.Git("rev-parse", "A"));
        // The layer above it still waits for the next restack, exactly as the panel says.
        Assert.Equal(oldB, await f.Git("rev-parse", "B"));
        var entry = await writer.LastJournalAsync(root);
        Assert.NotNull(entry);
        Assert.Contains(entry!.Moved, step => step.Target == "refs/heads/A");
        var undone = await writer.UndoLastAsync(root);
        Assert.True(undone.RolledBack, undone.Message);
        Assert.Equal(oldA, await f.Git("rev-parse", "A"));
        Assert.Equal(oldB, await f.Git("rev-parse", "B"));
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.False((await writer.StateAsync(root)).HasOperation);
    }

    // Given a restack was applied, when a stash is taken afterwards, then undo is still offered and still works.
    [Fact] public async Task A_stash_never_hides_undo()
    {
        await using var f = new GitFixture(); await f.Linear();
        await f.Git("checkout", "main"); await f.Write("main2.txt", "moved\n"); await f.Commit("base moved");
        var writer = f.Writer();
        var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A");
        Assert.True((await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack))).Applied);
        await f.Write("base.txt", "work in progress\n");
        var stash = await writer.StashAsync(root, "Stacker: before the next restack");
        Assert.NotNull(stash);
        var entry = await writer.LastJournalAsync(root);
        Assert.NotNull(entry); Assert.True(entry!.CanUndo); Assert.Equal(stash, entry.StashRef);
        var undone = await writer.UndoLastAsync(root);
        Assert.True(undone.RolledBack, undone.Message);
        Assert.Equal(oldA, await f.Git("rev-parse", "A"));
        // The stash is still recorded on the entry, so it can be restored afterwards.
        Assert.Equal(stash, (await writer.ActiveJournalAsync(root))?.StashRef);
    }

    // Given an effect fails after the first layer moved, when the restack returns, then every branch is restored.
    [Fact] public async Task A_failure_mid_cascade_restores_every_branch()
    {
        await using var f = new GitFixture(); await f.Linear();
        var journal = f.CreateJournal();
        var writer = new GitBranchWriter(f.Runner, new GitExecutable(), f.Reader, new FaultyJournal(journal, failOnStep: 2), new SystemClock());
        await f.Git("checkout", "main"); await f.Write("main2.txt", "moved\n"); await f.Commit("base moved");
        var before = new Dictionary<string, string>();
        foreach (var branch in new[] { "A", "B", "C" }) before[branch] = await f.Git("rev-parse", branch);
        var outcome = await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack));
        Assert.True(outcome.RolledBack, outcome.Message);
        foreach (var branch in new[] { "A", "B", "C" }) Assert.Equal(before[branch], await f.Git("rev-parse", branch));
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
        var root = await f.ResolvedRoot();
        Assert.Null(await journal.UndoableAsync(root));
        Assert.Equal(JournalPhase.RolledBack, journal.Recent(root)[0].Phase);
    }

    // Given a crash left an entry running with one branch already moved, when recovery runs, then that branch is restored.
    [Fact] public async Task A_running_entry_from_a_crash_is_recoverable()
    {
        await using var f = new GitFixture(); await f.Linear();
        var journal = f.CreateJournal();
        var writer = new GitBranchWriter(f.Runner, new GitExecutable(), f.Reader, journal, new SystemClock());
        var root = await f.ResolvedRoot();
        var oldA = await f.Git("rev-parse", "A");
        // What a killed process leaves behind: the entry opened, the first effect done, nothing closed.
        var entry = await journal.OpenAsync(root, "restack",
            [new JournalStep("original", "original", "main", "main", null, StepState.Planned),
             new JournalStep("layer:0", "rebase", "refs/heads/A", oldA, null, StepState.Planned)]);
        await f.Git("checkout", "A"); await f.Git("commit", "--amend", "-m", "A rewritten");
        var moved = await f.Git("rev-parse", "A");
        await journal.StepAsync(entry, entry.Steps[1].Finished(oldA, moved, StepState.Done));
        Assert.NotEqual(oldA, moved);
        var active = await writer.ActiveJournalAsync(root);
        Assert.NotNull(active); Assert.True(active!.NeedsAttention);
        var aborted = await writer.AbortAsync(root);
        Assert.True(aborted.RolledBack, aborted.Message);
        Assert.Equal(oldA, await f.Git("rev-parse", "A"));
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.Null(await writer.LastJournalAsync(root));
    }
}
