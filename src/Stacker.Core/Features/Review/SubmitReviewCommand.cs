using Stacker.Core.Framework;
namespace Stacker.Core.Features.Review;

/// <summary>Durable command for submitting one grouped review with its decision and line comments.</summary>
/// <remarks>
/// One review submission is one remote effect even when it carries many line comments, so the target is just the pull
/// request number; the comments travel inside the draft supplied to the effect. A failure here is reported to the user as a
/// possibly published review rather than retried, because GitHub exposes no exactly-once submit.
/// </remarks>
/// <param name="number">Pull request number receiving the review.</param>
/// <param name="effect">Adapter call that submits the review.</param>
public sealed class SubmitReviewCommand(long number, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.review", number.ToString(), effect);
