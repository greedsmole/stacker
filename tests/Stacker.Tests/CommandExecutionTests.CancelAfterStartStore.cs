using Stacker.Core;
using Stacker.Core.Features.LocalGit;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;

namespace Stacker.Tests;

public sealed partial class CommandExecutionTests
{
    private sealed class CancelAfterStartStore(IStateStore<JournalLog> inner, CancellationTokenSource cancellation) : IStateStore<JournalLog>
    {
        public Result<JournalLog?> Read(string identity) => inner.Read(identity);
        public Result<string> Quarantine(string identity) => inner.Quarantine(identity);
        public async Task<Result<Unit>> WriteAsync(string identity, JournalLog value, CancellationToken ct = default)
        {
            var result = await inner.WriteAsync(identity, value, ct);
            if (result.IsOk && value.Entries.Any(entry => entry.Phase == JournalPhase.Running && entry.Steps.Any(step => step.State == StepState.Started)))
                cancellation.Cancel();
            return result;
        }
    }
}
