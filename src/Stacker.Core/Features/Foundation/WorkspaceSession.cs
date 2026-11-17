namespace Stacker.Core;
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
    public WorkspaceSnapshot? Pending
    {
        get; private set;
    }
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
        if (Displayed.Repository?.Root != repository.Root)
        {
            _epoch++;
            Selection = new(null, null, null);
            _selectionRevision++;
            Pending = null;
            Displayed = new(Displayed.Revision + 1, repository, null, null);
        }
        else
            Displayed = Displayed with
            {
                Revision = Displayed.Revision + 1,
                Repository = repository
            };
    }
    /// <inheritdoc/>
    /// <remarks>If the offered snapshot belongs to a different GitHub account or repository than the displayed one, the
    /// epoch and selection revision advance immediately, so reads issued for the old identity can no longer publish even
    /// though the snapshot is only staged. The staged copy gets <c>Displayed.Revision + 1</c>, the revision it will have
    /// when applied.</remarks>
    public void Offer(GitHubSnapshot snapshot, GraphSnapshot? graph)
    {
        if (Displayed.Remote?.Context.Identity is { } owner && owner != snapshot.Context.Identity)
        {
            _epoch++;
            _selectionRevision++;
        }
        Pending = Displayed with
        {
            Revision = Displayed.Revision + 1,
            Remote = snapshot,
            Graph = graph
        };
    }
    /// <inheritdoc/>
    /// <remarks>Used by the workspace runtime to keep one half of the remote state in step with the other. A change of
    /// GitHub identity, including clearing the remote side, advances the epoch and selection revision; a change confined
    /// to the graph advances only the displayed revision. An already staged snapshot is left untouched.</remarks>
    public void SetRemote(GitHubSnapshot? snapshot, GraphSnapshot? graph)
    {
        if (Displayed.Remote?.Context.Identity != snapshot?.Context.Identity)
        {
            _epoch++;
            _selectionRevision++;
        }
        Displayed = Displayed with
        {
            Revision = Displayed.Revision + 1,
            Remote = snapshot,
            Graph = graph
        };
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
    public void ApplyPending()
    {
        if (Pending is null)
            return;
        Displayed = Pending;
        Pending = null;
        _selectionRevision++;
    }
    /// <inheritdoc/>
    /// <remarks>An equal selection is ignored so that re-selecting the same target does not invalidate in-flight work.
    /// A genuine change advances the selection revision but not the epoch, because the repository and account are
    /// unchanged.</remarks>
    public void Select(WorkspaceSelection selection)
    {
        if (selection == Selection)
            return;
        Selection = selection;
        _selectionRevision++;
    }
    /// <inheritdoc/>
    /// <remarks>Compares the whole ticket by value; nothing about the session is retained by the caller beyond those three
    /// numbers, which is what makes the check safe to perform after an arbitrary await.</remarks>
    public bool IsCurrent(QueryTicket ticket) => ticket == Ticket;
    /// <inheritdoc/>
    /// <remarks>Advances the epoch and the selection revision so every outstanding ticket becomes stale, and leaves the
    /// displayed snapshot, the staged snapshot and the selection in place. Used while preparing to close, so a result
    /// that arrives late cannot touch the window being shut down.</remarks>
    public void Suspend()
    {
        _epoch++;
        _selectionRevision++;
    }
}
