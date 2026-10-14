// Purpose: Real-Git checks that a restack rewrites only stack branches, preserves layer content, and restores everything on failure.
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;
public sealed class RestackTests
{
    private static GitBranchWriter Writer(GitFixture f) => f.Writer();

    /// <summary>Advances main past the layers so every layer needs a rebase.</summary>
    private static async Task MoveBase(GitFixture f)
    {
        await f.Git("checkout", "main"); await f.Write("main2.txt", "moved\n"); await f.Commit("base moved");
    }

    // Scenario: A moved base rewrites every layer, keeps each layer's own change, and returns the user to their branch.
    [Fact] public async Task Restack_rebases_every_layer_and_keeps_its_content()
    {
        await using var f = new GitFixture(); await f.Linear();
        var before = new Dictionary<string, string>();
        foreach (var branch in new[] { "A", "B", "C" }) before[branch] = await f.Git("rev-parse", branch);
        await MoveBase(f);
        var writer = Writer(f);
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Blockers));
        Assert.Equal(3, plan.RebaseCount); Assert.Equal("main", plan.OriginalBranch); Assert.True(plan.HasOrigin == false);
        var outcome = await writer.RestackAsync(plan);
        Assert.True(outcome.Applied, outcome.Message); Assert.False(outcome.RolledBack); Assert.Equal(3, outcome.Steps.Count);
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
        foreach (var branch in new[] { "A", "B", "C" })
        {
            Assert.Equal(0, await f.ExitCode("merge-base", "--is-ancestor", "main", branch));
            Assert.NotEqual(before[branch], await f.Git("rev-parse", branch));
        }
        var repo = await f.Reader.OpenAsync(f.Root);
        var service = new DiffService(f.Reader);
        Assert.Equal("A.txt", Assert.Single(Assert.Single(await service.CompareAsync(new(repo, f.Stack, DiffMode.Layer, [0]))).Files).NewPath);
        Assert.Equal("C.txt", Assert.Single(Assert.Single(await service.CompareAsync(new(repo, f.Stack, DiffMode.Layer, [2]))).Files).NewPath);
        var backups = await f.Git("for-each-ref", "--format=%(refname)", "refs/stacker/backup");
        Assert.Equal(3, backups.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain(repo.Refs, reference => reference.Name.StartsWith("refs/stacker", StringComparison.Ordinal));
        var journal = await writer.LastJournalAsync(await f.ResolvedRoot());
        Assert.NotNull(journal); Assert.True(journal!.CanUndo); Assert.Equal(JournalPhase.Applied, journal.Phase);
        Assert.Equal(3, journal.Moved.Count);
    }

    // Scenario: Undo restores every branch, including the one the user has checked out.
    [Fact] public async Task Undo_restores_every_branch_even_the_checked_out_one()
    {
        await using var f = new GitFixture(); await f.Linear();
        var before = new Dictionary<string, string>();
        foreach (var branch in new[] { "A", "B", "C" }) before[branch] = await f.Git("rev-parse", branch);
        await MoveBase(f);
        var writer = Writer(f);
        Assert.True((await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack))).Applied);
        await f.Git("checkout", "B");
        await f.Write("B.txt", "edited\n");
        var root = await f.ResolvedRoot();
        Assert.Contains("uncommitted", (await Assert.ThrowsAsync<StackerException>(() => writer.UndoLastAsync(root))).Message);
        await f.Git("checkout", "--", "B.txt");
        var outcome = await writer.UndoLastAsync(root);
        Assert.True(outcome.RolledBack); Assert.Equal(3, outcome.Steps.Count);
        foreach (var branch in new[] { "A", "B", "C" }) Assert.Equal(before[branch], await f.Git("rev-parse", branch));
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
        // Undo restores the branches but keeps the backups, so the restack can still be recovered.
        Assert.NotEmpty((await f.Git("for-each-ref", "--format=%(refname)", "refs/stacker/backup")).Trim());
    }

    // Scenario: A user working on the top layer is returned there, on the rewritten commit.
    [Fact] public async Task Restack_returns_to_a_rewritten_layer_branch()
    {
        await using var f = new GitFixture(); await f.Linear(); await MoveBase(f);
        await f.Git("checkout", "C");
        var writer = Writer(f);
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.Equal("C", plan.OriginalBranch);
        var outcome = await writer.RestackAsync(plan);
        Assert.True(outcome.Applied, outcome.Message);
        Assert.Equal("C", await f.Git("branch", "--show-current"));
        Assert.Equal(await f.Git("rev-parse", "C"), await f.Git("rev-parse", "HEAD"));
        Assert.Equal("C", File.ReadAllText(Path.Combine(f.Root, "C.txt")).Trim());
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
    }

    /// <summary>Builds a two-layer stack whose first layer conflicts with the moved base.</summary>
    private static async Task<(Dictionary<string, string> Before, StackDefinition Stack)> Conflicting(GitFixture f)
    {
        await f.Init();
        await f.Git("checkout", "-b", "A"); await f.Write("conflict.txt", "A\n"); await f.Commit("A");
        await f.Git("checkout", "-b", "B"); await f.Write("B.txt", "B\n"); await f.Commit("B");
        await f.Git("checkout", "main"); await f.Write("conflict.txt", "main\n"); await f.Commit("main moved");
        var before = new Dictionary<string, string>();
        foreach (var branch in new[] { "main", "A", "B" }) before[branch] = await f.Git("rev-parse", branch);
        return (before, new("t", "T", "refs/heads/main", ["refs/heads/A", "refs/heads/B"]));
    }

    // Scenario: A conflict stops the restack, keeps the rebase, and is finished from the app after resolving.
    [Fact] public async Task A_conflicting_layer_stops_for_the_user_to_resolve()
    {
        await using var f = new GitFixture();
        var (before, stack) = await Conflicting(f);
        var writer = Writer(f);
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), stack);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Blockers));
        var outcome = await writer.RestackAsync(plan);
        Assert.True(outcome.Conflicted, outcome.Message);
        Assert.Equal("A", outcome.ConflictingBranch);
        Assert.Contains("conflict.txt", outcome.ConflictingFiles);
        // The rebase is left in progress and the conflicted layer has not moved.
        var root = await f.ResolvedRoot();
        Assert.Equal(before["A"], await f.Git("rev-parse", "A"));
        var state = await writer.StateAsync(root);
        Assert.Equal(WorkingOperation.Rebase, state.Operation);
        Assert.True(state.CanContinue); Assert.True(state.HasConflicts);
        Assert.Contains("conflict.txt", state.ConflictedFiles);
        var journal = await writer.ActiveJournalAsync(root);
        Assert.NotNull(journal); Assert.True(journal!.NeedsAttention); Assert.Equal(JournalPhase.NeedsUser, journal.Phase);
        // The layer the user has to resolve is recorded as pending, so an undo after resolving reverses it too.
        Assert.NotNull(journal.Pending); Assert.Equal("refs/heads/A", journal.Pending!.Target);
        // Resolving in the working tree, staging it and continuing finishes the layer.
        await f.Write("conflict.txt", "resolved\n");
        Assert.Contains("conflict.txt", await writer.StageAsync(root, state.ConflictedFiles));
        var resumed = await writer.ContinueAsync(root, "true");
        Assert.True(resumed.Applied, resumed.Message);
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
        Assert.Equal(0, await f.ExitCode("merge-base", "--is-ancestor", "main", "A"));
        // The resolved content lives on the layer; the working tree is back on the branch the user started from.
        Assert.Equal("resolved", (await f.Git("show", "A:conflict.txt")).Trim());
        Assert.Equal("main", File.ReadAllText(Path.Combine(f.Root, "conflict.txt")).Trim());
        Assert.False((await writer.StateAsync(root)).HasOperation);
    }

    // Scenario: Aborting a conflicted restack restores every branch the interrupted run moved.
    [Fact] public async Task Aborting_a_conflicted_restack_restores_every_branch()
    {
        await using var f = new GitFixture();
        var (before, stack) = await Conflicting(f);
        var writer = Writer(f);
        var outcome = await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), stack));
        Assert.True(outcome.Conflicted, outcome.Message);
        var aborted = await writer.AbortAsync(await f.ResolvedRoot());
        Assert.True(aborted.RolledBack, aborted.Message);
        foreach (var branch in new[] { "main", "A", "B" }) Assert.Equal(before[branch], await f.Git("rev-parse", branch));
        Assert.Equal("main", await f.Git("branch", "--show-current"));
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
        Assert.False(Directory.Exists(Path.Combine(f.Root, ".git", "rebase-merge")));
        Assert.False(Directory.Exists(Path.Combine(f.Root, ".git", "rebase-apply")));
        // A rollback is recorded for diagnostics but is never offered as an undo.
        Assert.Null(await writer.LastJournalAsync(await f.ResolvedRoot()));
    }

    // Scenario: Skipping the conflicting commit drops it and keeps the rest of the stack.
    [Fact] public async Task Skipping_the_conflicting_commit_resumes_the_rebase()
    {
        await using var f = new GitFixture();
        var (_, stack) = await Conflicting(f);
        var writer = Writer(f);
        Assert.True((await writer.RestackAsync(await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), stack))).Conflicted);
        var skipped = await writer.SkipAsync(await f.ResolvedRoot());
        Assert.True(skipped.Applied, skipped.Message);
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
        Assert.Equal("main", File.ReadAllText(Path.Combine(f.Root, "conflict.txt")).Trim());
        Assert.Equal(0, await f.ExitCode("merge-base", "--is-ancestor", "main", "A"));
    }

    // Scenario: A layer with no commits of its own follows its parent instead of being left behind.
    [Fact] public async Task An_empty_layer_follows_its_parent()
    {
        await using var f = new GitFixture(); await f.Init();
        await f.Git("checkout", "-b", "A"); await f.Write("A.txt", "A\n"); await f.Commit("A");
        await f.Git("branch", "empty");
        var stack = new StackDefinition("t", "T", "refs/heads/main", ["refs/heads/A", "refs/heads/empty"]);
        await MoveBase(f);
        var writer = Writer(f);
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), stack);
        Assert.True(plan.Steps[1].NeedsRebase); Assert.Equal(0, plan.Steps[1].CommitCount); Assert.NotNull(plan.Steps[1].Note);
        Assert.True((await writer.RestackAsync(plan)).Applied);
        Assert.Equal(await f.Git("rev-parse", "A"), await f.Git("rev-parse", "empty"));
    }

    // Scenario: The generated demo is a real playground: its own workspace files never block a restack.
    [Fact] public async Task Demo_repository_can_be_restacked()
    {
        await using var f = new GitFixture();
        var applicationStore = new ApplicationStore(f.Root + " app");
        var root = await new DemoRepositoryGenerator(f.Runner, new GitExecutable(), new YamlStackStore(), applicationStore).CreateAsync();
        var writer = new GitBranchWriter(f.Runner, new GitExecutable(), f.Reader, new JsonJournal(new JsonStateStore<JournalLog>(new AtomicFile(), root + " journal"), new SystemClock()), new SystemClock());
        await f.GitAt(root, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(root, "later.txt"), "later\n");
        await f.GitAt(root, "add", "--all");
        await f.GitAt(root, "commit", "-m", "base moved");
        var stack = (await new YamlStackStore().LoadAsync(root)).Stacks.Single(s => s.Id == "authorization");
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(root), stack);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Blockers));
        Assert.Equal(3, plan.RebaseCount);
        Assert.True((await writer.RestackAsync(plan)).Applied);
        Assert.Empty((await f.GitAt(root, "status", "--porcelain")).Trim());
    }

    // Scenario: The subject of a branch's newest commit is offered as a pull-request title.
    [Fact] public async Task The_newest_commit_subject_is_available_for_a_pull_request_title()
    {
        await using var f = new GitFixture(); await f.Linear();
        var writer = Writer(f);
        Assert.Equal("B", await writer.LastCommitSubjectAsync(f.Root, "refs/heads/B"));
        Assert.Null(await writer.LastCommitSubjectAsync(f.Root, "refs/heads/missing"));
    }

    // Scenario: Every write is visible in the Git command log the diagnostics tab shows.
    [Fact] public async Task Restack_commands_appear_in_the_git_log()
    {
        await using var f = new GitFixture(); await f.Linear(); await MoveBase(f);
        var log = new GitCommandLog();
        var writer = new GitBranchWriter(new ProcessRunner(log), new GitExecutable(), f.Reader, f.CreateJournal(), new SystemClock());
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.True((await writer.RestackAsync(plan)).Applied);
        Assert.Contains(log.Snapshot(), entry => entry.Command.Contains("rebase --onto", StringComparison.Ordinal));
        Assert.Contains(log.Snapshot(), entry => entry.Command.Contains("update-ref refs/stacker/backup", StringComparison.Ordinal));
        Assert.All(log.Snapshot(), entry => Assert.False(entry.IsRunning));
    }

    // Scenario: An untracked scratch file no longer blocks a restack, while a tracked edit still does.
    [Fact] public async Task Untracked_files_are_allowed_but_tracked_changes_block()
    {
        await using var f = new GitFixture(); await f.Linear(); await MoveBase(f);
        var writer = Writer(f);
        await f.Write("scratch.txt", "notes\n");
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Blockers));
        Assert.True((await writer.RestackAsync(plan)).Applied);
        // A change to a tracked file is a different matter, and it is what the stash is for.
        await f.Write("base.txt", "edited\n");
        var blocked = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.False(blocked.CanExecute); Assert.Contains(blocked.Blockers, blocker => blocker.Contains("uncommitted"));
        var stash = await writer.StashAsync(f.Root, "Stacker: test");
        Assert.NotNull(stash);
        // Stashing takes the tracked change; the untracked scratch file stays where it is.
        Assert.False((await writer.StateAsync(f.Root)).HasTrackedChanges);
        var restored = await writer.RestoreStashAsync(f.Root);
        Assert.Equal("edited", File.ReadAllText(Path.Combine(f.Root, "base.txt")).Trim());
        Assert.False(restored.HasConflicts);
    }

    // Scenario: A stale preview or an unsafe repository state is refused before anything moves.
    [Fact] public async Task Dirty_tree_and_an_unfinished_rebase_refuse_to_plan()
    {
        await using var f = new GitFixture(); await f.Linear();
        var writer = Writer(f);
        await f.Write("base.txt", "edited\n");
        var dirty = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.False(dirty.CanExecute); Assert.Contains(dirty.Blockers, blocker => blocker.Contains("uncommitted"));
        Assert.Contains("uncommitted", (await writer.RestackAsync(dirty)).Message);
        await f.Git("checkout", "--", "base.txt");
        await f.Git("checkout", "A"); await f.Write("conflict.txt", "A\n"); await f.Commit("A");
        await f.Git("checkout", "main"); await f.Write("conflict.txt", "main\n"); await f.Commit("main");
        Assert.NotEqual(0, await f.ExitCode("rebase", "A"));
        var blocked = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.False(blocked.CanExecute); Assert.Contains(blocked.Blockers, blocker => blocker.Contains("in progress"));
        Assert.Equal(0, await f.ExitCode("rebase", "--abort"));
    }
}
