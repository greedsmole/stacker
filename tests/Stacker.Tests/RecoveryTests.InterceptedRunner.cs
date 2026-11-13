using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Tests;

public sealed partial class RecoveryTests
{
    private sealed class InterceptedRunner(IProcessRunner inner, Action<ProcessRequest, CancellationToken> intercept,
        Action<ProcessRequest, Result<ProcessResult>>? after = null) : IProcessRunner
    {
        public async Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            intercept(request, ct);
            var result = await inner.RunAsync(request, ct);
            after?.Invoke(request, result);
            return result;
        }
    }
}
