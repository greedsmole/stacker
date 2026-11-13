using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Tests;

public sealed partial class FrameworkTests
{
    private sealed class RefusingCommand : IStackCommand
    {
        public string Kind => "test.refuse";
        public string Describe() => "Refuses to run";
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Blocked("the working tree is dirty"));
        public Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default) => throw new InvalidOperationException("must not run");
    }
}
