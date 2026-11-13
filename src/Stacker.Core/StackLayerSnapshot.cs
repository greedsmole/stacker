namespace Stacker.Core;

/// <summary>Resolved state for one ordered layer, including parent relationship and divergence warning.</summary>
/// <param name="Position">Zero-based index in definition order; the UI shows it as position plus one.</param>
/// <param name="Branch">Full ref of the layer.</param>
/// <param name="Parent">Full ref the layer should sit on: the stack base for the first layer, otherwise the previous branch.</param>
/// <param name="HeadSha">Commit the layer ref pointed at in the snapshot, or null when the ref was missing.</param>
/// <param name="ParentSha">Commit the parent ref pointed at in the snapshot, or null when the ref was missing.</param>
/// <param name="Ahead">Commits reachable from the layer but not from the parent; stays zero when a ref was missing.</param>
/// <param name="Behind">Parent commits missing from the layer; stays zero when a ref was missing, and a nonzero value otherwise means the
/// parent is not an ancestor.</param>
/// <param name="Warning">Display-ready caveat - a missing ref, or a comparison that can only use the merge base - or null when the layer is
/// comparable as it stands.</param>
/// <remarks>The counts are only meaningful together with <c>Warning</c>: when a ref is missing the counts were never measured and their zeros
/// must not be read as "in sync".</remarks>
public sealed record StackLayerSnapshot(int Position, string Branch, string Parent, string? HeadSha,
    string? ParentSha, int Ahead, int Behind, string? Warning);
