namespace Stacker.Core;

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
    WorkspaceSnapshot Displayed
    {
        get;
    }
    /// <summary>The staged remote snapshot waiting for an explicit apply, or <see langword="null"/> when nothing is staged.</summary>
    /// <remarks>Staging never affects <see cref="Displayed"/>. The staged copy already carries the revision it will have
    /// once applied, and a newer offer replaces an older one instead of queueing both.</remarks>
    WorkspaceSnapshot? Pending
    {
        get;
    }
    /// <summary>The change, comparison and file the user last selected.</summary>
    WorkspaceSelection Selection
    {
        get;
    }
    /// <summary>Ticket describing the current epoch, selection and displayed revision.</summary>
    QueryTicket Ticket
    {
        get;
    }
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
