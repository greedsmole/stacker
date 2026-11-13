using Stacker.Core.Framework;
namespace Stacker.Core;

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
    public long Revision
    {
        get; init;
    }
    /// <summary>Node identity to the version identity analysis must use, so a running review cannot be silently re-pointed
    /// at newer code; a node without an entry has no observed version yet.</summary>
    public IReadOnlyDictionary<string, string> Versions { get; init; } = new Dictionary<string, string>();
    /// <summary>Renders the observation time for the history revision list.</summary>
    public override string ToString() => ObservedAt.LocalDateTime.ToString("g");
}
