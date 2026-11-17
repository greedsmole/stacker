using Stacker.Core.Framework;
namespace Stacker.Core.Features.Review;

/// <summary>Durable command for posting one inline review comment at a diff line.</summary>
/// <remarks>
/// The target includes the path and line so the journal names the code location; two comments on the same line share that
/// display target and remain distinct because each has its own operation id. The anchor's commit id is sent with the
/// comment, so a stale anchor is rejected by validation before this command reaches the bus.
/// </remarks>
/// <param name="anchor">Anchor supplying the pull request, path, line and commit for the comment.</param>
/// <param name="effect">Adapter call that posts the inline comment.</param>
public sealed class InlineCommentCommand(ReviewAnchor anchor, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.inline-comment", anchor.PullRequest + ":" + anchor.Path + ":" + anchor.Line, effect);
