using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class CommandRecoveryTests
{
    private sealed class Recoverable(bool cancel) : IUndoableCommand
    {
        public string Kind => "test.recoverable";
        public bool Restored { get; private set; }
        public string Describe() => Kind;
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready());
        public Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default) => cancel
            ? Task.FromException<CommandOutcome>(new OperationCanceledException(ct))
            : Task.FromResult(CommandOutcome.Failed(Problem.Of(ProblemCodes.GitFailed, "returned failure")));
        public Task<CommandOutcome> UndoAsync(JournalEntry entry, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Restored = true;
            return Task.FromResult(CommandOutcome.Restored("restored"));
        }
    }

    private sealed class Partial : IStackCommand
    {
        public string Kind => "test.partial";
        public string Describe() => Kind;
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready(
            [JournalStep.Planned("one", "publish", "A"), JournalStep.Planned("two", "publish", "B")]));
        public async Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default)
        {
            (await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0] with { State = StepState.Done }, ct)).OrThrow();
            return CommandOutcome.Failed(Problem.Of(ProblemCodes.GitHubRequest, "second write failed"));
        }
    }

    [Fact] public async Task A_nonreversible_partial_failure_reports_completed_effects_without_claiming_rollback()
    {
        await using var f = new GitFixture(); var journal = f.CreateJournal();
        var result = await new CommandRunner(journal).RunAsync(f.Root, new Partial());
        Assert.Equal(CommandStatus.PartiallyApplied, result.Status);
        Assert.Equal(new[] { "publish: A" }, result.Effects);
        Assert.True(result.NeedsUser); Assert.False(result.RolledBack);
        Assert.Equal(JournalPhase.NeedsUser, journal.Recent(f.Root).OrThrow()[0].Phase);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Returned_failures_and_cancellation_both_compensate_with_a_live_cleanup_token(bool cancel)
    {
        await using var f = new GitFixture(); var journal = f.CreateJournal();
        var command = new Recoverable(cancel);
        var result = await new CommandRunner(journal).RunAsync(f.Root, command);
        Assert.True(command.Restored);
        Assert.Equal(cancel ? CommandStatus.Cancelled : CommandStatus.RolledBack, result.Status);
        Assert.Equal(JournalPhase.RolledBack, journal.Recent(f.Root).OrThrow()[0].Phase);
    }
}
