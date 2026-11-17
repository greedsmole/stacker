using Stacker.Core.Framework;
namespace Stacker.Core.Features.Review;

/// <summary>Durable command for opening the pull request of one stack layer.</summary>
/// <remarks>
/// The target records the layer's head and parent refs so the journal shows which branch pair was published even if the
/// pull request creation is interrupted before a number exists. Locally validated branch refs are used instead of raw
/// text, so an invalid name fails before a command is built. Creation is not idempotent: the caller re-derives the plan
/// from a live snapshot immediately before publishing, which is what prevents a duplicate pull request.
/// </remarks>
/// <param name="head">Local branch of the layer, which becomes the pull request head.</param>
/// <param name="parent">Local branch of the layer's parent, which becomes the base.</param>
/// <param name="effect">Adapter call that creates the pull request.</param>
public sealed class CreatePullRequestCommand(LocalBranch head, LocalBranch parent,
    Func<CommandContext, CancellationToken, Task<Result<PullRequest>>> effect)
    : EffectCommand<PullRequest>("github.create-pr", head.Ref + "->" + parent.Ref, effect);
