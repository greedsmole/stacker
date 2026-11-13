using Stacker.Core.Framework;
namespace Stacker.Core.Features.Review;

/// <summary>Durable command for resolving or reopening one review thread.</summary>
/// <remarks>
/// The thread node id is the whole target: the mutation addresses the thread directly, and the desired state is captured
/// by the effect so a resolve and an unresolve of the same thread produce separate journal entries.
/// </remarks>
/// <param name="threadId">GraphQL node id of the review thread.</param>
/// <param name="effect">Adapter call that changes the thread's resolution.</param>
public sealed class ResolveThreadCommand(string threadId, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.resolve-thread", threadId, effect);
