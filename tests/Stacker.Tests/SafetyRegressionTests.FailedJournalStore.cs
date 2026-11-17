using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;

namespace Stacker.Tests;

public sealed partial class SafetyRegressionTests
{
    private sealed class FailedJournalStore : IStateStore<JournalLog>
    {
        public Result<JournalLog?> Read(string identity) => Result<JournalLog?>.Ok(null);
        public Task<Result<Unit>> WriteAsync(string identity, JournalLog value, CancellationToken ct = default) =>
            Task.FromResult(Result<Unit>.Fail(ProblemCodes.StoreWrite, "injected disk full"));
        public Result<string> Quarantine(string identity) => Result<string>.Fail(ProblemCodes.StoreWrite, "disk full");
    }
}
