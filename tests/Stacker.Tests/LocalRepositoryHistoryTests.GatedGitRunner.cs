using System.IO.Compression;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class LocalRepositoryHistoryTests
{
    private sealed class GatedGitRunner(IProcessRunner inner) : IProcessRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            if (request.Arguments.Contains("--patch"))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await inner.RunAsync(request, ct);
        }
    }
}
