using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class FakeGitHub : IGitHubReader, IGitHubWriter
{
    public PullRequest Current { get; set; } = DiscoveryTests.Pr(42, "main", "feature");
    public List<PullRequest> Extra { get; } = [];
    public bool RejectAccess
    {
        get; set;
    }
    public bool FailAfterSend
    {
        get; set;
    }
    public int Writes
    {
        get; private set;
    }
    public int Verifications
    {
        get; private set;
    }
    public List<DiscussionComment> Published { get; } = [];
    public List<ReviewSummary> Submitted { get; } = [];
    public List<ReviewThread> FileThreads { get; } = [];
    public bool RejectFile
    {
        get; set;
    }
    public TaskCompletionSource<ReviewDiscussion>? DiscussionGate
    {
        get; set;
    }
    public Task<GitHubRepositoryContext> ConnectAsync(string root, string? repositoryOverride, CancellationToken ct = default) => Task.FromResult(Context);
    public static GitHubRepositoryContext Context { get; } = new("github.example", 1, "team", "repo", "https://github.example/team/repo.git", "alice", "main");
    public Task<GitHubSnapshot> SnapshotAsync(GitHubRepositoryContext context, CancellationToken ct = default) => Task.FromResult(new GitHubSnapshot(context, [Current, .. Extra], DateTimeOffset.UtcNow));
    public Task<PullRequest> PullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default) => Task.FromResult(Current);
    public Task<ReviewDiscussion> DiscussionAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default) => DiscussionGate is { } gate ? gate.Task.WaitAsync(ct) : Task.FromResult(new ReviewDiscussion(Published.ToArray(), Submitted.ToArray(), FileThreads.ToArray()));
    public Task VerifyAccessAsync(GitHubRepositoryContext context, CancellationToken ct = default)
    {
        Verifications++;
        if (RejectAccess)
            throw new StackerException("HTTP 403");
        return Task.CompletedTask;
    }
    public Task ValidateAnchorsAsync(GitHubRepositoryContext context, PullRequest pr, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct = default)
    {
        if (anchors.Any(a => a.Line != 1 || a.HeadSha != pr.HeadSha))
            throw new StackerException("Invalid anchor");
        return Task.CompletedTask;
    }
    public Task CommentAsync(GitHubRepositoryContext context, long number, string body, CancellationToken ct = default)
    {
        Writes++;
        Published.Add(new("1", "alice", body, "", DateTimeOffset.UtcNow));
        if (FailAfterSend)
            throw new StackerException("Timed out after send");
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<string>> ChangedFilePathsAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(["renamed ü.png", "other.cs"]);
    public Task ValidateFileAsync(GitHubRepositoryContext context, PullRequest pr, string path, CancellationToken ct = default)
    {
        if (RejectFile)
            throw new StackerException("File disappeared");
        return Task.CompletedTask;
    }
    public Task FileCommentAsync(GitHubRepositoryContext context, PullRequest pr, string path, string body, CancellationToken ct = default)
    {
        Writes++;
        FileThreads.Add(new("file-thread", false, false, true, null, [new("2", "alice", body, "", DateTimeOffset.UtcNow)], path, true));
        if (FailAfterSend)
            throw new StackerException("Timed out after send");
        return Task.CompletedTask;
    }
    public Task InlineCommentAsync(GitHubRepositoryContext context, DraftComment comment, CancellationToken ct = default) => CommentAsync(context, comment.Anchor.PullRequest, comment.Body, ct);
    public Task ReplyAsync(GitHubRepositoryContext context, long number, string commentId, string body, CancellationToken ct = default) => CommentAsync(context, number, body, ct);
    public Task ResolveAsync(GitHubRepositoryContext context, string threadId, bool resolved, CancellationToken ct = default)
    {
        Writes++;
        return Task.CompletedTask;
    }
    public Task SubmitReviewAsync(GitHubRepositoryContext context, ReviewDraft draft, CancellationToken ct = default)
    {
        Writes++;
        Submitted.Add(new("1", "alice", draft.Summary, draft.Decision, DateTimeOffset.UtcNow));
        if (FailAfterSend)
            throw new StackerException("Timed out after send");
        return Task.CompletedTask;
    }

    Task<Result<GitHubRepositoryContext>> IGitHubReader.ConnectAsync(string root, string? repositoryOverride, CancellationToken ct) => Boundary.CaptureAsync(() => ConnectAsync(root, repositoryOverride, ct), ProblemCodes.GitHubRequest);

    Task<Result<GitHubSnapshot>> IGitHubReader.SnapshotAsync(GitHubRepositoryContext context, CancellationToken ct) => Boundary.CaptureAsync(() => SnapshotAsync(context, ct), ProblemCodes.GitHubRequest);

    Task<Result<PullRequest>> IGitHubReader.PullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct) => Boundary.CaptureAsync(() => PullRequestAsync(context, number, ct), ProblemCodes.GitHubRequest);

    Task<Result<ReviewDiscussion>> IGitHubReader.DiscussionAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct) => Boundary.CaptureAsync(() => DiscussionAsync(context, pr, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubReader.ValidateAnchorsAsync(GitHubRepositoryContext context, PullRequest pr, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct) => Boundary.CaptureAsync(() => ValidateAnchorsAsync(context, pr, anchors, ct), ProblemCodes.GitHubRequest);

    Task<Result<IReadOnlyList<string>>> IGitHubReader.ChangedFilePathsAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct) => Boundary.CaptureAsync(() => ChangedFilePathsAsync(context, pr, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubReader.ValidateFileAsync(GitHubRepositoryContext context, PullRequest pr, string path, CancellationToken ct) => Boundary.CaptureAsync(() => ValidateFileAsync(context, pr, path, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubReader.VerifyAccessAsync(GitHubRepositoryContext context, CancellationToken ct) => Boundary.CaptureAsync(() => VerifyAccessAsync(context, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.CommentAsync(GitHubRepositoryContext context, long number, string body, CancellationToken ct) => Boundary.CaptureAsync(() => CommentAsync(context, number, body, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.FileCommentAsync(GitHubRepositoryContext context, PullRequest pr, string path, string body, CancellationToken ct) => Boundary.CaptureAsync(() => FileCommentAsync(context, pr, path, body, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.InlineCommentAsync(GitHubRepositoryContext context, DraftComment comment, CancellationToken ct) => Boundary.CaptureAsync(() => InlineCommentAsync(context, comment, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.ReplyAsync(GitHubRepositoryContext context, long number, string commentId, string body, CancellationToken ct) => Boundary.CaptureAsync(() => ReplyAsync(context, number, commentId, body, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.ResolveAsync(GitHubRepositoryContext context, string threadId, bool resolved, CancellationToken ct) => Boundary.CaptureAsync(() => ResolveAsync(context, threadId, resolved, ct), ProblemCodes.GitHubRequest);

    Task<Result<Unit>> IGitHubWriter.SubmitReviewAsync(GitHubRepositoryContext context, ReviewDraft draft, CancellationToken ct) => Boundary.CaptureAsync(() => SubmitReviewAsync(context, draft, ct), ProblemCodes.GitHubRequest);
}
