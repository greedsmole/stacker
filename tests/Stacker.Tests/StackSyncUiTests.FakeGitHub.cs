using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed partial class StackSyncUiTests
{
    /// <summary>In-memory server double: writes are reflected in later reads, so the flow can be checked end to end.</summary>
    private sealed class FakeGitHub : IGitHubReader, IGitHubPullRequestWriter
    {
        private readonly List<PullRequest> _pullRequests;
        public FakeGitHub(IReadOnlyList<PullRequest> pullRequests) => _pullRequests = [.. pullRequests];
        public GitHubRepositoryContext Context { get; } = new("github.com", 1, "team", "repo", "https://github.com/team/repo.git", "alice", "main");
        public List<string> Calls { get; } = [];
        public int? LoseResponseOnWrite
        {
            get; set;
        }
        public bool FailSnapshotAfterWrite
        {
            get; init;
        }
        /// <summary>Stands in for someone else opening a pull request while the preview is open.</summary>
        public void AddPullRequest(PullRequest pullRequest) => _pullRequests.Add(pullRequest);
        public Task<GitHubRepositoryContext> ConnectAsync(string root, string? repositoryOverride, CancellationToken ct = default) => Task.FromResult(Context);
        public Task<GitHubSnapshot> SnapshotAsync(GitHubRepositoryContext context, CancellationToken ct = default)
        {
            if (FailSnapshotAfterWrite && Calls.Count > 0)
                throw new StackerException("injected refresh failure");
            return Task.FromResult(new GitHubSnapshot(context, _pullRequests.ToArray(), DateTimeOffset.UtcNow));
        }
        public Task VerifyAccessAsync(GitHubRepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PullRequest> CreatePullRequestAsync(GitHubRepositoryContext context, string headBranch, string baseBranch, string title, string body, bool draft, CancellationToken ct = default)
        {
            Calls.Add($"create {headBranch} → {baseBranch}: {title}");
            var number = 100 + _pullRequests.Count(pr => pr.Number >= 100);
            var created = new PullRequest(number, title, context.RepositoryId, baseBranch, new('a', 40), context.RepositoryId, headBranch, new('b', 40), draft, $"https://example.invalid/pr/{number}");
            _pullRequests.Add(created);
            return AcknowledgeAsync(created);
        }
        public Task<PullRequest> UpdatePullRequestBaseAsync(GitHubRepositoryContext context, long number, string baseBranch, CancellationToken ct = default)
        {
            Calls.Add($"base #{number} → {baseBranch}");
            var index = _pullRequests.FindIndex(pr => pr.Number == number);
            var updated = _pullRequests[index] with
            {
                BaseRef = baseBranch
            };
            _pullRequests[index] = updated;
            return AcknowledgeAsync(updated);
        }
        private Task<PullRequest> AcknowledgeAsync(PullRequest pr) => Calls.Count == LoseResponseOnWrite
            ? throw new StackerException(Problem.Of(ProblemCodes.GitHubRequest, "injected response loss"))
            : Task.FromResult(pr);
        public Task<PullRequest> PullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReviewDiscussion> DiscussionAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAnchorsAsync(GitHubRepositoryContext context, PullRequest pr, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ChangedFilePathsAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateFileAsync(GitHubRepositoryContext context, PullRequest pr, string path, CancellationToken ct = default) => throw new NotSupportedException();

        Task<Result<GitHubRepositoryContext>> IGitHubReader.ConnectAsync(string root, string? repositoryOverride, CancellationToken ct) => Boundary.CaptureAsync(() => ConnectAsync(root, repositoryOverride, ct), ProblemCodes.GitHubRequest);

        Task<Result<GitHubSnapshot>> IGitHubReader.SnapshotAsync(GitHubRepositoryContext context, CancellationToken ct) => Boundary.CaptureAsync(() => SnapshotAsync(context, ct), ProblemCodes.GitHubRequest);

        Task<Result<PullRequest>> IGitHubReader.PullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct) => Boundary.CaptureAsync(() => PullRequestAsync(context, number, ct), ProblemCodes.GitHubRequest);

        Task<Result<ReviewDiscussion>> IGitHubReader.DiscussionAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct) => Boundary.CaptureAsync(() => DiscussionAsync(context, pr, ct), ProblemCodes.GitHubRequest);

        Task<Result<Unit>> IGitHubReader.ValidateAnchorsAsync(GitHubRepositoryContext context, PullRequest pr, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct) => Boundary.CaptureAsync(() => ValidateAnchorsAsync(context, pr, anchors, ct), ProblemCodes.GitHubRequest);

        Task<Result<IReadOnlyList<string>>> IGitHubReader.ChangedFilePathsAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct) => Boundary.CaptureAsync(() => ChangedFilePathsAsync(context, pr, ct), ProblemCodes.GitHubRequest);

        Task<Result<Unit>> IGitHubReader.ValidateFileAsync(GitHubRepositoryContext context, PullRequest pr, string path, CancellationToken ct) => Boundary.CaptureAsync(() => ValidateFileAsync(context, pr, path, ct), ProblemCodes.GitHubRequest);

        Task<Result<Unit>> IGitHubReader.VerifyAccessAsync(GitHubRepositoryContext context, CancellationToken ct) => Boundary.CaptureAsync(() => VerifyAccessAsync(context, ct), ProblemCodes.GitHubRequest);

        Task<Result<PullRequest>> IGitHubPullRequestWriter.CreatePullRequestAsync(GitHubRepositoryContext context, string headBranch, string baseBranch, string title, string body, bool draft, CancellationToken ct) => Boundary.CaptureAsync(() => CreatePullRequestAsync(context, headBranch, baseBranch, title, body, draft, ct), ProblemCodes.GitHubRequest);

        Task<Result<PullRequest>> IGitHubPullRequestWriter.UpdatePullRequestBaseAsync(GitHubRepositoryContext context, long number, string baseBranch, CancellationToken ct) => Boundary.CaptureAsync(() => UpdatePullRequestBaseAsync(context, number, baseBranch, ct), ProblemCodes.GitHubRequest);
    }
}
