using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Tests;

public sealed partial class FrameworkTests
{
    private sealed class ConflictingCommand : IStackCommand
    {
        public string Kind => "test.conflict";
        public string Describe() => "Stops for the user";
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready([JournalStep.Planned("s1", "rebase", "refs/heads/A")]));
        public Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default) => Task.FromResult(CommandOutcome.Attention("resolve conflict.txt, then continue"));
    }
}
