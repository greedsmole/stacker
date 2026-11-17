using Stacker.Core.Framework;
namespace Stacker.Core.Features.Review;

/// <summary>Durable command for posting one pull-request conversation comment.</summary>
/// <remarks>
/// The bus journals one effect step with this kind and target before the adapter runs, so an interrupted send still leaves
/// a durable record naming the pull request it addressed. Remote effects are never retried or compensated automatically:
/// this command exists so the attempt is evidence, not so a failure can be undone.
/// </remarks>
/// <param name="number">Pull request number, used as the journal target and passed to the effect.</param>
/// <param name="effect">Adapter call that performs the mutation and returns its result.</param>
public sealed class CommentCommand(long number, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.comment", number.ToString(), effect);
