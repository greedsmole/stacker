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
    private sealed class FailingConfirmationStore(IApplicationState inner) : IApplicationState
    {
        public bool FailConfirmation { get; set; } = true;
        public string? ConfirmedId
        {
            get; private set;
        }
        public string Root => inner.Root;
        public string? LastProblem => inner.LastProblem;
        public Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class =>
            inner.ReadAsync<T>(kind, identity, ct);

        public Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class
        {
            if (value is ReviewDraft draft && draft.PublicationHistory.LastOrDefault() is { Phase: PublicationPhase.Confirmed } confirmed)
            {
                if (FailConfirmation)
                    return Task.FromResult(Result<Unit>.Fail(ProblemCodes.StoreWrite, "injected local save failure"));
                ConfirmedId = confirmed.Id;
            }
            return inner.WriteAsync(kind, identity, value, ct);
        }
    }
}
