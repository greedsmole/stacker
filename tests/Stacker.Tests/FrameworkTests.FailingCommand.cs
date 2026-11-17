using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Tests;

public sealed partial class FrameworkTests
{
    private sealed class FailingCommand : IUndoableCommand
    {
        public bool Undone
        {
            get; private set;
        }
        public string Kind => "test.fail";
        public string Describe() => "Moves a branch, then fails";
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready([JournalStep.Planned("s1", "move", "refs/heads/A")]));
        public async Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default)
        {
            (await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0].Finished(new string('a', 40), new string('b', 40), StepState.Done), ct)).OrThrow();
            throw new InvalidOperationException("boom");
        }
        public Task<CommandOutcome> UndoAsync(JournalEntry entry, CancellationToken ct = default)
        {
            Undone = true;
            return Task.FromResult(CommandOutcome.Restored($"restored {entry.Moved.Count} branch(es)"));
        }
    }
}
