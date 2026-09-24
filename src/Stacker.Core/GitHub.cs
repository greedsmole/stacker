namespace Stacker.Core;

public sealed record GitHubRepositoryContext(string Host, long RepositoryId, string Owner, string Name,
    string CloneUrl, string User, string DefaultBranch)
{
    public string Identity => $"{Host}/{RepositoryId}/{User}";
    public string Display => $"{Host}/{Owner}/{Name} · @{User}";
}
public sealed record PullRequest(long Number, string Title, long BaseRepositoryId, string BaseRef, string BaseSha,
    long HeadRepositoryId, string HeadRef, string HeadSha, bool IsDraft, string Url)
{
    public string Label => $"#{Number} · {Title}";
}
public sealed record GitHubSnapshot(GitHubRepositoryContext Context, IReadOnlyList<PullRequest> PullRequests, DateTimeOffset UpdatedAt);
public sealed record DiscoveredStack(string Id, string Name, IReadOnlyList<PullRequest> Layers, bool SharedPrefix);
public sealed record DiscoveryResult(IReadOnlyList<DiscoveredStack> Stacks, IReadOnlyList<PullRequest> Other, IReadOnlyList<string> Warnings);
public sealed class RepositoryPreferences
{
    public string? GitHubOverride { get; set; }
    public List<string>? BoundaryBranches { get; set; }
}
public sealed record ReviewAnchor(long PullRequest, string HeadSha, string BaseSha, string Path, string Side,
    int Line, int? StartLine = null, string? StartSide = null);
public sealed record DiscussionComment(string Id, string Author, string Body, string Url, DateTimeOffset CreatedAt);
public sealed record ReviewSummary(string Id, string Author, string Body, string State, DateTimeOffset? SubmittedAt);
public sealed record ReviewThread(string Id, bool IsResolved, bool IsOutdated, bool CanResolve, ReviewAnchor? Anchor,
    IReadOnlyList<DiscussionComment> Comments, string? FilePath = null, bool IsFileLevel = false)
{
    public string? Path => FilePath ?? Anchor?.Path;
    public string Label => $"{Path ?? "Unknown file"}{(IsFileLevel ? " · File comment" : Anchor?.Line is { } line ? $":{line}" : " · Line comment")} · {(IsOutdated ? "Outdated" : IsResolved ? "Resolved" : "Open")}";
    public string Text => string.Join("\n\n", Comments.Select(c => $"@{c.Author}\n{c.Body}"));
}
public sealed record ReviewDiscussion(IReadOnlyList<DiscussionComment> Comments, IReadOnlyList<ReviewSummary> Reviews, IReadOnlyList<ReviewThread> Threads);
public sealed record DraftComment(ReviewAnchor Anchor, string Body);
public sealed class ReviewDraft
{
    public string ContextIdentity { get; set; } = "";
    public long PullRequest { get; set; }
    public string HeadSha { get; set; } = "";
    public string BaseSha { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Composer { get; set; } = "";
    public Dictionary<string, string> FileComposers { get; set; } = [];
    public string? PendingFilePath { get; set; }
    public string Decision { get; set; } = "COMMENT";
    public List<DraftComment> Comments { get; set; } = [];
    public string? PendingOperation { get; set; }
}
public interface IGitHubReader
{
    Task<GitHubRepositoryContext> ConnectAsync(string root, string? repositoryOverride, CancellationToken ct = default);
    Task<GitHubSnapshot> SnapshotAsync(GitHubRepositoryContext context, CancellationToken ct = default);
    Task<PullRequest> PullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default);
    Task<ReviewDiscussion> DiscussionAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default);
    Task ValidateAnchorsAsync(GitHubRepositoryContext context, PullRequest pr, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ChangedFilePathsAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default);
    Task ValidateFileAsync(GitHubRepositoryContext context, PullRequest pr, string path, CancellationToken ct = default);
    Task VerifyAccessAsync(GitHubRepositoryContext context, CancellationToken ct = default);
}
public interface IGitHubWriter
{
    Task CommentAsync(GitHubRepositoryContext context, long number, string body, CancellationToken ct = default);
    Task FileCommentAsync(GitHubRepositoryContext context, PullRequest pr, string path, string body, CancellationToken ct = default);
    Task InlineCommentAsync(GitHubRepositoryContext context, DraftComment comment, CancellationToken ct = default);
    Task ReplyAsync(GitHubRepositoryContext context, long number, string commentId, string body, CancellationToken ct = default);
    Task ResolveAsync(GitHubRepositoryContext context, string threadId, bool resolved, CancellationToken ct = default);
    Task SubmitReviewAsync(GitHubRepositoryContext context, ReviewDraft draft, CancellationToken ct = default);
}
public interface IGitObjectCache
{
    Task<RepositorySnapshot> PrepareAsync(GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default);
    Task ClearAsync(GitHubRepositoryContext context, CancellationToken ct = default);
}
public sealed record SyntaxSpan(int Start, int Length, string Color, bool Italic = false);
public interface ISyntaxHighlighter
{
    Task<IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>> HighlightAsync(string blobId, string path, string text, string theme, CancellationToken ct = default);
}
public sealed record BlobContent(string Id, string Text);
