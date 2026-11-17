using Stacker.Core.Framework;
namespace Stacker.Core;

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
