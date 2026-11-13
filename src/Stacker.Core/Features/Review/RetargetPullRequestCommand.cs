using Stacker.Core.Framework;
namespace Stacker.Core.Features.Review;

/// <summary>Durable command for retargeting one existing pull request at a new parent branch.</summary>
/// <remarks>
/// The target pairs the pull request number with the new parent ref, which is the information a reader needs when the
/// retarget is interrupted: the number identifies the pull request and the ref identifies the base it should have. The
/// updated pull request comes back from the effect so the caller can report the new base without another read.
/// </remarks>
/// <param name="number">Pull request number to retarget.</param>
/// <param name="parent">Local branch that becomes the new base.</param>
/// <param name="effect">Adapter call that changes the base branch.</param>
public sealed class RetargetPullRequestCommand(long number, LocalBranch parent,
    Func<CommandContext, CancellationToken, Task<Result<PullRequest>>> effect)
    : EffectCommand<PullRequest>("github.retarget-pr", number + "->" + parent.Ref, effect);
