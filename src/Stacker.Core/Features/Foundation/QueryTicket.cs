namespace Stacker.Core;

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
