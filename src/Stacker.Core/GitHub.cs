namespace Stacker.Core;

/// <summary>Resolved GitHub identity for the repository origin and active gh account.</summary>
public sealed record GitHubRepositoryContext(string Host, long RepositoryId, string Owner, string Name,
    string CloneUrl, string User, string DefaultBranch)
{
    public string Identity => $"{Host}/{RepositoryId}/{User}";
    public string Display => $"{Host}/{Owner}/{Name} · @{User}";
}
/// <summary>Server snapshot of a pull request's base/head refs and immutable commit IDs.</summary>
public sealed record PullRequest(long Number, string Title, long BaseRepositoryId, string BaseRef, string BaseSha,
    long HeadRepositoryId, string HeadRef, string HeadSha, bool IsDraft, string Url)
{
    public string Label => $"#{Number} · {Title}";
}
/// <summary>One timestamped page-complete set of open pull requests used for stack discovery.</summary>
public sealed record GitHubSnapshot(GitHubRepositoryContext Context, IReadOnlyList<PullRequest> PullRequests, DateTimeOffset UpdatedAt);
public sealed record DiscoveredStack(string Id, string Name, IReadOnlyList<PullRequest> Layers, bool SharedPrefix);
public sealed record DiscoveryResult(IReadOnlyList<DiscoveredStack> Stacks, IReadOnlyList<PullRequest> Other, IReadOnlyList<string> Warnings);
public sealed class RepositoryPreferences
{
    public string? GitHubOverride { get; set; }
    public List<string>? BoundaryBranches { get; set; }
}
/// <summary>GitHub comment target tied to the PR version and a specific side/line of its diff.</summary>
public sealed record ReviewAnchor(long PullRequest, string HeadSha, string BaseSha, string Path, string Side,
    int Line, int? StartLine = null, string? StartSide = null);
/// <summary>One ordinary PR conversation comment, separate from review decisions and code threads.</summary>
public sealed record DiscussionComment(string Id, string Author, string Body, string Url, DateTimeOffset CreatedAt)
{ public string DisplayBody => ReviewText.Visible(Body); }
/// <summary>One reviewer submission summary such as approved, commented, or requested changes.</summary>
public sealed record ReviewSummary(string Id, string Author, string Body, string State, DateTimeOffset? SubmittedAt)
{ public string DisplayBody => ReviewText.Visible(Body); }
internal static class ReviewText
{
    public static string Visible(string body) => System.Text.RegularExpressions.Regex.Replace(body, @"\s*<!-- stacker:[0-9a-f]{32} -->\s*$", "");
}
/// <summary>Code-review thread, which may be resolved, outdated, or anchored to a file or line range.</summary>
public sealed record ReviewThread(string Id, bool IsResolved, bool IsOutdated, bool CanResolve, ReviewAnchor? Anchor,
    IReadOnlyList<DiscussionComment> Comments, string? FilePath = null, bool IsFileLevel = false)
{
    public string? Path => FilePath ?? Anchor?.Path;
    public string Label => $"{Path ?? "Unknown file"}{(IsFileLevel ? " · File comment" : Anchor?.Line is { } line ? $":{line}" : " · Line comment")} · {(IsOutdated ? "Outdated" : IsResolved ? "Resolved" : "Open")}";
    public string Text => string.Join("\n\n", Comments.Select(c => $"@{c.Author}\n{c.Body}"));
}
/// <summary>Distinct collections of PR conversation, reviewer submissions, and code threads.</summary>
public sealed record ReviewDiscussion(IReadOnlyList<DiscussionComment> Comments, IReadOnlyList<ReviewSummary> Reviews, IReadOnlyList<ReviewThread> Threads);
public sealed record DraftComment(ReviewAnchor Anchor, string Body);
/// <summary>Persisted local review work, including unsent text retained across navigation and transient failures.</summary>
public sealed class ReviewDraft
{
    public string ContextIdentity { get; set; } = "";
    public long PullRequest { get; set; }
    public string HeadSha { get; set; } = "";
    public string BaseSha { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Composer { get; set; } = "";
    public int EditorVersion { get; set; }
    public string RecoveredText { get; set; } = "";
    public Dictionary<string, string> ContextComposers { get; set; } = [];
    public string? PendingComposerKey { get; set; }
    public string? PendingBody { get; set; }
    public string? PendingSummary { get; set; }
    public List<DraftComment>? PendingReviewComments { get; set; }
    public Dictionary<string, string> FileComposers { get; set; } = [];
    public string? PendingFilePath { get; set; }
    public string Decision { get; set; } = "COMMENT";
    public List<DraftComment> Comments { get; set; } = [];
    public string? PendingOperation { get; set; }
}
/// <summary>Read-only GitHub operations; all methods must use the context's explicit host and repository.</summary>
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
/// <summary>Explicitly invoked GitHub mutations for comments, thread state, and review submission.</summary>
/// <remarks>Writers must validate PR version/anchors before publishing; draft persistence belongs to the caller.</remarks>
public interface IGitHubWriter
{
    Task CommentAsync(GitHubRepositoryContext context, long number, string body, CancellationToken ct = default);
    Task FileCommentAsync(GitHubRepositoryContext context, PullRequest pr, string path, string body, CancellationToken ct = default);
    Task InlineCommentAsync(GitHubRepositoryContext context, DraftComment comment, CancellationToken ct = default);
    Task ReplyAsync(GitHubRepositoryContext context, long number, string commentId, string body, CancellationToken ct = default);
    Task ResolveAsync(GitHubRepositoryContext context, string threadId, bool resolved, CancellationToken ct = default);
    Task SubmitReviewAsync(GitHubRepositoryContext context, ReviewDraft draft, CancellationToken ct = default);
}
/// <summary>Fetches remote PR objects into an application-owned repository cache without touching the user's checkout.</summary>
public interface IGitObjectCache
{
    Task<RepositorySnapshot> PrepareAsync(GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default);
    Task ClearAsync(GitHubRepositoryContext context, CancellationToken ct = default);
}
/// <summary>One syntax-colored character range in a source line.</summary>
public sealed record SyntaxSpan(int Start, int Length, string Color, bool Italic = false);
/// <summary>Optional syntax coloring service; callers can render plain text while tokenization is pending or unavailable.</summary>
public interface ISyntaxHighlighter
{
    Task<IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>> HighlightAsync(string blobId, string path, string text, string theme, CancellationToken ct = default);
}
public sealed record BlobContent(string Id, string Text);
