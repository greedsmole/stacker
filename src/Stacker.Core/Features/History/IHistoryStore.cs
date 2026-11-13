using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Persistence port for change identity, immutable versions, graph revisions, checkpoints and blocking concerns.</summary>
/// <remarks>History is append-only: versions never change, graphs accumulate revisions, and the only deletions are a
/// withdrawn checkpoint or concern. Local nodes are looked up by scope and full ref, remote nodes by scope and pull request
/// number, so the same number in another repository or account cannot collide with this history. Expected failures are
/// returned as failed results — for example a stale revision — rather than thrown at the caller.</remarks>
public interface IHistoryStore
{
    /// <summary>Looks up a change without creating one, so repeated observations do not invent identities.</summary>
    /// <param name="scope">Repository scope that owns the history.</param>
    /// <param name="reference">Full ref used as the lookup key for a local node.</param>
    /// <param name="pr">Pull request number; when present it, rather than the reference, is the lookup key.</param>
    /// <param name="ct">Cancellation for the read.</param>
    /// <returns>The stored node, or <see langword="null"/> when this scope has no history for that key yet.</returns>
    Task<Result<ChangeNode?>> FindNodeAsync(string scope, string reference, long? pr = null, CancellationToken ct = default);
    /// <summary>Reads the newest graph revision, or <see langword="null"/> before the first observation.</summary>
    /// <remarks>Callers use it to obtain the prior revision for the next write and to carry earlier version pins and
    /// explicit edges forward into a new observation.</remarks>
    /// <param name="scope">Repository scope that owns the history.</param>
    /// <param name="ct">Cancellation for the read.</param>
    /// <returns>The latest revision, or <see langword="null"/> for a scope that has never been observed.</returns>
    Task<Result<GraphSnapshot?>> LatestGraphAsync(string scope, CancellationToken ct = default);
    /// <summary>Writes the nodes, the observed versions and one new graph revision as a single batch.</summary>
    /// <remarks>The batch is atomic: either the whole observation lands or none of it does. The expected revision makes
    /// this a compare-and-swap, so a concurrent observation is refused (the caller must reload) instead of being silently
    /// overwritten, and an unchanged graph may be coalesced onto the previous revision rather than gaining a new one.</remarks>
    /// <param name="batch">Graph revision, versions to persist and the revision the caller based its work on.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>The saved revision, with the store-assigned revision number.</returns>
    Task<Result<GraphSnapshot>> ApplyObservationBatchAsync(HistoryObservationBatch batch, CancellationToken ct = default);
    /// <summary>Finds an existing node for the key or creates one, refreshing its title and reference.</summary>
    /// <remarks>Used when a viewed comparison is captured into history and the change may never have been observed.
    /// A created node receives a fresh unique identity; an existing node keeps the identity it already has.</remarks>
    /// <param name="scope">Repository scope that owns the history.</param>
    /// <param name="reference">Full ref, or head ref for a remote change.</param>
    /// <param name="title">Display title to store on the node.</param>
    /// <param name="pr">Pull request number, or <see langword="null"/> for a local branch.</param>
    /// <param name="ct">Cancellation for the read and write.</param>
    /// <returns>The existing or newly created node.</returns>
    Task<Result<ChangeNode>> ResolveNodeAsync(string scope, string reference, string title, long? pr = null, CancellationToken ct = default);
    /// <summary>Upserts one node without adding a graph revision, for callers that only change metadata.</summary>
    /// <param name="node">Node to store; its <see cref="ChangeNode.Id"/> must already be the stable identity.</param>
    /// <param name="ct">Cancellation for the write.</param>
    Task<Result<Unit>> SaveNodeAsync(ChangeNode node, CancellationToken ct = default);
    /// <summary>Moves a local node's identity onto a different ref, so a renamed branch keeps its history.</summary>
    /// <remarks>The guard set is the contract: a pull request node cannot be rebound because its identity is the number, the
    /// scope must be local, the target must be a full ref without control characters, and the ref must not already own a
    /// different history — two histories are never merged implicitly, and a rename performed in another session is reported
    /// as a failure instead of overwriting.</remarks>
    /// <param name="node">Existing local node to move.</param>
    /// <param name="reference">Full ref that should become the node's new reference.</param>
    /// <param name="ct">Cancellation for the write.</param>
    Task<Result<Unit>> RebindLocalNodeAsync(ChangeNode node, string reference, CancellationToken ct = default);
    /// <summary>Records one immutable version and returns the stored form, including its merge-base evidence.</summary>
    /// <remarks>Observing the same change/base/head triple again is idempotent: the existing version is returned. A version is
    /// first stored without merge-base evidence and a later observation of the same triple attaches it; contradicting
    /// evidence is rejected rather than applied, because a version's ancestor cannot be both things.</remarks>
    /// <param name="version">Observed version. Its identity is recomputed from the change and both commits.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>The stored version, carrying the merge base once it is known.</returns>
    Task<Result<ChangeVersion>> ObserveAsync(ChangeVersion version, CancellationToken ct = default);
    /// <summary>Every stored version of a change, oldest observation first.</summary>
    /// <remarks>Includes versions recorded under identities the store knows are aliases of this change — a local/pull
    /// request binding or a migrated scope — and reports each with <see cref="ChangeVersion.ChangeId"/> set to the requested
    /// change, so both views see one history.</remarks>
    /// <param name="changeId">Change whose versions are wanted.</param>
    /// <param name="ct">Cancellation for the read.</param>
    /// <returns>Versions ordered by observation time; empty when nothing was ever observed.</returns>
    Task<Result<IReadOnlyList<ChangeVersion>>> VersionsAsync(string changeId, CancellationToken ct = default);
    /// <summary>Writes a new graph revision as supplied, for explicit dependency edits that bypass discovery.</summary>
    /// <param name="graph">Revision to store; its revision field is the expected prior revision when it is non-zero.</param>
    /// <param name="ct">Cancellation for the write.</param>
    Task<Result<Unit>> SaveGraphAsync(GraphSnapshot graph, CancellationToken ct = default);
    /// <summary>All retained revisions of a scope, oldest first, for the read-only historical browser.</summary>
    /// <param name="scope">Repository scope that owns the history.</param>
    /// <param name="ct">Cancellation for the read.</param>
    Task<Result<IReadOnlyList<GraphSnapshot>>> GraphsAsync(string scope, CancellationToken ct = default);
    /// <summary>Newest checkpoint per path for a change, one entry per path.</summary>
    /// <param name="changeId">Change whose markers are wanted.</param>
    /// <param name="ct">Cancellation for the read.</param>
    Task<Result<IReadOnlyList<ReviewCheckpoint>>> CheckpointsAsync(string changeId, CancellationToken ct = default);
    /// <summary>Blocking concerns for a change, one entry per path with the recorded texts merged.</summary>
    /// <param name="changeId">Change whose concerns are wanted.</param>
    /// <param name="ct">Cancellation for the read.</param>
    Task<Result<IReadOnlyList<ReviewConcern>>> ConcernsAsync(string changeId, CancellationToken ct = default);
    /// <summary>Adds or withdraws the blocking question for one path.</summary>
    /// <param name="concern">Concern to store; <see cref="ReviewConcern.Path"/> selects the slot and
    /// <see cref="ReviewConcern.Text"/> is the question.</param>
    /// <param name="remove"><see langword="true"/> deletes the stored concern for that path instead of writing text.</param>
    /// <param name="ct">Cancellation for the write.</param>
    Task<Result<Unit>> SetConcernAsync(ReviewConcern concern, bool remove, CancellationToken ct = default);
    /// <summary>Adds or removes the reviewed marker for one path.</summary>
    /// <param name="checkpoint">Marker to store; <see cref="ReviewCheckpoint.Path"/> also identifies the marker to remove,
    /// so the version and timestamp are ignored for a removal.</param>
    /// <param name="remove"><see langword="true"/> deletes the marker for that path instead of writing it.</param>
    /// <param name="ct">Cancellation for the write.</param>
    Task<Result<Unit>> SetCheckpointAsync(ReviewCheckpoint checkpoint, bool remove, CancellationToken ct = default);
}
