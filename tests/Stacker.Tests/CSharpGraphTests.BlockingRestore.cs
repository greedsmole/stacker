using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Tests;

public sealed partial class CSharpGraphTests
{
    private sealed class BlockingRestore : IProcessRunner
    {
        public TaskCompletionSource<string> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            Entered.SetResult(request.WorkingDirectory!);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result<ProcessResult>.Ok(new(0, "", ""));
        }
    }
}
