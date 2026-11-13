using System.Security.Cryptography;
using System.Text;
using Stacker.Core.Framework;
namespace Stacker.Core;

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
