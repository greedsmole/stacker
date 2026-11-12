using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Core.Features.Review;
namespace Stacker.Infrastructure;

/// <summary>Serializes explicit GitHub mutations and durably records attempted sends; never retries them.</summary>
public sealed class CommandGitHubWriter(GitHubCli client, CommandBus bus) : IGitHubWriter, IGitHubPullRequestWriter
{
    public Task CommentAsync(GitHubRepositoryContext context, long number, string body, CancellationToken ct = default) =>
        Run(context, new CommentCommand(number, (_, token) => Capture(() => client.CommentAsync(context, number, body, token))), ct);
    public Task FileCommentAsync(GitHubRepositoryContext context, PullRequest pr, string path, string body, CancellationToken ct = default) =>
        Run(context, new FileCommentCommand(new(pr.Number, pr.HeadSha, pr.BaseSha, path, "RIGHT", 0),
            (_, token) => Capture(() => client.FileCommentAsync(context, pr, path, body, token))), ct);
    public Task InlineCommentAsync(GitHubRepositoryContext context, DraftComment comment, CancellationToken ct = default) =>
        Run(context, new InlineCommentCommand(comment.Anchor, (_, token) => Capture(() => client.InlineCommentAsync(context, comment, token))), ct);
    public Task ReplyAsync(GitHubRepositoryContext context, long number, string commentId, string body, CancellationToken ct = default) =>
        Run(context, new ReplyCommand(number, commentId, (_, token) => Capture(() => client.ReplyAsync(context, number, commentId, body, token))), ct);
    public Task ResolveAsync(GitHubRepositoryContext context, string threadId, bool resolved, CancellationToken ct = default) =>
        Run(context, new ResolveThreadCommand(threadId, (_, token) => Capture(() => client.ResolveAsync(context, threadId, resolved, token))), ct);
    public Task SubmitReviewAsync(GitHubRepositoryContext context, ReviewDraft draft, CancellationToken ct = default) =>
        Run(context, new SubmitReviewCommand(draft.PullRequest, (_, token) => Capture(() => client.SubmitReviewAsync(context, draft, token))), ct);
    public Task<PullRequest> CreatePullRequestAsync(GitHubRepositoryContext context, string headBranch, string baseBranch,
        string title, string body, bool draft, CancellationToken ct = default) =>
        Run(context, new CreatePullRequestCommand(Branch(headBranch), Branch(baseBranch),
            (_, token) => Capture(() => client.CreatePullRequestAsync(context, headBranch, baseBranch, title, body, draft, token))), ct);
    public Task<PullRequest> UpdatePullRequestBaseAsync(GitHubRepositoryContext context, long number, string baseBranch, CancellationToken ct = default) =>
        Run(context, new RetargetPullRequestCommand(number, Branch(baseBranch),
            (_, token) => Capture(() => client.UpdatePullRequestBaseAsync(context, number, baseBranch, token))), ct);

    private static LocalBranch Branch(string name) => LocalBranch.Parse("refs/heads/" + name).OrThrow();
    private async Task<T> Run<T>(GitHubRepositoryContext context, EffectCommand<T> command, CancellationToken ct)
    {
        var outcome = await bus.RunRemoteAsync(context, command, ct);
        if (!outcome.Ok && command.Value is { IsOk: true })
        {
            var problem = outcome.Problem ?? Problem.Of(ProblemCodes.StoreWrite, outcome.Message);
            throw new StackerException(problem with { Certainty = EffectCertainty.Confirmed });
        }
        return command.Value is { } value ? value.OrThrow() : throw new StackerException(Problem.Of(ProblemCodes.GitHubRequest, outcome.Message) with { Certainty = outcome.Certainty });
    }
    private static Task<Result<Unit>> Capture(Func<Task> effect) => Capture(async () => { await effect(); return Unit.Value; });
    private static async Task<Result<T>> Capture<T>(Func<Task<T>> effect)
    {
        try { return Result<T>.Ok(await effect()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<T>.Fail(Boundary.Problem(ex, ProblemCodes.GitHubRequest)); }
    }

    Task<Result<Unit>> IGitHubWriter.CommentAsync(GitHubRepositoryContext context, long number, string body, CancellationToken ct) => Boundary.CaptureAsync(() => CommentAsync(context, number, body, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.FileCommentAsync(GitHubRepositoryContext context, PullRequest pr, string path, string body, CancellationToken ct) => Boundary.CaptureAsync(() => FileCommentAsync(context, pr, path, body, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.InlineCommentAsync(GitHubRepositoryContext context, DraftComment comment, CancellationToken ct) => Boundary.CaptureAsync(() => InlineCommentAsync(context, comment, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.ReplyAsync(GitHubRepositoryContext context, long number, string commentId, string body, CancellationToken ct) => Boundary.CaptureAsync(() => ReplyAsync(context, number, commentId, body, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.ResolveAsync(GitHubRepositoryContext context, string threadId, bool resolved, CancellationToken ct) => Boundary.CaptureAsync(() => ResolveAsync(context, threadId, resolved, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.SubmitReviewAsync(GitHubRepositoryContext context, ReviewDraft draft, CancellationToken ct) => Boundary.CaptureAsync(() => SubmitReviewAsync(context, draft, ct), ProblemCodes.GitHubRequest);

    Task<Result<PullRequest>> IGitHubPullRequestWriter.CreatePullRequestAsync(GitHubRepositoryContext context, string headBranch, string baseBranch, string title, string body, bool draft, CancellationToken ct) => Boundary.CaptureAsync(() => CreatePullRequestAsync(context, headBranch, baseBranch, title, body, draft, ct), ProblemCodes.GitHubRequest);

    Task<Result<PullRequest>> IGitHubPullRequestWriter.UpdatePullRequestBaseAsync(GitHubRepositoryContext context, long number, string baseBranch, CancellationToken ct) => Boundary.CaptureAsync(() => UpdatePullRequestBaseAsync(context, number, baseBranch, ct), ProblemCodes.GitHubRequest);
}
