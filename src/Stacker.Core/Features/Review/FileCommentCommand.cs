using Stacker.Core.Framework;
namespace Stacker.Core.Features.Review;

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
