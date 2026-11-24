using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

public sealed partial class GhStackClient
{
    private sealed class GhStackCommand(string kind, string description, IReadOnlyList<JournalStep> steps,
        Func<CancellationToken, Task> validate, Func<CommandContext, CancellationToken, Task<CommandOutcome>> execute) : IStackCommand
    {
        public string Kind => kind;
        public string Describe() => description;
        public async Task<CommandPlan> PlanAsync(CancellationToken ct = default)
        {
            try { await validate(ct); }
            catch (StackerException ex) { return CommandPlan.Blocked(ex.Message); }
            return CommandPlan.Ready(steps);
        }

        public Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default) => execute(context, ct);
    }
}
