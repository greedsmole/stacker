using Stacker.Core.Framework;
namespace Stacker.Core.Features.Review;

/// <summary>Durable command for replying inside an existing review thread.</summary>
/// <remarks>
/// Both the pull request and the server comment id belong to the target, because a reply is addressed to the thread rather
/// than to the pull request; the adapter also refuses a comment id that is not a positive number instead of letting the
/// request fail later.
/// </remarks>
/// <param name="number">Pull request number that owns the thread.</param>
/// <param name="commentId">Server id of the review comment being answered.</param>
/// <param name="effect">Adapter call that posts the reply.</param>
public sealed class ReplyCommand(long number, string commentId, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.reply", number + ":" + commentId, effect);
