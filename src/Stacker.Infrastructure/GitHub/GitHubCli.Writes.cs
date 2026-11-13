using System.Text.Json;
using System.Text.RegularExpressions;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

public sealed partial class GitHubCli
{
    /// <summary>Posts one ordinary PR conversation comment, not a review decision or code-thread comment.</summary>
    public async Task CommentAsync(GitHubRepositoryContext context, long number, string body, CancellationToken ct = default) =>
        _ = await Api(context.Host, $"{Repo(context)}/issues/{number}/comments", ct, new
        {
            body
        });
    /// <summary>Posts a file-level review comment without fabricating a line number.</summary>
    public async Task FileCommentAsync(GitHubRepositoryContext context, PullRequest pr, string path, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(body))
            throw new StackerException("Choose a file and enter a comment.");
        _ = await Api(context.Host, $"{Repo(context)}/pulls/{pr.Number}/comments", ct,
            new
            {
                body,
                commit_id = pr.HeadSha,
                path,
                subject_type = "file"
            });
    }
    /// <summary>Posts an inline comment using the current PR's line/side coordinates.</summary>
    public async Task InlineCommentAsync(GitHubRepositoryContext context, DraftComment comment, CancellationToken ct = default)
    {
        var body = AnchorBody(comment);
        body["commit_id"] = comment.Anchor.HeadSha;
        _ = await Api(context.Host, $"{Repo(context)}/pulls/{comment.Anchor.PullRequest}/comments", ct, body);
    }
    /// <summary>Replies to an existing review thread after validating the server comment ID.</summary>
    public async Task ReplyAsync(GitHubRepositoryContext context, long number, string commentId, string body, CancellationToken ct = default)
    {
        if (!long.TryParse(commentId, out var id) || id <= 0)
            throw new StackerException("Invalid comment ID.");
        _ = await Api(context.Host, $"{Repo(context)}/pulls/{number}/comments/{id}/replies", ct, new
        {
            body
        });
    }
    /// <summary>Changes thread resolution through GitHub's review-thread mutation.</summary>
    public async Task ResolveAsync(GitHubRepositoryContext context, string threadId, bool resolved, CancellationToken ct = default) =>
        _ = await Graph(context.Host, "mutation($id:ID!){" + (resolved ? "resolveReviewThread" : "unresolveReviewThread") + "(input:{threadId:$id}){thread{id isResolved}}}", new
        {
            id = threadId
        }, ct);
    /// <summary>Submits a grouped review and its line comments only when explicitly requested by the user.</summary>
    public async Task SubmitReviewAsync(GitHubRepositoryContext context, ReviewDraft draft, CancellationToken ct = default)
    {
        if (draft.Decision is not ("COMMENT" or "APPROVE" or "REQUEST_CHANGES"))
            throw new StackerException("Invalid review decision.");
        _ = await Api(context.Host, $"{Repo(context)}/pulls/{draft.PullRequest}/reviews", ct,
            new
            {
                commit_id = draft.HeadSha,
                body = draft.Summary,
                @event = draft.Decision,
                comments = draft.Comments.Select(AnchorBody).ToArray()
            });
    }
    /// <summary>Creates a pull request whose base is the layer's parent branch.</summary>
    public async Task<PullRequest> CreatePullRequestAsync(GitHubRepositoryContext context, string headBranch, string baseBranch,
        string title, string body, bool draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(headBranch) || string.IsNullOrWhiteSpace(baseBranch))
            throw new StackerException("A pull request needs both a head and a base branch.");
        var value = await Api(context.Host, Repo(context) + "/pulls", ct,
            new
            {
                title,
                head = headBranch,
                @base = baseBranch,
                body,
                draft
            }, method: "POST");
        return ParsePr(value);
    }
    /// <summary>Retargets one existing pull request after its layer's parent changed.</summary>
    public async Task<PullRequest> UpdatePullRequestBaseAsync(GitHubRepositoryContext context, long number, string baseBranch, CancellationToken ct = default)
    {
        if (number <= 0 || string.IsNullOrWhiteSpace(baseBranch))
            throw new StackerException("A pull request needs a valid number and a base branch.");
        var value = await Api(context.Host, $"{Repo(context)}/pulls/{number}", ct, new
        {
            @base = baseBranch
        }, method: "PATCH");
        return ParsePr(value);
    }
    // GitHub's current review-comment contract uses line/side and optional start_line/start_side, never legacy position.
    private static Dictionary<string, object> AnchorBody(DraftComment comment)
    {
        var a = comment.Anchor;
        if (a.Line <= 0 || a.Side is not ("LEFT" or "RIGHT") || a.StartLine is <= 0 || a.StartLine > a.Line)
            throw new StackerException("Invalid review line range.");
        var body = new Dictionary<string, object> { ["body"] = comment.Body, ["path"] = a.Path, ["line"] = a.Line, ["side"] = a.Side };
        if (a.StartLine is not null && a.StartLine != a.Line)
        {
            body["start_line"] = a.StartLine;
            body["start_side"] = a.StartSide ?? a.Side;
        }
        return body;
    }
}
