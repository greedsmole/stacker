using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

public sealed partial class WorkingTreeAuthoring
{
    private sealed class AuthoringCommand<T>(string kind, string target, string description, Func<CancellationToken, Task<Result<T>>> execute)
        : EffectCommand<T>(kind, target, (_, token) => execute(token))
    {
        public override string Describe() => description;
    }
}
