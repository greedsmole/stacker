using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;

public sealed partial class FoundationWorkflowTests
{
    private sealed class FailingDoneJournalState(IStateStore<JournalLog> inner) : IStateStore<JournalLog>
    {
        public Result<string> Quarantine(string identity) => inner.Quarantine(identity);
        public Result<JournalLog?> Read(string identity) => inner.Read(identity);
        public Task<Result<Unit>> WriteAsync(string identity, JournalLog value, CancellationToken ct = default) =>
            value.Entries.Any(entry => entry.Steps.Any(step => step.State == StepState.Done))
                ? Task.FromResult(Result<Unit>.Fail(ProblemCodes.StoreWrite, "injected execution record failure"))
                : inner.WriteAsync(identity, value, ct);
    }
}
