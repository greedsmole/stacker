using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Persistent identity of a repository, shared by every worktree that resolves the same Git common directory.</summary>
/// <param name="Value">Opaque identifier. A registered repository stores a 32-character GUID; identities derived by a
/// reader use the first 32 characters of a <see cref="StableIdentity"/> digest.</param>
/// <remarks>Value equality only — the string is compared verbatim, so an identifier read from storage must be handed
/// back unchanged. The identity, not the folder path, keys history, drafts and workspace state; that is what lets a
/// moved repository keep its past and keeps two unrelated clones from merging. There is no parsing or normalisation,
/// so an empty or malformed value is accepted here and rejected only where it is looked up.</remarks>
public readonly record struct RepositoryId(string Value);
/// <summary>Persistent identity of one physical working tree inside a repository.</summary>
/// <param name="Value">Opaque identifier, 32 characters, generated when the location is first registered.</param>
/// <remarks>Linked worktrees of one repository share a <see cref="RepositoryId"/> but not a <see cref="WorktreeId"/>.
/// The workspace key is built from this value, so per-worktree view state follows the working tree instead of the
/// folder path and survives a move.</remarks>
public readonly record struct WorktreeId(string Value);
/// <summary>Opaque identifier for the person or automation attributed with an action.</summary>
/// <param name="Value">Caller-supplied identifier, compared verbatim and never normalised.</param>
/// <remarks>Declared as the attribution token for ports that need one; nothing in <c>src</c> currently constructs or
/// reads it, so it carries no behaviour of its own.</remarks>
public readonly record struct ActorId(string Value);
/// <summary>Identity of one textual comparison between a basis commit and a head commit, including the comparison policy.</summary>
/// <param name="Value">Digest produced by <see cref="ComparisonId.For"/>: 64 lowercase hexadecimal characters.</param>
/// <remarks>The value is content-derived, so the same change, commits and policy always produce the same id. A caller
/// that stores or compares a comparison identity passes <see cref="Value"/> rather than re-deriving it from display
/// text. The session compares selections by value, so a changed id counts as a selection change and invalidates query
/// tickets issued before it.</remarks>
public readonly record struct ComparisonId(string Value)
{
    /// <summary>Derives the comparison id from the change, both commit ids and the policy token.</summary>
    /// <param name="change">Change node id the comparison belongs to.</param>
    /// <param name="basis">Merge-base commit id used as the old side of the comparison.</param>
    /// <param name="head">Head commit id used as the new side.</param>
    /// <param name="policy">Comparison policy token. It participates in the digest, so a future change to how
    /// comparisons are produced yields new ids instead of reusing ids stored for the old policy.</param>
    /// <returns>The derived comparison id.</returns>
    /// <remarks>The default <c>unified-v1</c> names the current textual unified-diff policy and is the only value the
    /// code uses; nothing else reads the string. Why that exact label was chosen is not recorded.</remarks>
    public static ComparisonId For(string change, string basis, string head, string policy = "unified-v1") => new(StableIdentity.For(change, basis, head, policy));
}
/// <summary>Derives stable, content-addressed identifiers from the values that define a thing.</summary>
/// <remarks>Used for change nodes, observed versions, comparison identities and cache keys. Stored history, checkpoints
/// and drafts are looked up by these ids, so the same inputs must produce the same value on every run and every
/// machine; a random or time-dependent value would reopen stored data under a new identity.</remarks>
public static class StableIdentity
{
    /// <summary>Returns the lowercase hexadecimal SHA-256 digest of the values joined by a NUL separator.</summary>
    /// <param name="values">Fields that identify the thing, in a fixed order; changing the order changes the id.</param>
    /// <returns>64 lowercase hexadecimal characters: the full 32-byte SHA-256 digest, untruncated and unsalted.</returns>
    /// <remarks>Fields are joined with U+0000 before UTF-8 encoding. NUL cannot occur in a Git ref or a path, so two
    /// field lists cannot be made to produce the same joined text by shifting characters across a field boundary.
    /// The digest is not keyed: equal inputs anywhere in the process tree produce equal ids, and an empty list hashes
    /// the empty string.</remarks>
    public static string For(params string[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', values)))).ToLowerInvariant();
}
/// <summary>Resolved identity of an opened repository and worktree, together with the paths Git resolved for them.</summary>
/// <param name="Repository">Repository identity, shared by all of its worktrees.</param>
/// <param name="Worktree">Identity of this physical working tree.</param>
/// <param name="Root">Absolute working-tree root Git reported for the opened folder.</param>
/// <param name="CommonDirectory">Absolute Git common directory; linked worktrees of one repository share it.</param>
/// <remarks>Registration resolves <c>Root</c> and <c>CommonDirectory</c> from Git rather than trusting the path the
/// user typed, so a folder inside the tree resolves to the repository whose identity owns it. Relocation reuses both
/// identifiers with a new root, which is why history and workspace state survive a move.</remarks>
public sealed record RepositoryIdentity(RepositoryId Repository, WorktreeId Worktree, string Root, string CommonDirectory)
{
    /// <summary>History scope for this repository: the <c>local:</c> prefix followed by the repository id.</summary>
    /// <remarks>Derived from <see cref="RepositoryId"/>, not from <see cref="Root"/>, so moving or renaming the folder
    /// keeps the scope and its stored history. All worktrees of one repository therefore share one history scope,
    /// while workspace state keys use <see cref="WorktreeId"/> instead.</remarks>
    public string Scope => "local:" + Repository.Value;
}
/// <summary>Resolves and persists repository and worktree identities, and binds a local change to its GitHub counterpart.</summary>
/// <remarks>Identity is deliberately independent of folder paths: registration resolves the Git common directory and
/// working-tree root, relocation preserves both ids, and a binding requires the verified account plus a matching local
/// ref. Every guard exists so a second clone or a second account cannot inherit another history by accident. Methods
/// return their outcome instead of throwing; cancellation is the only expected exception.</remarks>
public interface IChangeIdentityService
{
    /// <summary>Returns every registered repository location, ordered by root path.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>An empty list when nothing was ever registered, otherwise one entry per registered worktree; a failed
    /// result carries the store's read code.</returns>
    Task<Result<IReadOnlyList<RepositoryIdentity>>> KnownAsync(CancellationToken ct = default);
    /// <summary>Registers the repository that contains <paramref name="root"/> and returns its identity.</summary>
    /// <param name="root">Folder inside the repository or worktree to register; Git resolves the real root.</param>
    /// <param name="ct">Cancels the Git resolution and the database write.</param>
    /// <returns>The already registered identity when the common directory or root was seen before, otherwise a newly
    /// created one.</returns>
    /// <remarks>Fails instead of guessing when Git cannot resolve the location unambiguously, for example outside a
    /// repository or with an unexpected <c>rev-parse</c> layout. It never hands an existing identity to a different
    /// repository, which is the guard that keeps a second clone from merging into the first one's history.</remarks>
    Task<Result<RepositoryIdentity>> RegisterAsync(string root, CancellationToken ct = default);
    /// <summary>Associates a new root with an existing repository identity, keeping the repository and worktree ids.</summary>
    /// <param name="repository">Identity the caller already holds, typically from <see cref="KnownAsync"/>.</param>
    /// <param name="root">New location of that repository or worktree.</param>
    /// <param name="worktree">Worktree to keep, when the caller wants to pin a specific worktree instead of letting the
    /// location resolve one.</param>
    /// <param name="ct">Cancels the Git resolution and the database write.</param>
    /// <returns>The identity with the new root and the same ids.</returns>
    /// <remarks>Refuses when the target common directory already belongs to a different identity, when the requested
    /// repository id is unknown, when the requested worktree id is not registered for that repository, or when the root
    /// is already a different worktree. Each refusal keeps the stored history untouched rather than merging it. This is
    /// the explicit path for a moved folder; plain registration of an unrelated clone never merges.</remarks>
    Task<Result<RepositoryIdentity>> RelocateAsync(RepositoryId repository, string root, WorktreeId? worktree = null, CancellationToken ct = default);
    /// <summary>Binds one local change reference to a GitHub repository and pull request, and aliases the two change nodes.</summary>
    /// <param name="localScope">Local history scope that owns the local change, for example
    /// <see cref="RepositoryIdentity.Scope"/>.</param>
    /// <param name="localRef">Local branch ref, which must match the pull request's head ref.</param>
    /// <param name="context">Verified GitHub context of the active account and repository.</param>
    /// <param name="pr">Pull request snapshot to bind.</param>
    /// <param name="ct">Cancels the database write.</param>
    /// <returns>A successful empty result, or a failed result when either guard rejects the binding.</returns>
    /// <remarks>Guards: the pull request head must come from the same repository the context names, and the local ref
    /// must be <c>refs/heads/</c> plus the pull request's head branch; and the local scope must not already be bound to
    /// another account. On success the local change node is aliased to the remote one, so checkpoints and versions
    /// recorded locally are visible from the pull request review and the other way round.</remarks>
    Task<Result<Unit>> BindAsync(string localScope, string localRef, GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default);
}
/// <summary>One recorded observation: which version a change pointed at, when it was seen and in which order.</summary>
/// <param name="VersionId">Id of the observed <see cref="ChangeVersion"/>.</param>
/// <param name="SeenAt">Moment the version was observed.</param>
/// <param name="Sequence">Strictly increasing ordering value, assigned by the store when the observation row is appended.</param>
/// <remarks>Mirrors a row of the observation log. No code in <c>src</c> currently constructs or reads this type: the
/// store appends observation rows while saving versions and reads only the newest one per change.</remarks>
public sealed record ChangeObservation(string VersionId, DateTimeOffset SeenAt, long Sequence);
/// <summary>One atomic metadata observation: the graph, the versions it references and the revision the caller read.</summary>
/// <param name="Graph">Graph snapshot to persist; its node membership and version ids define the stored graph.</param>
/// <param name="Versions">Versions saved in the same transaction, so the graph never points at a version that was not
/// stored.</param>
/// <param name="ExpectedRevision">Revision of the graph the caller based this observation on, or 0 when the scope has
/// no graph yet.</param>
/// <remarks>The revision guard turns a stale observation into a failed result with the store's stale-revision code
/// instead of overwriting a newer graph, for example when a background poll finishes after the user changed the stack.
/// A batch whose graph repeats the stored one, apart from its timestamp and revision, is a no-op that returns the
/// stored revision, so repeated polls do not create a revision per poll.</remarks>
public sealed record HistoryObservationBatch(GraphSnapshot Graph, IReadOnlyList<ChangeVersion> Versions, long ExpectedRevision);
/// <summary>A stored review draft together with the revision that a later save must match.</summary>
/// <param name="Draft">Draft payload as persisted.</param>
/// <param name="Revision">Revision the draft was read at; passing it back to the save call is what detects a concurrent
/// write from another window.</param>
public sealed record RevisionedDraft(ReviewDraft Draft, long Revision);
/// <summary>Persists review drafts with an explicit revision, so two windows cannot silently interleave saves.</summary>
/// <remarks>A read returns a null draft for a key that was never saved rather than an empty payload. A write compares
/// the expected revision inside one transaction; a mismatch fails and leaves the stored draft untouched, and the caller
/// keeps its own text in the window instead of losing it.</remarks>
public interface IReviewDraftRepository
{
    /// <summary>Reads the draft stored under <paramref name="key"/>.</summary>
    /// <param name="key">Draft key, usually one review context.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The draft and its revision, or a null value when nothing is stored yet.</returns>
    Task<Result<RevisionedDraft?>> ReadDraftAsync(string key, CancellationToken ct = default);
    /// <summary>Saves a draft only when the stored revision still matches the revision the caller read.</summary>
    /// <param name="key">Draft key.</param>
    /// <param name="draft">Complete payload to store.</param>
    /// <param name="expectedRevision">Revision the caller read, or 0 to insert a draft that does not exist yet.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>The revision the payload was stored as: the expected revision plus one.</returns>
    /// <remarks>A first save inserts instead of replacing, so if another window inserted the same key in the meantime
    /// the insert affects no row and the caller gets the stale-revision failure rather than overwriting that draft.
    /// The failure is how the UI can say that text is kept in this window and must be copied before reloading.</remarks>
    Task<Result<long>> SaveDraftAsync(string key, ReviewDraft draft, long expectedRevision, CancellationToken ct = default);
}
