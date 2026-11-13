using Stacker.Core.Framework;
namespace Stacker.Core;

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
