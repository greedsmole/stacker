using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Explicitly invoked GitHub mutations for comments, thread state, and review submission.</summary>
/// <remarks>Writers must validate PR version/anchors before publishing; draft persistence belongs to the caller.</remarks>
public interface IGitHubWriter
{
    /// <summary>Posts one conversation comment on the pull request, with no review decision attached.</summary>
    /// <param name="context">Pinned host, repository and account to write with.</param>
    /// <param name="number">Pull request number the comment is posted on.</param>
    /// <param name="body">Markdown body, already carrying the publication marker that identifies this send.</param>
    /// <param name="ct">Cancels the request.</param>
    Task<Result<Unit>> CommentAsync(GitHubRepositoryContext context, long number, string body, CancellationToken ct = default);
    /// <summary>Posts a file-level review comment addressed to the file, without inventing a line number.</summary>
    /// <param name="context">Pinned host, repository and account to write with.</param>
    /// <param name="pr">Snapshot whose number and head commit address the comment; the head must still be current.</param>
    /// <param name="path">Changed file the comment is attached to.</param>
    /// <param name="body">Markdown body, already carrying the publication marker.</param>
    /// <param name="ct">Cancels the request.</param>
    Task<Result<Unit>> FileCommentAsync(GitHubRepositoryContext context, PullRequest pr, string path, string body, CancellationToken ct = default);
    /// <summary>Posts one inline review comment at the anchor's line and side on the anchor's commit.</summary>
    /// <param name="context">Pinned host, repository and account to write with.</param>
    /// <param name="comment">Comment whose anchor supplies the pull request, commit, path and line range.</param>
    /// <param name="ct">Cancels the request.</param>
    Task<Result<Unit>> InlineCommentAsync(GitHubRepositoryContext context, DraftComment comment, CancellationToken ct = default);
    /// <summary>Replies to an existing review comment thread.</summary>
    /// <param name="context">Pinned host, repository and account to write with.</param>
    /// <param name="number">Pull request number that owns the thread.</param>
    /// <param name="commentId">Server id of the review comment being replied to.</param>
    /// <param name="body">Markdown body, already carrying the publication marker.</param>
    /// <param name="ct">Cancels the request.</param>
    Task<Result<Unit>> ReplyAsync(GitHubRepositoryContext context, long number, string commentId, string body, CancellationToken ct = default);
    /// <summary>Resolves or unresolves one review thread.</summary>
    /// <param name="context">Pinned host, repository and account to write with.</param>
    /// <param name="threadId">GraphQL node id of the review thread.</param>
    /// <param name="resolved">True to resolve the thread, false to reopen it.</param>
    /// <param name="ct">Cancels the mutation.</param>
    Task<Result<Unit>> ResolveAsync(GitHubRepositoryContext context, string threadId, bool resolved, CancellationToken ct = default);
    /// <summary>Submits the grouped review with its decision, summary and draft line comments in one request.</summary>
    /// <param name="context">Pinned host, repository and account to write with.</param>
    /// <param name="draft">Draft supplying the pull request, head commit, summary, decision and line comments.</param>
    /// <param name="ct">Cancels the request.</param>
    Task<Result<Unit>> SubmitReviewAsync(GitHubRepositoryContext context, ReviewDraft draft, CancellationToken ct = default);
}
