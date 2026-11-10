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
/// <summary>Durable command for posting one file-level review comment.</summary>
/// <remarks>
/// A file comment has no line, so the target names the pull request and the path only; the anchor's line and side are
/// ignored on purpose, and two files can never share a journal target. The comment is still tied to the pull request
/// version by the commit id the adapter sends, which is why the version is validated before this command is built.
/// </remarks>
/// <param name="anchor">Anchor supplying the pull request number and path; its line coordinates are not used.</param>
/// <param name="effect">Adapter call that posts the file comment.</param>
public sealed class FileCommentCommand(ReviewAnchor anchor, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.file-comment", anchor.PullRequest + ":" + anchor.Path, effect);
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
/// <summary>Durable command for resolving or reopening one review thread.</summary>
/// <remarks>
/// The thread node id is the whole target: the mutation addresses the thread directly, and the desired state is captured
/// by the effect so a resolve and an unresolve of the same thread produce separate journal entries.
/// </remarks>
/// <param name="threadId">GraphQL node id of the review thread.</param>
/// <param name="effect">Adapter call that changes the thread's resolution.</param>
public sealed class ResolveThreadCommand(string threadId, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("github.resolve-thread", threadId, effect);
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
