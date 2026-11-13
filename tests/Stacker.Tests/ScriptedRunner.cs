using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class ScriptedRunner(Func<ProcessRequest, ProcessResult> handler) : IProcessRunner
{
    public List<ProcessRequest> Calls { get; } = [];
    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(request);
        return Task.FromResult(handler(request));
    }

    Task<Result<ProcessResult>> IProcessRunner.RunAsync(ProcessRequest request, CancellationToken ct) => Boundary.CaptureAsync(() => RunAsync(request, ct), ProblemCodes.GitFailed);
}
