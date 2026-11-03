using Stacker.Core.Framework;
namespace Stacker.Core;

public sealed record ChangeNode(string Id, string Scope, string Title, string Reference, long? PullRequest = null,
    string State = "Open")
{ public override string ToString() => Title; }
public sealed record ChangeVersion(string Id, string ChangeId, string BaseSha, string HeadSha, string? MergeBase,
    DateTimeOffset ObservedAt)
{
    public string Label => $"{ObservedAt.LocalDateTime:g} · {HeadSha[..Math.Min(8, HeadSha.Length)]}";
    public override string ToString() => Label;
}
public sealed record DependencyEdge(string Parent, string Child, string Source);
public sealed record GraphSnapshot(string Scope, DateTimeOffset ObservedAt, IReadOnlyList<ChangeNode> Nodes,
    IReadOnlyList<DependencyEdge> Edges, IReadOnlyList<string> Warnings)
{
    public long Revision { get; init; }
    public IReadOnlyDictionary<string, string> Versions { get; init; } = new Dictionary<string, string>();
    public override string ToString() => ObservedAt.LocalDateTime.ToString("g");
}
public sealed record ReviewCheckpoint(string ChangeId, string Path, string VersionId, DateTimeOffset ReviewedAt);
public sealed record ReviewConcern(string ChangeId, string Path, string Text);
public enum FileReviewState { Unreviewed, Reviewed, CodeChanged, BaseChanged, MetadataChanged, Unavailable }
public sealed record FileReviewStatus(FileChange Change, FileReviewState State, ChangeVersion? CheckedVersion)
{
    public string? Concern { get; init; }
    public string Label => $"{Change.DisplayPath} · {State}" + (Concern is null ? "" : " · BLOCKER: " + Concern);
}
public sealed record VersionComparison(ChangeVersion Previous, ChangeVersion Current, string? ExpectedTree,
    IReadOnlyList<FileChange> CodeChanges, IReadOnlyList<FileChange> BaseChanges, string? Unavailable)
{
    public bool IsReliable => Unavailable is null;
}
public sealed record StackAnalysisIssue(string ChangeId, string Kind, string Message, IReadOnlyList<string> Paths);
public sealed record StackAnalysisResult(GraphSnapshot Graph, IReadOnlyList<StackAnalysisIssue> Issues);
public sealed record FileEvolutionStep(ChangeNode Change, ChangeVersion Version, FileChange? File, bool Continuous);
public sealed record FileEvolution(IReadOnlyList<FileEvolutionStep> Steps);
public sealed record AnchorProposal(ReviewAnchor Original, ReviewAnchor Proposed, string OldContext, string NewContext);

public interface IHistoryRemoteReader
{
    Task<Result<PullRequest>> HistoricalPullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default);
}
public interface IHistoryStore
{
    Task<Result<ChangeNode?>> FindNodeAsync(string scope, string reference, long? pr = null, CancellationToken ct = default);
    Task<Result<GraphSnapshot?>> LatestGraphAsync(string scope, CancellationToken ct = default);
    Task<Result<GraphSnapshot>> ApplyObservationBatchAsync(HistoryObservationBatch batch, CancellationToken ct = default);
    Task<Result<ChangeNode>> ResolveNodeAsync(string scope, string reference, string title, long? pr = null, CancellationToken ct = default);
    Task<Result<Unit>> SaveNodeAsync(ChangeNode node, CancellationToken ct = default);
    Task<Result<Unit>> RebindLocalNodeAsync(ChangeNode node, string reference, CancellationToken ct = default);
    Task<Result<ChangeVersion>> ObserveAsync(ChangeVersion version, CancellationToken ct = default);
    Task<Result<IReadOnlyList<ChangeVersion>>> VersionsAsync(string changeId, CancellationToken ct = default);
    Task<Result<Unit>> SaveGraphAsync(GraphSnapshot graph, CancellationToken ct = default);
    Task<Result<IReadOnlyList<GraphSnapshot>>> GraphsAsync(string scope, CancellationToken ct = default);
    Task<Result<IReadOnlyList<ReviewCheckpoint>>> CheckpointsAsync(string changeId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<ReviewConcern>>> ConcernsAsync(string changeId, CancellationToken ct = default);
    Task<Result<Unit>> SetConcernAsync(ReviewConcern concern, bool remove, CancellationToken ct = default);
    Task<Result<Unit>> SetCheckpointAsync(ReviewCheckpoint checkpoint, bool remove, CancellationToken ct = default);
}
/// <summary>All writes, including object import and merge simulation, occur in an application-owned bare repository.</summary>
public interface IHistoryGit
{
    Task<Result<ContentAvailability>> AvailabilityAsync(string scope, CancellationToken ct = default);
    Task<Result<string>> CaptureAsync(string scope, string source, IReadOnlyList<string> commits, CancellationToken ct = default);
    Task<Result<ObjectSnapshotLease>> AcquireAsync(string scope, CancellationToken ct = default);
    Task<Result<Unit>> ForgetContentsAsync(string scope, CancellationToken ct = default);
    Task<Result<string>> RootAsync(string scope, CancellationToken ct = default);
    Task<Result<string?>> TransplantAsync(string root, string oldBase, string oldHead, string newBase, CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> MergeAsync(string root, string parent, string head, CancellationToken ct = default);
}
