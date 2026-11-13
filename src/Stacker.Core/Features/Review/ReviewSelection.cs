using System.Text;
using System.Text.RegularExpressions;

namespace Stacker.Core;

/// <summary>GitHub line-anchor rules shared by the selection affordance and review composer.</summary>
/// <remarks>
/// The rules exist because GitHub rejects or misfiles a review comment whose line is not on the requested side of the
/// current diff, and because a comment attached to the wrong line is worse than a refusal: it looks authoritative while
/// pointing at unrelated code. Every rejection below therefore returns a user-facing reason and no anchor.
/// </remarks>
public static class ReviewSelection
{
    /// <summary>Builds an anchor from selected diff lines, or explains why the selection cannot be commented on.</summary>
    /// <remarks>
    /// Side selection: an explicit side wins. Without one, a selection that contains removed lines and no added lines is
    /// inferred as LEFT and then required to contain only removed lines, which is how a deletion is recognized; every other
    /// selection is treated as RIGHT, where context and added lines are allowed and a removed line is refused, because
    /// GitHub accepts unchanged context on the right. A left-side selection must contain only removed lines and a right-side
    /// selection only added or context lines, which is what the screen shows; a range that mixes both sides, or includes a
    /// header line, is refused because the two sides are different coordinate spaces. Line numbers are taken on the chosen
    /// side, de-duplicated and sorted, so every selected line must have a number there, no line may repeat, and the range
    /// must be contiguous — a gap cannot be represented by one anchor and would place the comment across unrelated lines.
    /// The anchor's <c>Line</c> is the last selected line and <c>StartLine</c> is set only for a range, matching GitHub's
    /// end-line/start-line contract used by the writer.
    /// </remarks>
    /// <param name="pr">Pull request whose number and version SHAs the anchor records.</param>
    /// <param name="path">Repository-relative path of the file being reviewed.</param>
    /// <param name="lines">Selected diff lines, in the order shown; header and notice lines carry no side line number.</param>
    /// <param name="side">Explicit <c>LEFT</c> or <c>RIGHT</c> choice, or null to infer it from the selected line kinds.</param>
    /// <param name="anchor">The built anchor on success; null when the selection is refused.</param>
    /// <param name="error">A message naming the rule the selection broke, or null on success.</param>
    /// <returns>True when <paramref name="anchor"/> was built; false with <paramref name="error"/> set otherwise.</returns>
    public static bool TryCreateAnchor(PullRequest pr, string path, IReadOnlyList<DiffLine> lines, string? side,
        out ReviewAnchor? anchor, out string? error)
    {
        anchor = null;
        error = null;
        if (lines.Count == 0)
        {
            error = "Select code lines first.";
            return false;
        }
        var left = side == "LEFT" || side is null && lines.Any(l => l.Kind == DiffLineKind.Removed) && lines.All(l => l.Kind != DiffLineKind.Added);
        // GitHub accepts unchanged context on RIGHT; LEFT targets only deleted lines.
        if (side is not (null or "LEFT" or "RIGHT") || (left ? lines.Any(l => l.Kind != DiffLineKind.Removed) : lines.Any(l => l.Kind is not (DiffLineKind.Added or DiffLineKind.Context))))
        {
            error = "Select a contiguous range on one side of the diff. Context lines use the right side.";
            return false;
        }
        var numbers = lines.Select(l => left ? l.OldLineNumber : l.NewLineNumber).OfType<int>().Distinct().Order().ToArray();
        if (numbers.Length != lines.Count || numbers[0] <= 0 || numbers[^1] - numbers[0] + 1 != numbers.Length)
        {
            error = "Select a contiguous range of code lines.";
            return false;
        }
        anchor = new(pr.Number, pr.HeadSha, pr.BaseSha, path, left ? "LEFT" : "RIGHT", numbers[^1],
            numbers.Length > 1 ? numbers[0] : null, numbers.Length > 1 ? left ? "LEFT" : "RIGHT" : null);
        return true;
    }
}
