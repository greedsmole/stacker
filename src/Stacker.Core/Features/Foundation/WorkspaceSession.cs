namespace Stacker.Core;

/// <summary>Navigation state recorded by the workspace: the change, the comparison and the file in view.</summary>
/// <param name="ChangeId">Id of the change node being reviewed, or <see langword="null"/> when nothing is selected.</param>
/// <param name="ComparisonId">Comparison identity of the displayed comparison, or <see langword="null"/>.</param>
/// <param name="Path">Path of the selected file inside that comparison, or <see langword="null"/>.</param>
/// <remarks>Value equality is significant: the session ignores a selection equal to the current one, so reselecting the
/// same target must not invalidate in-flight queries. The session stores this record as its selection state and compares
/// whole values; it is not a ticket and does not by itself prove that a background result is current.</remarks>
public sealed record WorkspaceSelection(string? ChangeId, string? ComparisonId, string? Path);
/// <summary>Everything the workspace displays at one revision: the repository, the applied remote snapshot and its graph.</summary>
/// <param name="Revision">Monotonic revision of the displayed data, starting at 0. Each displayed change advances it by
/// one, and a staged snapshot carries the value that was reserved for it when it was offered.</param>
/// <param name="Repository">Repository snapshot in use, or <see langword="null"/> before a repository is opened.</param>
/// <param name="Remote">Applied GitHub snapshot, or <see langword="null"/> when no remote side is applied.</param>
/// <param name="Graph">History graph that belongs to <paramref name="Remote"/>, or <see langword="null"/>.</param>
/// <remarks>The record is immutable: a change replaces the whole value with a modified copy, so a reader that captured an
/// earlier instance keeps a consistent view. The revision is what query tickets compare, which is why an asynchronous
/// producer has to check the ticket before publishing anything derived from an older instance.</remarks>
public sealed record WorkspaceSnapshot(long Revision, RepositorySnapshot? Repository, GitHubSnapshot? Remote, GraphSnapshot? Graph);
/// <summary>Proof that an asynchronous read still belongs to the current workspace state.</summary>
/// <param name="Epoch">Session epoch: advances when the opened repository root changes, when the GitHub account or
/// repository identity changes, and when the session is suspended for shutdown.</param>
/// <param name="SelectionRevision">Selection counter: advances whenever the selection is replaced or reset, and when a
/// staged snapshot is applied.</param>
/// <param name="SnapshotRevision">Revision of the displayed snapshot the read started from.</param>
/// <remarks>A caller captures the ticket before awaiting and compares it with <see cref="IWorkspaceSession.IsCurrent"/>
/// before publishing. All three values must still match, because a result can be stale for three independent reasons:
/// another repository or account, another selection, or newer displayed data. The record is compared by value, so a
/// ticket can be held across awaits and even passed to another component without keeping a reference to the session.</remarks>
public sealed record QueryTicket(long Epoch, long SelectionRevision, long SnapshotRevision);
/// <summary>Application state the workspace displays, staged separately from what a background refresh observed.</summary>
/// <remarks>The session is a plain in-memory object with no locking and no dispatcher dependency: it is driven from one
/// thread (the UI thread) and hands out immutable records. Background work stages a remote snapshot with
/// <see cref="Offer"/> and the user applies it explicitly, so a poll never replaces the code currently being read; query
/// tickets decide whether an asynchronous result may still be published.</remarks>
public interface IWorkspaceSession
{
    /// <summary>The snapshot the workspace currently shows.</summary>
    /// <remarks>Only opening a repository, applying a staged snapshot or installing a remote snapshot changes it; a
    /// background poll stages its result and leaves this value alone.</remarks>
    WorkspaceSnapshot Displayed { get; }
    /// <summary>The staged remote snapshot waiting for an explicit apply, or <see langword="null"/> when nothing is staged.</summary>
    /// <remarks>Staging never affects <see cref="Displayed"/>. The staged copy already carries the revision it will have
    /// once applied, and a newer offer replaces an older one instead of queueing both.</remarks>
    WorkspaceSnapshot? Pending { get; }
    /// <summary>The change, comparison and file the user last selected.</summary>
    WorkspaceSelection Selection { get; }
    /// <summary>Ticket describing the current epoch, selection and displayed revision.</summary>
    QueryTicket Ticket { get; }
    /// <summary>Opens or refreshes the repository snapshot; a different root starts a new session epoch.</summary>
    /// <param name="repository">Resolved repository snapshot; its root identifies the repository.</param>
    void Open(RepositorySnapshot repository);
    /// <summary>Stages a remote snapshot and its graph without displaying either.</summary>
    /// <param name="snapshot">Metadata snapshot returned by a poll.</param>
    /// <param name="graph">History graph observed from that snapshot, or <see langword="null"/>.</param>
    void Offer(GitHubSnapshot snapshot, GraphSnapshot? graph);
    /// <summary>Applies the staged snapshot, or does nothing when nothing is staged.</summary>
    void ApplyPending();
    /// <summary>Installs a remote snapshot and graph directly, without staging.</summary>
    /// <param name="snapshot">Remote snapshot to display, or <see langword="null"/> to clear the remote side.</param>
    /// <param name="graph">History graph to store with it, or <see langword="null"/>.</param>
    void SetRemote(GitHubSnapshot? snapshot, GraphSnapshot? graph);
    /// <summary>Drops the staged snapshot without touching what is displayed.</summary>
    void DiscardPending();
    /// <summary>Replaces the selection, advancing the selection revision only when the value actually changes.</summary>
    /// <param name="selection">New selection.</param>
    void Select(WorkspaceSelection selection);
    /// <summary>True when <paramref name="ticket"/> still matches the current epoch, selection and displayed revision.</summary>
    /// <param name="ticket">Ticket captured by an asynchronous read before it started.</param>
    /// <returns><see langword="true"/> when the read may publish its result.</returns>
    bool IsCurrent(QueryTicket ticket);
    /// <summary>Invalidates every outstanding ticket for shutdown, without clearing the displayed state.</summary>
    void Suspend();
}
/// <summary>The application state is independent of controls and dispatchers. Only explicit Apply replaces displayed data.</summary>
/// <remarks>Three counters make the ticket comparisons work. <c>_epoch</c> advances when the repository root changes,
/// when the GitHub account or repository identity changes and on <see cref="Suspend"/>. <c>_selectionRevision</c>
/// advances on every selection replacement or reset, when a staged snapshot is applied, and together with the epoch.
/// <c>Displayed.Revision</c> starts at 0 and advances by exactly one per displayed change. The split is what lets a
/// selection change invalidate pending work without bumping the displayed revision, while a repository switch invalidates
/// both.
/// <para>The type holds no lock and no cancellation source, so it is meant to be driven from one thread; the records it
/// returns are immutable, so a background reader can hold a snapshot safely, but a background writer must not call back
/// into it. Why the state was split into displayed and pending rather than applied in place is described in the
/// architecture record: a metadata poll may not replace the code the user is reading until they ask for the update.</para>
/// <para>Each displayed change advances the revision by one. An offer reserves <c>Displayed.Revision + 1</c> for the
/// staged copy without moving the displayed one, and successive offers before an apply reuse that same reserved value;
/// that is harmless because only the newest staged snapshot is kept. Applying installs the reserved value, which is
/// normally one past the displayed revision, and the selection revision advances in every case, so a ticket taken before
/// the apply cannot survive even if the revision happens to stay the same.</para></remarks>
public sealed class WorkspaceSession : IWorkspaceSession
{
    /// <summary>Session epoch. Advances on a repository root change, a GitHub identity change and on
    /// <see cref="Suspend"/>; it is not a count of opened repositories.</summary>
    private long _epoch;
    /// <summary>Selection revision. Advances whenever the selection is replaced or reset, when a staged snapshot is
    /// applied, and together with the epoch.</summary>
    private long _selectionRevision;
    /// <inheritdoc/>
    /// <remarks>Starts as an empty snapshot at revision 0, before any repository is opened.</remarks>
    public WorkspaceSnapshot Displayed { get; private set; } = new(0, null, null, null);
    /// <inheritdoc/>
    /// <remarks>Cleared by <see cref="DiscardPending"/> and by opening a different repository root; a refresh of the
    /// same root keeps it.</remarks>
    public WorkspaceSnapshot? Pending { get; private set; }
    /// <inheritdoc/>
    /// <remarks>Starts with every field null; it is replaced through <see cref="Select"/> and reset when a different
    /// repository root is opened.</remarks>
    public WorkspaceSelection Selection { get; private set; } = new(null, null, null);
    /// <inheritdoc/>
    /// <remarks>Computed on each read from the two counters and the displayed revision, so it reflects the state at the
    /// moment of the read and nothing is cached.</remarks>
    public QueryTicket Ticket => new(_epoch, _selectionRevision, Displayed.Revision);
    /// <inheritdoc/>
    /// <remarks>A different root starts a new epoch, resets the selection, discards any staged snapshot and clears the
    /// remote side and graph, because all of that belonged to the previous repository. The same root is a refresh: only
    /// the repository snapshot is replaced and the revision advances, so the user's selection, the applied remote
    /// snapshot and the history graph survive. The root is compared as a string.</remarks>
    public void Open(RepositorySnapshot repository)
    {
        if (Displayed.Repository?.Root != repository.Root) { _epoch++; Selection = new(null, null, null); _selectionRevision++; Pending = null; Displayed = new(Displayed.Revision + 1, repository, null, null); }
        else Displayed = Displayed with { Revision = Displayed.Revision + 1, Repository = repository };
    }
    /// <inheritdoc/>
    /// <remarks>If the offered snapshot belongs to a different GitHub account or repository than the displayed one, the
    /// epoch and selection revision advance immediately, so reads issued for the old identity can no longer publish even
    /// though the snapshot is only staged. The staged copy gets <c>Displayed.Revision + 1</c>, the revision it will have
    /// when applied.</remarks>
    public void Offer(GitHubSnapshot snapshot, GraphSnapshot? graph)
    {
        if (Displayed.Remote?.Context.Identity is { } owner && owner != snapshot.Context.Identity) { _epoch++; _selectionRevision++; }
        Pending = Displayed with { Revision = Displayed.Revision + 1, Remote = snapshot, Graph = graph };
    }
    /// <inheritdoc/>
    /// <remarks>Used by the workspace runtime to keep one half of the remote state in step with the other. A change of
    /// GitHub identity, including clearing the remote side, advances the epoch and selection revision; a change confined
    /// to the graph advances only the displayed revision. An already staged snapshot is left untouched.</remarks>
    public void SetRemote(GitHubSnapshot? snapshot, GraphSnapshot? graph)
    {
        if (Displayed.Remote?.Context.Identity != snapshot?.Context.Identity) { _epoch++; _selectionRevision++; }
        Displayed = Displayed with { Revision = Displayed.Revision + 1, Remote = snapshot, Graph = graph };
    }
    /// <inheritdoc/>
    /// <remarks>No counter advances, because nothing displayed changed; a ticket captured before the discard stays
    /// current.</remarks>
    public void DiscardPending() => Pending = null;
    /// <inheritdoc/>
    /// <remarks>Installs the staged snapshot as displayed and advances only the selection revision, because the displayed
    /// revision was already reserved when the snapshot was offered. Every ticket captured before the apply becomes stale
    /// through that selection revision even if an intervening displayed change means the revision itself does not move.
    /// A no-op when nothing is staged.</remarks>
    public void ApplyPending() { if (Pending is null) return; Displayed = Pending; Pending = null; _selectionRevision++; }
    /// <inheritdoc/>
    /// <remarks>An equal selection is ignored so that re-selecting the same target does not invalidate in-flight work.
    /// A genuine change advances the selection revision but not the epoch, because the repository and account are
    /// unchanged.</remarks>
    public void Select(WorkspaceSelection selection) { if (selection == Selection) return; Selection = selection; _selectionRevision++; }
    /// <inheritdoc/>
    /// <remarks>Compares the whole ticket by value; nothing about the session is retained by the caller beyond those three
    /// numbers, which is what makes the check safe to perform after an arbitrary await.</remarks>
    public bool IsCurrent(QueryTicket ticket) => ticket == Ticket;
    /// <inheritdoc/>
    /// <remarks>Advances the epoch and the selection revision so every outstanding ticket becomes stale, and leaves the
    /// displayed snapshot, the staged snapshot and the selection in place. Used while preparing to close, so a result
    /// that arrives late cannot touch the window being shut down.</remarks>
    public void Suspend() { _epoch++; _selectionRevision++; }
}
