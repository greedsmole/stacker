using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class CommandRecoveryTests
{
    private sealed class Recoverable(bool cancel) : IUndoableCommand
    {
        public string Kind => "test.recoverable";
        public bool Restored
        {
            get; private set;
        }
        public string Describe() => Kind;
        public Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready());
        public Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default) => cancel
            ? Task.FromException<CommandOutcome>(new OperationCanceledException(ct))
            : Task.FromResult(CommandOutcome.Failed(Problem.Of(ProblemCodes.GitFailed, "returned failure")));
        public Task<CommandOutcome> UndoAsync(JournalEntry entry, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Restored = true;
            return Task.FromResult(CommandOutcome.Restored("restored"));
        }
    }
}
