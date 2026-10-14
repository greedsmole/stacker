// Purpose: Contracts for results, values and the command runner that every effect must go through.
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Tests;
public sealed class FrameworkTests
{
    // Scenario: A result carries either a value or a problem, and never both.
    [Fact] public void A_result_carries_either_a_value_or_a_problem()
    {
        var ok = Result<int>.Ok(41);
        Assert.True(ok.IsOk); Assert.Equal(41, ok.Value); Assert.Null(ok.Problem);
        Assert.Equal(42, ok.Map(value => value + 1).Value);
        var failed = Result<int>.Fail(ProblemCodes.GitFailed, "no", "detail");
        Assert.True(failed.IsFailed); Assert.Equal(0, failed.Value);
        Assert.Equal("git.failed", failed.Problem!.Code); Assert.Equal("detail", Assert.Single(failed.Problem.Details));
        Assert.Contains("no", failed.OrThrowExceptionMessage());
        // A failure passes through Bind untouched; a success adopts the next step's failure.
        Assert.Equal(ProblemCodes.GitFailed, failed.Bind(_ => Result<Unit>.Ok(Unit.Value)).Problem!.Code);
        Assert.Equal(ProblemCodes.StoreRead, Result<int>.Ok(1).Bind(_ => Result<Unit>.Fail(ProblemCodes.StoreRead, "x")).Problem!.Code);
    }

    // Scenario: Only local branches may be rewritten; a remote-tracking ref is refused with an actionable message.
    [Fact] public void Only_local_branches_may_be_rewritten()
    {
        var local = StackBranchRef.Parse("refs/heads/feature/x");
        Assert.True(local.IsOk); Assert.True(local.Value.IsLocal); Assert.Equal("feature/x", local.Value.Name);
        Assert.True(local.Value.RequireLocal().IsOk);
        var remote = StackBranchRef.Parse("refs/remotes/origin/main");
        Assert.True(remote.IsOk); Assert.False(remote.Value.IsLocal);
        Assert.Equal("main", remote.Value.Name);
        Assert.Equal(ProblemCodes.GitNotLocalBranch, remote.Value.RequireLocal().Problem!.Code);
        Assert.True(LocalBranch.Parse("main").IsFailed);
        Assert.True(CommitSha.Parse("abc123").IsFailed);
        Assert.True(CommitSha.Parse(new string('a', 40)).IsOk);
        Assert.True(CommitMessage.Parse("  ").IsFailed);
        Assert.Equal("done", CommitMessage.Parse("  done  ").Value.Value);
    }

    // Scenario: An editor command is split without a shell and quoted for git, so a path with spaces survives both uses.
    [Fact] public void An_editor_command_survives_both_uses()
    {
        var plain = ShellCommand.Parse("code --wait");
        Assert.Equal("code", plain.Value.Program); Assert.Equal("--wait", Assert.Single(plain.Value.Arguments));
        Assert.Equal("code --wait", plain.Value.ForGitEditor());
        var spaced = ShellCommand.Parse("\"/Applications/My Editor/bin/edit\" -w");
        Assert.Equal("/Applications/My Editor/bin/edit", spaced.Value.Program);
        Assert.Equal("\"/Applications/My Editor/bin/edit\" -w", spaced.Value.ForGitEditor());
        Assert.Equal(["-w", "file one.cs"], spaced.Value.ArgumentsFor(["file one.cs"]));
        Assert.Equal(ProblemCodes.EditorMissing, ShellCommand.Parse("  ").Problem!.Code);
    }

    // Scenario: A persisted publication marker round-trips, and a damaged one is refused instead of throwing.
    [Fact] public void A_publication_marker_round_trips_and_a_damaged_one_is_refused()
    {
        var expected = new[] { ("comment", PendingKind.Comment), ("file", PendingKind.FileComment), ("inline", PendingKind.Inline), ("reply", PendingKind.Reply), ("review", PendingKind.Review) };
        foreach (var (wire, kind) in expected)
        {
            var encoded = PendingOperation.For(wire, "marker-1").Encode();
            Assert.Equal($"{wire}|marker-1", encoded);
            Assert.True(PendingOperation.TryParse(encoded, out var parsed));
            Assert.Equal(kind, parsed.Kind); Assert.Equal("marker-1", parsed.Marker);
        }
        var resolve = PendingOperation.ForResolve("thread-9", true).Encode();
        Assert.True(PendingOperation.TryParse(resolve, out var parsedResolve));
        Assert.Equal(PendingKind.Resolve, parsedResolve.Kind);
        Assert.Equal("thread-9", parsedResolve.ThreadId); Assert.True(parsedResolve.Resolved);
        Assert.False(PendingOperation.TryParse("resolve|thread-9|maybe", out _));
        Assert.False(PendingOperation.TryParse("garbage", out _));
        Assert.False(PendingOperation.TryParse("nonsense|marker", out _));
        Assert.False(PendingOperation.TryParse("", out _));
    }

    private sealed class RecordingJournal : IJournal
    {
        public List<string> Calls { get; } = [];
        public JournalEntry? Last { get; private set; }
        public Task<JournalEntry> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default)
        { Calls.Add("open"); Last = new(JournalEntry.CurrentSchema, "e1", repository, kind, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, JournalPhase.Running, planned, null, null, null); return Task.FromResult(Last); }
        public Task<JournalEntry> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default)
        { Calls.Add("step"); Last = entry with { Steps = entry.Steps.Select(existing => existing.Id == step.Id ? step : existing).ToArray() }; return Task.FromResult(Last!); }
        public Task<JournalEntry> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default)
        { Calls.Add("close:" + phase); Last = entry with { Phase = phase, StashRef = stashRef ?? entry.StashRef, PendingStepId = pendingStepId, Message = message }; return Task.FromResult(Last!); }
        public Task<JournalEntry> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default)
        { Calls.Add("stash"); Last = entry with { StashRef = stashRef }; return Task.FromResult(Last!); }
        public Task<JournalEntry?> ActiveAsync(string repository, CancellationToken ct = default) => Task.FromResult(Last);
        public Task<JournalEntry?> UndoableAsync(string repository, CancellationToken ct = default) => Task.FromResult(Last);
        public Task<JournalEntry?> ByIdAsync(string repository, string id, CancellationToken ct = default) => Task.FromResult(Last);
        public IReadOnlyList<JournalEntry> Recent(string repository, int count = 20) => Last is null ? [] : [Last];
    }

    private sealed class RefusingCommand : IStackCommand
    {
        public string Kind => "test.refuse";
        public string Describe() => "Refuses to run";
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Blocked("the working tree is dirty"));
        public Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default) => throw new InvalidOperationException("must not run");
    }

    private sealed class FailingCommand : IUndoableCommand
    {
        public bool Undone { get; private set; }
        public string Kind => "test.fail";
        public string Describe() => "Moves a branch, then fails";
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready([JournalStep.Planned("s1", "move", "refs/heads/A")]));
        public async Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default)
        {
            await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0].Finished(new string('a', 40), new string('b', 40), StepState.Done), ct);
            throw new InvalidOperationException("boom");
        }
        public Task<CommandOutcome> UndoAsync(JournalEntry entry, CancellationToken ct = default)
        { Undone = true; return Task.FromResult(CommandOutcome.Restored($"restored {entry.Moved.Count} branch(es)")); }
    }

    private sealed class ConflictingCommand : IStackCommand
    {
        public string Kind => "test.conflict";
        public string Describe() => "Stops for the user";
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready([JournalStep.Planned("s1", "rebase", "refs/heads/A")]));
        public Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default) => Task.FromResult(CommandOutcome.Attention("resolve conflict.txt, then continue"));
    }

    // Scenario: A blocked plan never opens a journal entry and never runs.
    [Fact] public async Task A_blocked_plan_never_opens_a_journal_entry()
    {
        var journal = new RecordingJournal();
        var outcome = await new CommandRunner(journal).RunAsync("/repo", new RefusingCommand());
        Assert.False(outcome.Ok); Assert.Contains("dirty", outcome.Message);
        Assert.Empty(journal.Calls);
    }

    // Scenario: A failed command is journalled before the effect, then reversed and closed as rolled back.
    [Fact] public async Task A_failed_command_is_journalled_then_reversed()
    {
        var journal = new RecordingJournal();
        var command = new FailingCommand();
        var outcome = await new CommandRunner(journal).RunAsync("/repo", command);
        Assert.True(outcome.RolledBack); Assert.True(command.Undone);
        Assert.Contains("restored 1 branch(es)", outcome.Message);
        Assert.Equal(["open", "step", "close:RolledBack"], journal.Calls);
        Assert.Equal(JournalPhase.RolledBack, journal.Last!.Phase);
    }

    // Scenario: A command that stops for the user leaves an entry that still needs attention.
    [Fact] public async Task A_command_that_stops_for_the_user_keeps_its_entry_open()
    {
        var journal = new RecordingJournal();
        var outcome = await new CommandRunner(journal).RunAsync("/repo", new ConflictingCommand());
        Assert.True(outcome.NeedsUser); Assert.False(outcome.Ok);
        Assert.Equal(["open", "close:NeedsUser"], journal.Calls);
        Assert.True(journal.Last!.NeedsAttention);
    }
}

internal static class ResultTestExtensions
{
    /// <summary>Test helper: the message a failing OrThrow would produce.</summary>
    public static string OrThrowExceptionMessage<T>(this Result<T> result)
    {
        try { result.OrThrow(); return ""; }
        catch (StackerException ex) { return ex.Message; }
    }
}
