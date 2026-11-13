using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

internal sealed class RepositoryRecordingRunner(IProcessRunner inner) : IProcessRunner
{
    public List<ProcessRequest> Calls { get; } = [];
    public Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        lock (Calls)
            Calls.Add(request);
        return inner.RunAsync(request, ct);
    }
}
