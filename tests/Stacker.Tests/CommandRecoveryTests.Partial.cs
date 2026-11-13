using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class CommandRecoveryTests
{
    private sealed class Partial : IStackCommand
    {
        public string Kind => "test.partial";
        public string Describe() => Kind;
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready(
            [JournalStep.Planned("one", "publish", "A"), JournalStep.Planned("two", "publish", "B")]));
        public async Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default)
        {
            (await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0] with
            {
                State = StepState.Started
            }, ct)).OrThrow();
            (await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0] with
            {
                State = StepState.Done
            }, ct)).OrThrow();
            return CommandOutcome.Failed(Problem.Of(ProblemCodes.GitHubRequest, "second write failed"));
        }
    }
}
