using Stacker.Core.Framework;
namespace Stacker.Core.Features.Review;

public sealed class CommentCommand(long number, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.comment", number.ToString(), effect);
public sealed class FileCommentCommand(ReviewAnchor anchor, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.file-comment", anchor.PullRequest + ":" + anchor.Path, effect);
public sealed class InlineCommentCommand(ReviewAnchor anchor, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.inline-comment", anchor.PullRequest + ":" + anchor.Path + ":" + anchor.Line, effect);
public sealed class ReplyCommand(long number, string commentId, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.reply", number + ":" + commentId, effect);
public sealed class SubmitReviewCommand(long number, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.review", number.ToString(), effect);
public sealed class ResolveThreadCommand(string threadId, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.resolve-thread", threadId, effect);
public sealed class CreatePullRequestCommand(LocalBranch head, LocalBranch parent,
    Func<CommandContext, CancellationToken, Task<Result<PullRequest>>> effect)
    : EffectCommand<PullRequest>("github.create-pr", head.Ref + "->" + parent.Ref, effect);
public sealed class RetargetPullRequestCommand(long number, LocalBranch parent,
    Func<CommandContext, CancellationToken, Task<Result<PullRequest>>> effect)
    : EffectCommand<PullRequest>("github.retarget-pr", number + "->" + parent.Ref, effect);
