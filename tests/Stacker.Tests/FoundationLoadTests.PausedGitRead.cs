using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Tests;

public sealed partial class FoundationLoadTests
{
    private sealed class PausedGitRead(IProcessRunner inner, Func<ProcessRequest, bool> matches) : IProcessRunner
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Resume() => _resume.TrySetResult();

        public async Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            if (matches(request) && Started.TrySetResult())
                await _resume.Task.WaitAsync(ct);
            return await inner.RunAsync(request, ct);
        }
    }
}
