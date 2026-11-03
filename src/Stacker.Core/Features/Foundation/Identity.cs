using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

public readonly record struct RepositoryId(string Value);
public readonly record struct WorktreeId(string Value);
public readonly record struct ActorId(string Value);
public readonly record struct ComparisonId(string Value)
{
    public static ComparisonId For(string change, string basis, string head, string policy = "unified-v1") => new(StableIdentity.For(change, basis, head, policy));
}
public static class StableIdentity
{
    public static string For(params string[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', values)))).ToLowerInvariant();
}
public sealed record RepositoryIdentity(RepositoryId Repository, WorktreeId Worktree, string Root, string CommonDirectory)
{ public string Scope => "local:" + Repository.Value; }
public interface IChangeIdentityService
{
    Task<Result<IReadOnlyList<RepositoryIdentity>>> KnownAsync(CancellationToken ct = default);
    Task<Result<RepositoryIdentity>> RegisterAsync(string root, CancellationToken ct = default);
    Task<Result<RepositoryIdentity>> RelocateAsync(RepositoryId repository, string root, WorktreeId? worktree = null, CancellationToken ct = default);
    Task<Result<Unit>> BindAsync(string localScope, string localRef, GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default);
}
public sealed record ChangeObservation(string VersionId, DateTimeOffset SeenAt, long Sequence);
public sealed record HistoryObservationBatch(GraphSnapshot Graph, IReadOnlyList<ChangeVersion> Versions, long ExpectedRevision);
public sealed record RevisionedDraft(ReviewDraft Draft, long Revision);
public interface IReviewDraftRepository
{
    Task<Result<RevisionedDraft?>> ReadDraftAsync(string key, CancellationToken ct = default);
    Task<Result<long>> SaveDraftAsync(string key, ReviewDraft draft, long expectedRevision, CancellationToken ct = default);
}
