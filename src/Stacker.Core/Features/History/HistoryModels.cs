using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One change under review — a local branch or a pull request — whose identity outlives the ref it currently uses.</summary>
/// <remarks>Versions, edges, checkpoints and concerns reference <see cref="Id"/>, so renaming a branch or moving a ref never
/// forks the history. <see cref="Reference"/> is only how the next observation finds a head SHA, and <see cref="State"/> is
/// the last observed state rather than a claim about the remote at this moment. Remote nodes carry a pull request number
/// and are looked up by it; local nodes are looked up by their full ref.</remarks>
/// <param name="Id">Durable identity referenced by version pins and edges; created once and preserved across renames.</param>
/// <param name="Scope">Repository scope owning the node: <c>local:&lt;repository-id&gt;</c> or
/// <c>&lt;host&gt;/&lt;repository-id&gt;/&lt;user&gt;</c>, so two accounts or two clones never share a node by accident.</param>
/// <param name="Title">Display name refreshed from the branch name or pull request title on every observation.</param>
/// <param name="Reference">Moveable full ref used to resolve the layer's current head; never part of the identity.</param>
/// <param name="PullRequest">Pull request number for a remote change, <see langword="null"/> for a local branch.</param>
/// <param name="State">Last observed state: <c>Open</c> or <c>Missing</c> locally; <c>Open</c>, <c>Merged</c> or
/// <c>Closed</c> from GitHub; <c>Unknown</c> when a historical lookup failed. A remote node that leaves the open snapshot is
/// retained with its final or re-read state instead of disappearing.</param>
public sealed record ChangeNode(string Id, string Scope, string Title, string Reference, long? PullRequest = null,
    string State = "Open")
{
    /// <summary>Renders the title, so list and combo bindings show the change rather than the record's fields.</summary>
    public override string ToString() => Title;
}
/// <summary>An immutable observation of one change at one base/head pair; every diff and comparison starts from it.</summary>
/// <remarks>Identity is content, not time: the store derives <see cref="Id"/> from the change and the two commits, so
/// observing the same pair twice returns the stored version, while a force-push creates a new version instead of rewriting
/// the old one. That is what lets a stored graph keep analysing the inputs a review began with. <see cref="MergeBase"/> is
/// deliberately outside the identity: it is evidence that arrives later, when the commits are actually materialized, so a
/// version can be listed without being diffable.</remarks>
/// <param name="Id">Identity derived from <see cref="ChangeId"/> plus both commits; the store recomputes it, so a
/// caller-supplied value acts as an idempotency key rather than as an authority. Core composes <c>change|base|head</c> and
/// hashes it, and the store derives its row key the same way, so that composition — separator included — is part of the
/// identity and must change on both sides together.</param>
/// <param name="ChangeId">Identity of the owning <see cref="ChangeNode"/>.</param>
/// <param name="BaseSha">Commit the layer was based on when observed — the parent ref tip locally, the pull request's base
/// SHA remotely. It is the intended base, not necessarily the merge base.</param>
/// <param name="HeadSha">Tip commit of the layer.</param>
/// <param name="MergeBase">Best common ancestor of <see cref="BaseSha"/> and <see cref="HeadSha"/>, recorded once the
/// commits exist. <see langword="null"/> before materialization or when Git reports zero or several ancestors; comparison
/// and analysis refuse such a version instead of guessing an ancestor.</param>
/// <param name="ObservedAt">When this pair was seen — the GitHub snapshot timestamp remotely, the local clock locally; it is
/// ordering information, not identity.</param>
public sealed record ChangeVersion(string Id, string ChangeId, string BaseSha, string HeadSha, string? MergeBase,
    DateTimeOffset ObservedAt)
{
    /// <summary>Compact label of local observation time and the first eight head characters, short enough for one version row.</summary>
    /// <remarks>The slice is clamped with <c>Math.Min</c> so a truncated or empty SHA cannot throw; eight hexadecimal
    /// characters are a reading convenience, not a uniqueness guarantee.</remarks>
    public string Label => $"{ObservedAt.LocalDateTime:g} · {HeadSha[..Math.Min(8, HeadSha.Length)]}";
    /// <summary>Binds the label wherever the version is shown as text.</summary>
    public override string ToString() => Label;
}
/// <summary>Directed relation stating that the child change is stacked on the parent change, with its provenance.</summary>
/// <remarks><see cref="Source"/> records how the edge was established: <c>local-definition</c> from the declared stack
/// order, <c>github</c> when the base ref is another pull request's head ref in the repository this change targets, and
/// <c>explicit</c> from the dependency editor. <see cref="ChangeGraphService"/> keeps explicit edges across observations
/// and lets them win over an inferred edge for the same child. Several edges may point at one child; only children with
/// exactly one parent are navigable as stack paths.</remarks>
/// <param name="Parent">Change that provides the base and therefore has to land first.</param>
/// <param name="Child">Change stacked on <see cref="Parent"/>.</param>
/// <param name="Source">Edge provenance: <c>local-definition</c>, <c>github</c> or <c>explicit</c>.</param>
public sealed record DependencyEdge(string Parent, string Child, string Source);
/// <summary>One timestamped, append-only revision of a scope's dependency graph.</summary>
/// <remarks>A revision records what was observed together with the exact version chosen for each node, so a review keeps the
/// inputs it started with and a later metadata poll cannot change them. <see cref="Revision"/> is assigned by the store,
/// monotonic per scope, and doubles as the optimistic-concurrency token; older revisions stay readable for the historical
/// view.</remarks>
/// <param name="Scope">Repository scope the revision belongs to.</param>
/// <param name="ObservedAt">When the observation ran; a local clock time, not a Git or GitHub timestamp.</param>
/// <param name="Nodes">Changes present in this revision, including retained remote nodes that left the open snapshot.</param>
/// <param name="Edges">Accepted dependency edges, after explicit-edge precedence and cycle rejection.</param>
/// <param name="Warnings">Non-fatal problems recorded while building the revision, such as an ambiguous remote parent or a
/// rejected cycle. They are surfaced verbatim so the user can link the stack manually.</param>
public sealed record GraphSnapshot(string Scope, DateTimeOffset ObservedAt, IReadOnlyList<ChangeNode> Nodes,
    IReadOnlyList<DependencyEdge> Edges, IReadOnlyList<string> Warnings)
{
    /// <summary>Store-assigned revision number, zero before the first save; a write whose expected revision no longer
    /// matches is refused instead of overwriting a concurrent observation.</summary>
    public long Revision { get; init; }
    /// <summary>Node identity to the version identity analysis must use, so a running review cannot be silently re-pointed
    /// at newer code; a node without an entry has no observed version yet.</summary>
    public IReadOnlyDictionary<string, string> Versions { get; init; } = new Dictionary<string, string>();
    /// <summary>Renders the observation time for the history revision list.</summary>
    public override string ToString() => ObservedAt.LocalDateTime.ToString("g");
}
/// <summary>Reviewer's marker that one path was reviewed at one concrete version.</summary>
/// <remarks>Stored per change and path, newest marker winning, so re-marking replaces instead of stacking. The recorded
/// <see cref="VersionId"/> is what makes progress classification possible: the later comparison starts from that version.
/// If the version is no longer stored — for example because its history was removed — the marker cannot be compared and the
/// path reads as unreviewed again.</remarks>
/// <param name="ChangeId">Change the marker belongs to.</param>
/// <param name="Path">Reviewed path on the new side; a renamed file is keyed by its current name.</param>
/// <param name="VersionId">Version that was on screen when the marker was set.</param>
/// <param name="ReviewedAt">When the marker was set; the newest one per path is the effective one.</param>
public sealed record ReviewCheckpoint(string ChangeId, string Path, string VersionId, DateTimeOffset ReviewedAt);
/// <summary>A blocking question the reviewer attached to one path of one change.</summary>
/// <remarks>Free-form text shown as <c>BLOCKER</c> beside the file, so an unresolved question cannot be mistaken for an
/// approval. This is local review state; nothing is published to GitHub. Several concerns for one path are merged when read,
/// and the UI clears one by removing it rather than by writing empty text.</remarks>
/// <param name="ChangeId">Change the concern belongs to.</param>
/// <param name="Path">Path the question is about.</param>
/// <param name="Text">The question itself; empty text is never stored as a concern.</param>
public sealed record ReviewConcern(string ChangeId, string Path, string Text);
/// <summary>How a file of the current layer stands against the reviewer's last checkpoint for that path.</summary>
public enum FileReviewState
{
    /// <summary>No checkpoint exists for the path, or its recorded version is no longer among the change's stored versions,
    /// so there is no evidence to compare from.</summary>
    Unreviewed,
    /// <summary>The checkpoint was taken on the current version, or the comparison found neither a code change for the path
    /// nor a moved base — the earlier review still covers it.</summary>
    Reviewed,
    /// <summary>The compared range changes this path's content since the checkpoint, so the earlier review no longer covers it.</summary>
    CodeChanged,
    /// <summary>The path itself is unchanged, but the layer's base or merge base moved, so the diff the reviewer saw has a
    /// different context and the file must be re-checked.</summary>
    BaseChanged,
    /// <summary>Some change entry for this path is a rename or a mode change, so the state names an identity move. A rename that
    /// also edits text lands here too, because the reviewer has to look again either way.</summary>
    MetadataChanged,
    /// <summary>The comparison could not be produced — unmaterialized contents, no merge base, or a replay that conflicts —
    /// so no state can be derived and the reviewer must look at the file.</summary>
    Unavailable
}
/// <summary>Per-file review verdict derived from a checkpoint and the comparison since then, with optional blocker text.</summary>
/// <remarks><see cref="Label"/> is the row text: current display path, the state, then <c>· BLOCKER: </c> plus the question
/// when one exists. Concerns are attached after the state is computed, so a blocker still shows on a file whose state is
/// <see cref="FileReviewState.Reviewed"/> — it stays visible until the reviewer withdraws it.</remarks>
/// <param name="Change">Current layer's file entry, which supplies the display path and the diff statistics.</param>
/// <param name="State">Classification computed by <see cref="ReviewProgressService.StatusAsync"/>.</param>
/// <param name="CheckedVersion">Version the checkpoint was taken on, or <see langword="null"/> when no usable checkpoint
/// exists.</param>
public sealed record FileReviewStatus(FileChange Change, FileReviewState State, ChangeVersion? CheckedVersion)
{
    /// <summary>Blocking question recorded for this path, attached after classification; <see langword="null"/> means clear.</summary>
    public string? Concern { get; init; }
    /// <summary>Row label built as display path, state and — when one exists — <c>· BLOCKER: </c> followed by the question.</summary>
    public string Label => $"{Change.DisplayPath} · {State}" + (Concern is null ? "" : " · BLOCKER: " + Concern);
}
/// <summary>Textual comparison of two versions of the same change, normalized onto the current base.</summary>
/// <remarks>The two lists answer different questions. <see cref="BaseChanges"/> is the diff between the two merge bases —
/// what landed underneath the layer. <see cref="CodeChanges"/> is the diff between <see cref="ExpectedTree"/> and the
/// current head — what the author changed since the checkpoint. When both versions share a merge base the previous head is
/// compared directly; otherwise the old patch is replayed onto the new base first, and a replay that conflicts yields
/// <see cref="Unavailable"/> instead of a plausible-looking wrong diff. Both lists are empty whenever the comparison is
/// unreliable, so emptiness alone is not evidence that nothing changed.</remarks>
/// <param name="Previous">Older version, normally the one the reviewer checked.</param>
/// <param name="Current">Version being reviewed now.</param>
/// <param name="ExpectedTree">Tree compared against the current head: the previous head when both versions share a merge
/// base, otherwise the tree produced by replaying the old patch onto the new base. <see langword="null"/> when the
/// comparison failed before it could be built.</param>
/// <param name="CodeChanges">Files changed between <see cref="ExpectedTree"/> and the current head.</param>
/// <param name="BaseChanges">Files changed between the two merge bases.</param>
/// <param name="Unavailable">Reason the comparison could not be produced — unmaterialized contents, absent merge base or a
/// conflicting replay; <see langword="null"/> on success.</param>
public sealed record VersionComparison(ChangeVersion Previous, ChangeVersion Current, string? ExpectedTree,
    IReadOnlyList<FileChange> CodeChanges, IReadOnlyList<FileChange> BaseChanges, string? Unavailable)
{
    /// <summary>True when the comparison actually ran; a false value means the change lists are empty and must not be read
    /// as "nothing changed".</summary>
    public bool IsReliable => Unavailable is null;
}
/// <summary>One finding about one graph edge, attributed to the child that would have to move to resolve it.</summary>
/// <remarks><see cref="Kind"/> is one of the literal kinds <see cref="StackAnalysisService"/> produces —
/// <c>Unavailable</c>, <c>StaleParent</c>, <c>Overlap</c> or <c>Conflict</c> — and several findings may exist for the same
/// edge. Findings describe ancestry and text only: a clean analysis does not prove that a restack will apply or that the
/// code is correct.</remarks>
/// <param name="ChangeId">Child node of the offending edge, that is, the layer that would need attention.</param>
/// <param name="Kind">Finding kind; the closed set of values is listed in the remarks.</param>
/// <param name="Message">User-facing sentence explaining the finding.</param>
/// <param name="Paths">Common paths for <c>Overlap</c> and conflicting paths for <c>Conflict</c>; empty when the finding
/// concerns ancestry or missing evidence.</param>
public sealed record StackAnalysisIssue(string ChangeId, string Kind, string Message, IReadOnlyList<string> Paths);
/// <summary>Outcome of one analysis pass: the revision that was analysed plus every finding on its edges.</summary>
/// <param name="Graph">The analysed revision; its pinned versions are the inputs, not the newest ones.</param>
/// <param name="Issues">Findings in edge order. An empty list means only that the captured edges produced no finding.</param>
public sealed record StackAnalysisResult(GraphSnapshot Graph, IReadOnlyList<StackAnalysisIssue> Issues);
/// <summary>One layer's contribution to a file's evolution, describing what the layer did to the tracked path.</summary>
/// <remarks>Steps are held oldest first; the last step is the newest state and carries the path the caller selected. A
/// step without a file entry still appears, so a gap in the walk stays visible instead of silently shortening the history.</remarks>
/// <param name="Change">Change that owns the layer and supplies the title used in attributions.</param>
/// <param name="Version">Captured version whose merge-base-to-head diff was inspected.</param>
/// <param name="File">File entry for the tracked path in that diff, or <see langword="null"/> when the version has no merge
/// base or its diff holds zero or several entries for the path.</param>
/// <param name="Continuous">True when the older neighbouring layer's head is this version's merge base — or when this is the
/// oldest step, which has nothing older to compare — proving the layer was captured directly on top of the previous one.
/// False marks an ancestry gap across which line numbers cannot be translated.</param>
public sealed record FileEvolutionStep(ChangeNode Change, ChangeVersion Version, FileChange? File, bool Continuous);
/// <summary>A file's evolution through a captured stack, oldest step first.</summary>
/// <remarks>Produced by <see cref="FileEvolutionService.BuildAsync"/>, which walks the selected path backwards from the
/// newest layer and then reverses the result, so the final step is the newest state and the path the user is looking at.</remarks>
/// <param name="Steps">Steps in ancestry order; empty only when the caller passed an empty path list.</param>
public sealed record FileEvolution(IReadOnlyList<FileEvolutionStep> Steps);
/// <summary>A stale review anchor re-proposed on a newer pull request version, awaiting explicit confirmation.</summary>
/// <remarks>Only produced when the selected block, widened by up to two context lines on each side, matches exactly once in
/// the new text; near-matches are refused because a comment attached to the wrong lines is worse than one that is visibly
/// stale. <see cref="Original"/> stays in the draft history, and the proposal is a suggestion until the user confirms it and
/// the publication path validates the anchor against the server.</remarks>
/// <param name="Original">Anchor as previously stored, kept unchanged for the draft history.</param>
/// <param name="Proposed">Recomputed anchor on the target pull request's head and base commits.</param>
/// <param name="OldContext">Matched region of the old text, joined with newlines so the user can see what was matched.</param>
/// <param name="NewContext">The same region in the new text, so the user can see where it moved to.</param>
public sealed record AnchorProposal(ReviewAnchor Original, ReviewAnchor Proposed, string OldContext, string NewContext);

/// <summary>Reads pull request metadata that the current open snapshot no longer contains.</summary>
/// <remarks>Used to settle the fate of a retained graph node: a change missing from the open page set is usually merged or
/// closed, and that cannot be inferred from absence. One request is made per node, and only while the recorded state is not
/// already final.</remarks>
public interface IHistoryRemoteReader
{
    /// <summary>Reads one pull request by number, including its state, for a node retained from earlier revisions.</summary>
    /// <param name="context">GitHub identity — host, repository and signed-in account — the number has to be read in.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="ct">Cancellation for the request.</param>
    /// <returns>The pull request, or a failure that the caller records as an unknown state rather than dropping the node.</returns>
    Task<Result<PullRequest>> HistoricalPullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default);
}
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
/// <summary>History pins and analysis use the explicitly selected local repository without checkout or index writes.</summary>
/// <remarks>Every method takes the repository it must run in, so no call can silently substitute a private clone or another
/// worktree. Captured commits are pinned as application refs under the history namespace, which keeps them alive for analysis
/// and for the historical views while the user's branches remain untouched. Analysis is textual: external diff helpers,
/// textconv and configured merge drivers are disabled, so a result that would require running user code is reported as
/// unavailable instead of executing it. A read lease is held for the duration of a comparison and defers pin removal.</remarks>
public interface IHistoryGit
{
    /// <summary>Reports whether this scope's pinned commits are present in the opened repository.</summary>
    /// <param name="repository">Local repository that must own the objects.</param>
    /// <param name="scope">History scope whose pins are checked.</param>
    /// <param name="ct">Cancellation for the check.</param>
    /// <returns><see cref="ContentAvailability.Available"/> when pins exist and every commit is present; removed when the user
    /// explicitly removed the history; needs-download when there are no pins and this repository was never recorded as having
    /// the content; unavailable when pins exist but referenced objects are missing, or the content was recorded while the pins
    /// are gone.</returns>
    Task<Result<ContentAvailability>> AvailabilityAsync(RepositoryIdentity repository, string scope, CancellationToken ct = default);
    /// <summary>Verifies that the commits exist locally and pins them for this scope.</summary>
    /// <remarks>Capture never downloads: a commit that is not already in the opened repository fails the call, and the
    /// message tells the user to load the current pull request changes first. Pinning is what allows a later force-push of
    /// the source branch not to destroy the analysed history.</remarks>
    /// <param name="repository">Local repository that owns the objects.</param>
    /// <param name="scope">History scope the pins belong to.</param>
    /// <param name="commits">Commit SHAs to retain, typically the base and head of one layer.</param>
    /// <param name="ct">Cancellation for the operation.</param>
    Task<Result<Unit>> CaptureAsync(RepositoryIdentity repository, string scope, IReadOnlyList<string> commits, CancellationToken ct = default);
    /// <summary>Takes a read lease over the scope's history objects, failing unless the content is available.</summary>
    /// <remarks>The lease's root is the working root to pass to Git reads. It must be disposed — while it is held, removal of
    /// the pins is deferred — and acquiring it fails with a user-facing problem when the content was removed or is missing.</remarks>
    /// <param name="repository">Local repository that owns the objects.</param>
    /// <param name="scope">History scope to lease.</param>
    /// <param name="ct">Cancellation for the acquisition.</param>
    /// <returns>A lease that releases the read when disposed.</returns>
    Task<Result<ObjectSnapshotLease>> AcquireAsync(RepositoryIdentity repository, string scope, CancellationToken ct = default);
    /// <summary>Removes this scope's history pins; Git may collect the commits later.</summary>
    /// <remarks>Versions, review metadata and drafts stay, and the content is recorded as explicitly removed so a later
    /// migration does not silently restore it. Repository directories and objects are never deleted here — disk management
    /// belongs to Git.</remarks>
    /// <param name="repository">Local repository that owns the pins.</param>
    /// <param name="scope">History scope whose pins are removed.</param>
    /// <param name="ct">Cancellation for the operation.</param>
    Task<Result<Unit>> ForgetContentsAsync(RepositoryIdentity repository, string scope, CancellationToken ct = default);
    /// <summary>Replays an old patch onto a new base and returns the resulting tree, or <see langword="null"/> on conflict.</summary>
    /// <remarks>The replay merges the old head as if it had been based on the new base, using the old base as the merge base,
    /// and returns a tree object — no commit, ref or index is written. A textual conflict is reported as
    /// <see langword="null"/> so the caller can mark the comparison unavailable; any other failure is a failed result.</remarks>
    /// <param name="repository">Local repository that owns the objects.</param>
    /// <param name="oldBase">Base the old patch was originally taken from.</param>
    /// <param name="oldHead">Head holding the old patch.</param>
    /// <param name="newBase">New base the patch is replayed onto.</param>
    /// <param name="ct">Cancellation for the analysis.</param>
    /// <returns>The normalized tree, or <see langword="null"/> when the patch does not apply cleanly.</returns>
    Task<Result<string?>> TransplantAsync(RepositoryIdentity repository, string oldBase, string oldHead, string newBase, CancellationToken ct = default);
    /// <summary>Trial-merges two commits and returns the paths that conflict, or an empty list for a clean merge.</summary>
    /// <remarks>A textual preview only: nothing is committed, no ref or index is written, and external merge drivers are
    /// disabled. A conflict set therefore does not predict exactly what a restack will produce, and a merge that would need a
    /// configured driver is reported as a failure instead of running user code.</remarks>
    /// <param name="repository">Local repository that owns the objects.</param>
    /// <param name="parent">Current parent head; the prospective base.</param>
    /// <param name="head">Child head that would be rebased.</param>
    /// <param name="ct">Cancellation for the analysis.</param>
    /// <returns>Conflicting paths, empty when the trial merge is clean.</returns>
    Task<Result<IReadOnlyList<string>>> MergeAsync(RepositoryIdentity repository, string parent, string head, CancellationToken ct = default);
}
