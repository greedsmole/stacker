using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Resolved GitHub identity for the repository origin and active gh account.</summary>
/// <remarks>
/// Every read and write is pinned to this context, so a settings change, a second account or a different clone cannot
/// silently retarget a queued operation. <see cref="Identity"/> is the scope used for draft keys, journals and object
/// references; <see cref="Display"/> is shown to the user and is not an identity.
/// </remarks>
/// <param name="Host">Lower-cased host the gh commands are pinned to, either github.com or a GitHub Enterprise host.</param>
/// <param name="RepositoryId">GitHub's numeric repository id. It is re-verified immediately before each publication because
/// owner and name can be renamed or re-created, while the number cannot be reassigned.</param>
/// <param name="Owner">Repository owner login exactly as GitHub reports it.</param>
/// <param name="Name">Repository name exactly as GitHub reports it.</param>
/// <param name="CloneUrl">Clone URL GitHub reports, used when pull request objects must be downloaded.</param>
/// <param name="User">Login of the active, valid gh account at connect time, so a later switch is detectable.</param>
/// <param name="DefaultBranch">The repository's default branch, used as the initial stack base and as a stack boundary.</param>
public sealed record GitHubRepositoryContext(string Host, long RepositoryId, string Owner, string Name,
    string CloneUrl, string User, string DefaultBranch)
{
    /// <summary>The stable scope key of this repository/account pair, shared by draft storage, journals and cached refs.</summary>
    /// <remarks>The numeric repository id stands in for owner/name so a rename keeps identity, and the login keeps one
    /// account from reading another account's review history on a shared machine.</remarks>
    public string Identity => $"{Host}/{RepositoryId}/{User}";
    /// <summary>Human-readable repository and account label for titles and headers.</summary>
    public string Display => $"{Host}/{Owner}/{Name} · @{User}";
}
/// <summary>Server snapshot of a pull request's base/head refs and immutable commit IDs.</summary>
/// <remarks>
/// The two SHAs are the version identity of everything review-related: anchors, drafts and review submissions carry
/// them, and a publication is refused when the live pull request no longer matches. The repository ids keep a pull
/// request from a fork from being matched to a local layer that merely shares a branch name.
/// </remarks>
/// <param name="Number">The pull request number inside its repository, unique for the life of the repository.</param>
/// <param name="Title">Current server title, used as a layer and stack label.</param>
/// <param name="BaseRepositoryId">Numeric id of the repository the base branch lives in, which may differ for a fork.</param>
/// <param name="BaseRef">Base branch name as GitHub reports it, without a refs prefix or remote name.</param>
/// <param name="BaseSha">Commit id of the base branch at snapshot time.</param>
/// <param name="HeadRepositoryId">Numeric id of the repository the head branch lives in, matched against the opened repository.</param>
/// <param name="HeadRef">Head branch name as GitHub reports it.</param>
/// <param name="HeadSha">Commit id of the pull request head at snapshot time, the version a draft is anchored to.</param>
/// <param name="IsDraft">Whether GitHub marks the pull request as a draft; drafts are still included in snapshots because
/// they can form a stack.</param>
/// <param name="Url">Server page URL, shown in the review and discussion UI.</param>
public sealed record PullRequest(long Number, string Title, long BaseRepositoryId, string BaseRef, string BaseSha,
    long HeadRepositoryId, string HeadRef, string HeadSha, bool IsDraft, string Url)
{
    /// <summary>Lifecycle state of the pull request, defaulting to <c>Open</c> for freshly parsed snapshots.</summary>
    /// <remarks>A snapshot only returns open pull requests, so parsed snapshot entries are <c>Open</c>; the history graph sets
    /// <c>Closed</c> and <c>Merged</c> when it reads a historical pull request or retains a node that already had a terminal
    /// state, which keeps merged nodes readable after they leave the open list.</remarks>
    public string State { get; init; } = "Open";
    /// <summary>Short label combining the number and the current title.</summary>
    public string Label => $"#{Number} · {Title}";
}
/// <summary>One timestamped page-complete set of open pull requests used for stack discovery.</summary>
/// <param name="Context">The account and repository the pages were read with, so results are never reused across contexts.</param>
/// <param name="PullRequests">Every open pull request across all pages, including drafts.</param>
/// <param name="UpdatedAt">When the read completed: shown as the snapshot's freshness and recorded as the observation time
/// of the history versions that were just observed.</param>
public sealed record GitHubSnapshot(GitHubRepositoryContext Context, IReadOnlyList<PullRequest> PullRequests, DateTimeOffset UpdatedAt);
/// <summary>One discovered chain of pull requests from the branch nearest the base upward.</summary>
/// <param name="Id">Stable identifier of the chain, derived from its topmost pull request number.</param>
/// <param name="Name">Display name of the chain, taken from the topmost pull request title.</param>
/// <param name="Layers">Pull requests ordered base to head; each one's base ref is the previous one's head ref.</param>
/// <param name="SharedPrefix">True when at least one pull request also appears in another discovered chain, meaning the
/// chains fork from a common bottom layer.</param>
public sealed record DiscoveredStack(string Id, string Name, IReadOnlyList<PullRequest> Layers, bool SharedPrefix);
/// <summary>Result of grouping a snapshot into pull request chains, with the leftovers and the refusals.</summary>
/// <param name="Stacks">Chains of at least two pull requests, ordered as discovered.</param>
/// <param name="Other">Open pull requests that belong to no chain, shown separately instead of being forced into one.</param>
/// <param name="Warnings">Duplicate-parent, cycle and blocked-edge messages explaining why a pull request is absent.</param>
public sealed record DiscoveryResult(IReadOnlyList<DiscoveredStack> Stacks, IReadOnlyList<PullRequest> Other, IReadOnlyList<string> Warnings);
/// <summary>Persisted per-repository GitHub settings that decide origin resolution and stack boundaries.</summary>
public sealed class RepositoryPreferences
{
    /// <summary>Explicit host/owner/repo used instead of the origin remote, for aliases or non-URL remotes.</summary>
    public string? GitHubOverride { get; set; }
    /// <summary>Base branch names that terminate discovery, so a pull request into the trunk is treated as a stack bottom.</summary>
    /// <remarks>Also used as the boundary set consulted while grouping; without it a chain would continue past the
    /// default branch or a release branch into unrelated pull requests.</remarks>
    public List<string>? BoundaryBranches { get; set; }
}
/// <summary>GitHub comment target tied to the pull request version and a specific side and line range of its diff.</summary>
/// <remarks>
/// GitHub addresses review comments by commit id, path, line and side, never by the legacy positional offset, so both
/// SHAs travel with the anchor and are revalidated against the live pull request before anything is posted. A
/// single-line anchor leaves <see cref="StartLine"/> and <see cref="StartSide"/> null; a range sets them to the first
/// line and side, while <see cref="Line"/> always stays the last line.
/// </remarks>
/// <param name="PullRequest">Number of the pull request the diff belongs to.</param>
/// <param name="HeadSha">Head commit the selection was made against.</param>
/// <param name="BaseSha">Base commit the selection was made against, used to detect a rebased or retargeted pull request.</param>
/// <param name="Path">Repository-relative path of the changed file.</param>
/// <param name="Side"><c>LEFT</c> for a removed line, <c>RIGHT</c> for an added or unchanged line.</param>
/// <param name="Line">Last line of the selection, counted on its own side.</param>
/// <param name="StartLine">First line of a multi-line selection, or null for a single-line comment.</param>
/// <param name="StartSide">Side of <paramref name="StartLine"/>; null when the selection is a single line.</param>
public sealed record ReviewAnchor(long PullRequest, string HeadSha, string BaseSha, string Path, string Side,
    int Line, int? StartLine = null, string? StartSide = null)
{
    [JsonIgnore]
    public string LocationLabel => StartLine is int start && start != Line
        ? $"{Path}:{start}–{Line} · {Side}"
        : $"{Path}:{Line} · {Side}";
}
/// <summary>One ordinary PR conversation comment, separate from review decisions and code threads.</summary>
/// <param name="Id">Server id. For a thread comment this is the database id used as the reply target; for a conversation
/// comment it is the issue-comment id used for display.</param>
/// <param name="Author">Login of the comment author, or <c>deleted</c> when GitHub no longer reports an account.</param>
/// <param name="Body">Raw Markdown body, which may still contain Stacker's trailing publication marker.</param>
/// <param name="Url">Server page URL for opening the comment in a browser.</param>
/// <param name="CreatedAt">Server creation timestamp; parsed as the minimum value if GitHub omits it.</param>
public sealed record DiscussionComment(string Id, string Author, string Body, string Url, DateTimeOffset CreatedAt)
{
    /// <summary>The body as the review UI renders it, with Stacker's trailing publication marker removed.</summary>
    public string DisplayBody => ReviewText.Visible(Body);
}
/// <summary>One reviewer submission summary such as approved, commented, or requested changes.</summary>
/// <param name="Id">Server review id.</param>
/// <param name="Author">Login of the reviewer.</param>
/// <param name="Body">Review summary Markdown, often empty for an approval.</param>
/// <param name="State">GitHub review state such as <c>APPROVED</c>, <c>COMMENTED</c> or <c>CHANGES_REQUESTED</c>.</param>
/// <param name="SubmittedAt">Submission timestamp; null for a pending review.</param>
public sealed record ReviewSummary(string Id, string Author, string Body, string State, DateTimeOffset? SubmittedAt)
{
    /// <summary>The summary as the review UI renders it, with Stacker's trailing publication marker removed.</summary>
    public string DisplayBody => ReviewText.Visible(Body);
}
/// <summary>Strips Stacker's own publication marker from text that is about to be displayed.</summary>
internal static class ReviewText
{
    /// <summary>Returns the body without the trailing <c>&lt;!-- stacker:marker --&gt;</c> comment that marks a send.</summary>
    /// <remarks>
    /// The marker is 32 lowercase hex characters because it is a GUID in its compact form, appended after a blank line
    /// by the review view. Only a marker at the very end of the body is removed, so a marker quoted inside the comment
    /// text stays visible rather than being trimmed as internal state; a damaged marker is also left alone instead of
    /// guessing at a prefix.
    /// </remarks>
    /// <param name="body">Raw comment body, possibly with the trailing marker.</param>
    public static string Visible(string body) => System.Text.RegularExpressions.Regex.Replace(body, @"\s*<!-- stacker:[0-9a-f]{32} -->\s*$", "");
}
/// <summary>Code-review thread, which may be resolved, outdated, or anchored to a file or line range.</summary>
/// <param name="Id">GraphQL node id used for the resolve mutation.</param>
/// <param name="IsResolved">Whether the thread is resolved on the server.</param>
/// <param name="IsOutdated">Whether the thread refers to a diff version that has since changed. GitHub may then stop
/// reporting its line, in which case the anchor is null while the path remains.</param>
/// <param name="CanResolve">Whether the active viewer may change resolution in its current state, resolved or not.</param>
/// <param name="Anchor">Line anchor for a line thread; null for a file-level thread or an outdated thread without a line.</param>
/// <param name="Comments">Thread comments in server order, with the first comment starting the thread.</param>
/// <param name="FilePath">Server-reported path, kept even when the anchor is unavailable.</param>
/// <param name="IsFileLevel">True when GitHub reports the thread's subject as a file rather than a line.</param>
public sealed record ReviewThread(string Id, bool IsResolved, bool IsOutdated, bool CanResolve, ReviewAnchor? Anchor,
    IReadOnlyList<DiscussionComment> Comments, string? FilePath = null, bool IsFileLevel = false)
{
    /// <summary>The best available path: the server path, falling back to the anchor's path.</summary>
    public string? Path => FilePath ?? Anchor?.Path;
    /// <summary>One-line thread label with its location and resolution state, as shown in the thread list.</summary>
    public string Label => $"{Path ?? "Unknown file"}{(IsFileLevel ? " · File comment" : Anchor?.Line is { } line ? $":{line}" : " · Line comment")} · {(IsOutdated ? "Outdated" : IsResolved ? "Resolved" : "Open")}";
    /// <summary>All comments flattened into an author-prefixed transcript, separated by blank lines.</summary>
    public string Text => string.Join("\n\n", Comments.Select(c => $"@{c.Author}\n{c.Body}"));
}
/// <summary>Distinct collections of PR conversation, reviewer submissions, and code threads.</summary>
/// <remarks>The three are separate GitHub objects with separate actions; merging them into one list would make a reply,
/// a resolve and a review decision look alike in the UI.</remarks>
/// <param name="Comments">Conversation comments on the pull request.</param>
/// <param name="Reviews">Review submissions with their decisions and summaries.</param>
/// <param name="Threads">Code threads, including file-level and outdated ones.</param>
public sealed record ReviewDiscussion(IReadOnlyList<DiscussionComment> Comments, IReadOnlyList<ReviewSummary> Reviews, IReadOnlyList<ReviewThread> Threads);
/// <summary>One line comment the user has written but not yet submitted as part of a review.</summary>
/// <param name="Anchor">The exact pull request version and diff range the comment targets.</param>
/// <param name="Body">Raw Markdown body, including any suggestion fence.</param>
public sealed record DraftComment(ReviewAnchor Anchor, string Body)
{
    /// <summary>Local identity of the comment, used to match it across draft edits and to clear only what was published.</summary>
    /// <remarks>A GUID in compact form. Reanchoring replaces the anchor through a <c>with</c> expression, which keeps this
    /// id, so a submitted review can still tell which draft comment it consumed.</remarks>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}
/// <summary>Persisted local review work, including unsent text retained across navigation and transient failures.</summary>
/// <remarks>
/// This object is the durability boundary of the review screen. It stores the pull request version the text targets, one
/// buffer per destination, the draft comments and the publication evidence. Version fields are compared with the live
/// pull request before a send, and the pending fields hold the exact snapshot of what was sent so an acknowledgement
/// removes only that text and leaves later edits alone.
/// </remarks>
public sealed class ReviewDraft
{
    /// <summary>Revision of the stored draft document, used to save only over the version this session loaded.</summary>
    /// <remarks>Not serialized: the revision lives in the surrounding storage record, and a repository save with a stale
    /// value fails with <see cref="ProblemCodes.StoreStaleRevision"/> instead of overwriting another window's edits.</remarks>
    [System.Text.Json.Serialization.JsonIgnore] public long PersistenceRevision { get; set; }
    /// <summary>GitHub context identity (host, repository id and account) the draft belongs to.</summary>
    public string ContextIdentity { get; set; } = "";
    /// <summary>Pull request number the draft belongs to.</summary>
    public long PullRequest { get; set; }
    /// <summary>Head commit the draft text and comments are anchored to.</summary>
    public string HeadSha { get; set; } = "";
    /// <summary>Base commit the draft text and comments are anchored to.</summary>
    public string BaseSha { get; set; } = "";
    /// <summary>Review summary text, submitted with the review decision.</summary>
    public string Summary { get; set; } = "";
    /// <summary>Legacy pull-request conversation buffer, still mirrored so older drafts and readers keep working.</summary>
    public string Composer { get; set; } = "";
    /// <summary>Format version of the composer fields; zero marks a draft written before per-destination buffers existed.</summary>
    /// <remarks>On load, a zero version moves the old single buffer into <see cref="RecoveredText"/> and sets this field to
    /// one, so pre-upgrade text is preserved but never posted to a destination it was not written for.</remarks>
    public int EditorVersion { get; set; }
    /// <summary>Text recovered from a destination that no longer exists, offered for manual reuse.</summary>
    /// <remarks>A line selection whose anchor targets an older pull request version is moved here when the draft is attached
    /// to a diff section, because that anchor cannot be sent; the text stays available instead of being discarded.</remarks>
    public string RecoveredText { get; set; } = "";
    /// <summary>Unsent text per draft destination, keyed by <see cref="ComposerKey.Encoded"/>.</summary>
    /// <remarks>Separate buffers are what keep one tab, reply or line selection from overwriting another's draft text.</remarks>
    public Dictionary<string, string> ContextComposers { get; set; } = [];
    /// <summary>Destination the in-flight send was written in, cleared together with <see cref="PendingBody"/>.</summary>
    public string? PendingComposerKey { get; set; }
    /// <summary>Body text captured before the pending marker was appended, so acknowledgement does not erase text edited
    /// afterwards.</summary>
    public string? PendingBody { get; set; }
    /// <summary>Review summary captured for the in-flight review submission.</summary>
    public string? PendingSummary { get; set; }
    /// <summary>Draft comments captured for the in-flight review submission.</summary>
    public List<DraftComment>? PendingReviewComments { get; set; }
    /// <summary>Unsent file-comment text per changed file path.</summary>
    public Dictionary<string, string> FileComposers { get; set; } = [];
    /// <summary>File path of the in-flight file comment.</summary>
    public string? PendingFilePath { get; set; }
    /// <summary>Review decision submitted with the draft comments.</summary>
    /// <remarks>One of <c>COMMENT</c>, <c>APPROVE</c> or <c>REQUEST_CHANGES</c>; the GitHub writer refuses any other value
    /// instead of letting the API reject the whole review.</remarks>
    public string Decision { get; set; } = "COMMENT";
    /// <summary>Line comments added to the review but not yet submitted.</summary>
    public List<DraftComment> Comments { get; set; } = [];
    /// <summary>Encoded marker (publication kind and marker, or thread and target state) of a send whose outcome is unknown.</summary>
    /// <remarks>Non-null while sending stays blocked: it is what a reload can match against the GitHub thread, and it is
    /// cleared only when the outcome is resolved, because losing it would make a possibly published comment look unsent.</remarks>
    public string? PendingOperation { get; set; }
    /// <summary>The unfinished publication, or null when no send is pending.</summary>
    public PublicationOperation? Operation { get; set; }
    /// <summary>Completed publications kept as evidence, one entry per confirmed or rejected send.</summary>
    public List<PublicationOperation> PublicationHistory { get; set; } = [];
    /// <summary>Anchors that were replaced when draft comments were reattached to a new version.</summary>
    /// <remarks>The original anchors stay in this history so a reanchored comment can still be traced to the version the
    /// reviewer actually read.</remarks>
    public List<DraftComment> PreviousAnchors { get; set; } = [];
}
/// <summary>Read-only GitHub operations; all methods must use the context's explicit host and repository.</summary>
/// <remarks>
/// Failures are returned as <see cref="Result{T}"/> problems rather than thrown across the port, so the review view can
/// report a refusal such as a closed pull request or a changed account. Nothing here writes to GitHub.
/// </remarks>
public interface IGitHubReader
{
    /// <summary>Resolves the origin (or the explicit override), validates the active account, and verifies repository access.</summary>
    /// <remarks>A missing origin, an unparseable remote, a mismatched gh account or a repository id mismatch each fail with a
    /// message that tells the user what to fix; local stacks stay available when no origin exists.</remarks>
    /// <param name="root">Working tree whose origin remote is consulted when no override is supplied.</param>
    /// <param name="repositoryOverride">Explicit host/owner/repo, or null to resolve the origin remote.</param>
    /// <param name="ct">Cancels the identity and access reads.</param>
    /// <returns>The resolved context, or a failed result describing what the user must correct.</returns>
    Task<Result<GitHubRepositoryContext>> ConnectAsync(string root, string? repositoryOverride, CancellationToken ct = default);
    /// <summary>Reads every open pull request across all pages, retaining drafts because they can still form a stack.</summary>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="ct">Cancels the paged read.</param>
    /// <returns>The page-complete open set; a repository without pull requests is an empty list, not a failure.</returns>
    Task<Result<GitHubSnapshot>> SnapshotAsync(GitHubRepositoryContext context, CancellationToken ct = default);
    /// <summary>Reads one current pull request and refuses it when it is no longer open.</summary>
    /// <remarks>Used by the stale-snapshot check, so a merged or closed pull request cannot receive a review built for an
    /// older state.</remarks>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="number">Pull request number inside that repository.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The current pull request; a closed or merged one comes back as a failed result, which is how the stale check
    /// notices that publishing must stop.</returns>
    Task<Result<PullRequest>> PullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default);
    /// <summary>Loads conversation comments, review decisions, and code threads as separate collections.</summary>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="pr">Snapshot whose number is queried and whose base/head SHAs fill anchors for threads GitHub reports
    /// without a commit.</param>
    /// <param name="ct">Cancels the paged reads.</param>
    Task<Result<ReviewDiscussion>> DiscussionAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default);
    /// <summary>Verifies that every anchor still matches the live pull request and exists on its side of the server diff.</summary>
    /// <remarks>This is the last guard before a comment or review is posted: it rejects a moved head/base and a range whose
    /// lines GitHub no longer reports, which would otherwise attach the text to a different line or fail the request
    /// after the mutation was already dispatched.</remarks>
    /// <param name="context">Pinned host, repository and account to verify against.</param>
    /// <param name="pr">The live pull request every anchor must still match.</param>
    /// <param name="anchors">Anchors to verify; an empty list is accepted and checks nothing, which lets a resolve or
    /// conversation comment reuse the same validation call.</param>
    /// <param name="ct">Cancels the diff read.</param>
    /// <returns><see cref="Unit"/> when every anchor is publishable; otherwise the first anchor that is not.</returns>
    Task<Result<Unit>> ValidateAnchorsAsync(GitHubRepositoryContext context, PullRequest pr, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct = default);
    /// <summary>Lists the paths of the files GitHub currently shows in the pull request diff.</summary>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="pr">Pull request whose current diff is listed.</param>
    /// <param name="ct">Cancels the paged read.</param>
    /// <returns>Distinct repository-relative paths; a file comment is only offered for one of them.</returns>
    Task<Result<IReadOnlyList<string>>> ChangedFilePathsAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default);
    /// <summary>Requires a path to be part of the current server-side diff before a file comment is created.</summary>
    /// <param name="context">Pinned host, repository and account to read with.</param>
    /// <param name="pr">Pull request whose current diff must contain the path.</param>
    /// <param name="path">Repository-relative file path to check.</param>
    /// <param name="ct">Cancels the diff read.</param>
    Task<Result<Unit>> ValidateFileAsync(GitHubRepositoryContext context, PullRequest pr, string path, CancellationToken ct = default);
    /// <summary>Revalidates the active account and the numeric repository id immediately before a sensitive action.</summary>
    /// <param name="context">The context whose account and repository id are rechecked.</param>
    /// <param name="ct">Cancels the two verification reads.</param>
    Task<Result<Unit>> VerifyAccessAsync(GitHubRepositoryContext context, CancellationToken ct = default);
}
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
/// <summary>Creates and retargets the pull requests a stack layer needs; every call is explicit and revalidated by the caller.</summary>
/// <remarks>
/// Branches are sent by name because that is what GitHub knows: a remote-tracking parent is normalized before the call,
/// never passed as <c>origin/name</c>. The caller re-derives the plan from a live snapshot, so these calls are not
/// idempotent by themselves and are never retried automatically.
/// </remarks>
public interface IGitHubPullRequestWriter
{
    /// <summary>Opens a pull request for one layer, with the layer's parent as its base branch.</summary>
    /// <param name="context">Pinned host, repository and account to create the pull request with.</param>
    /// <param name="headBranch">Branch name GitHub knows for the layer head, without a refs prefix.</param>
    /// <param name="baseBranch">Branch name GitHub knows for the layer parent, without a refs prefix or remote name.</param>
    /// <param name="title">Pull request title; the caller derives it from the layer.</param>
    /// <param name="body">Initial description; empty when the caller has none.</param>
    /// <param name="draft">Whether GitHub should mark the new pull request as a draft.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The created pull request as GitHub reports it, with its assigned number.</returns>
    Task<Result<PullRequest>> CreatePullRequestAsync(GitHubRepositoryContext context, string headBranch, string baseBranch,
        string title, string body, bool draft, CancellationToken ct = default);
    /// <summary>Points an existing pull request at a different base branch after a layer's parent changed.</summary>
    /// <param name="context">Pinned host, repository and account to write with.</param>
    /// <param name="number">Pull request number to retarget.</param>
    /// <param name="baseBranch">New base branch name GitHub knows, without a refs prefix.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The updated pull request as GitHub reports it.</returns>
    Task<Result<PullRequest>> UpdatePullRequestBaseAsync(GitHubRepositoryContext context, long number, string baseBranch, CancellationToken ct = default);
}
/// <summary>Where downloaded pull request objects live and how much space they occupy.</summary>
/// <param name="CommonDirectory">The repository's shared Git directory, shown so the user knows which checkout stores the objects.</param>
/// <param name="SizeBytes">Loose, packed and garbage object bytes reported by Git, in bytes, for display only. The application
/// never deletes objects to reduce it; Git owns that storage.</param>
public sealed record RepositoryObjectStorage(string CommonDirectory, long SizeBytes);
/// <summary>Ensures selected PR objects exist in the opened repository using its configured Git transport.</summary>
public interface IPullRequestObjects
{
    /// <summary>Downloads the commits of the selected pull requests that are missing, then verifies and pins them.</summary>
    /// <remarks>Uses the repository's configured origin, so SSH, credentials, proxy and TLS settings behave as they do for
    /// the user's own Git. A pull request whose head or base moved while downloading fails instead of being compared
    /// against a mix of versions; the caller must refresh GitHub and try again.</remarks>
    /// <param name="repository">Local repository identity whose object database receives the commits.</param>
    /// <param name="context">GitHub context used to validate the origin before downloading.</param>
    /// <param name="prs">Selected pull request snapshots whose base and head commits must exist locally.</param>
    /// <param name="ct">Cancels the download.</param>
    Task<Result<Unit>> EnsureAsync(RepositoryIdentity repository, GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default);
    /// <summary>Acquires a shared read lease so display reads can finish before object references are removed.</summary>
    /// <param name="repository">Local repository identity whose objects are read.</param>
    /// <param name="ct">Cancels the wait and releases the lease request.</param>
    /// <returns>A lease held until it is disposed; clearing object references waits for every outstanding lease.</returns>
    Task<Result<ObjectSnapshotLease>> AcquireAsync(RepositoryIdentity repository, CancellationToken ct = default);
    /// <summary>Deletes only the application-owned refs that keep the selected pull request objects reachable.</summary>
    /// <remarks>Removal waits for outstanding read leases and never deletes the repository directory, object files, user
    /// branches, remote-tracking branches or recovery refs, and never forces a garbage collection; an interrupted clear
    /// is retried by running it again.</remarks>
    /// <param name="repository">Local repository identity whose refs are cleared.</param>
    /// <param name="context">GitHub context that scopes which application refs belong to this download.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<Result<Unit>> ClearAsync(RepositoryIdentity repository, GitHubRepositoryContext context, CancellationToken ct = default);
    /// <summary>Reports the shared Git directory and the object bytes Git currently accounts for.</summary>
    /// <param name="repository">Local repository identity whose storage is inspected.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The shared Git directory and the object bytes Git accounts for.</returns>
    Task<Result<RepositoryObjectStorage>> InspectAsync(RepositoryIdentity repository, CancellationToken ct = default);
}
/// <summary>One syntax-colored character range in a source line.</summary>
/// <param name="Start">Zero-based character offset of the span inside its line.</param>
/// <param name="Length">Number of characters the span covers; a zero-length range carries no styling and the shipped
/// highlighter does not emit one.</param>
/// <param name="Color">Foreground color string supplied by the active theme.</param>
/// <param name="Italic">Optional italic styling; the shipped highlighter leaves it at its default.</param>
public sealed record SyntaxSpan(int Start, int Length, string Color, bool Italic = false);
/// <summary>Optional syntax coloring service; callers can render plain text while tokenization is pending or unavailable.</summary>
public interface ISyntaxHighlighter
{
    /// <summary>Tokenizes one file version and returns spans per one-based line number.</summary>
    /// <remarks>Highlighting is optional: the shipped implementation answers with an empty map, not a problem, when the
    /// extension has no grammar, the file exceeds its size or line bound, or tokenization runs past its time budget, so
    /// every caller must be able to render the plain text. Only a genuine adapter failure is returned as a problem.</remarks>
    /// <param name="blobId">Blob object id, used as the content identity so two revisions of the same bytes share work.</param>
    /// <param name="path">File path whose extension selects the grammar; unknown extensions yield no spans.</param>
    /// <param name="text">File content to tokenize.</param>
    /// <param name="theme">Theme name selecting the color palette.</param>
    /// <param name="ct">Cancels tokenization; cancellation is rethrown rather than reported as a problem.</param>
    /// <returns>Spans keyed by one-based line number; a line missing from the map is rendered as plain text.</returns>
    Task<Result<IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>>> HighlightAsync(string blobId, string path, string text, string theme, CancellationToken ct = default);
}
/// <summary>Content of one Git blob, together with the object id used as its content identity in caches.</summary>
/// <param name="Id">Blob object id, used as the cache key so two revisions of the same text are not re-tokenized.</param>
/// <param name="Text">Decoded content. A missing path or a blob past the display limit is reported as no content instead of
/// a partial or truncated text.</param>
public sealed record BlobContent(string Id, string Text);
