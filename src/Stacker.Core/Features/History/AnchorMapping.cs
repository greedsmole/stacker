namespace Stacker.Core;

/// <summary>Re-anchors a stale review comment by matching its exact context against a newer file version.</summary>
/// <remarks>Only exact matches are considered: no similarity scoring and no offset heuristics on ambiguous text. The reason is
/// that a proposal moves a review comment, so attaching it to the wrong lines is worse than leaving it visibly stale. The user
/// confirms the proposal explicitly, and the publication path validates the anchor against the server before anything is
/// written to the pull request.</remarks>
public static class AnchorMapping
{
    /// <summary>Requires an exact, unique selected block with up to two context lines on both sides.</summary>
    /// <remarks>Both texts are normalized from CRLF to LF and split, and the anchor's line numbers are read as one-based. The
    /// block runs from <c>StartLine</c> when the anchor has one — otherwise from the single line — through <c>Line</c>, widened
    /// by up to two context lines on each side and clamped to the file. The scan compares whole blocks for equality and aborts as
    /// soon as a second match appears, so a block occurring twice is rejected instead of resolved by position. Mapping preserves
    /// the anchor's length and shifts it by the leading context offset: <c>mappedStart = matchIndex + (start − contextStart) + 1</c>
    /// and <c>mappedEnd = mappedStart + (end − start)</c>; <c>StartLine</c> is emitted only when the original had one, so a
    /// single-line comment stays single-line. The proposal carries the target pull request's head and base commits and the
    /// possibly renamed path.</remarks>
    /// <param name="anchor">Anchor as stored; its line numbers refer to the old text.</param>
    /// <param name="target">Pull request version the proposal is for; supplies the new head and base commits.</param>
    /// <param name="newPath">Path in the new version, which can differ after a rename.</param>
    /// <param name="oldText">Full text of the file in the version the anchor was created on.</param>
    /// <param name="newText">Full text of the file in the target version.</param>
    /// <returns>The proposal, or <see langword="null"/> when the block is missing or matches more than once.</returns>
    public static AnchorProposal? Propose(ReviewAnchor anchor, PullRequest target, string newPath, string oldText, string newText)
    {
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');
        var start = (anchor.StartLine ?? anchor.Line) - 1;
        var end = anchor.Line - 1;
        if (start < 0 || end >= oldLines.Length || end < start)
            return null;
        var contextStart = Math.Max(0, start - 2);
        var contextEnd = Math.Min(oldLines.Length - 1, end + 2);
        var block = oldLines[contextStart..(contextEnd + 1)];
        var matches = new List<int>();
        for (var i = 0; i + block.Length <= newLines.Length; i++)
        {
            if (block.SequenceEqual(newLines[i..(i + block.Length)]))
                matches.Add(i);
            if (matches.Count > 1)
                return null;
        }
        if (matches.Count != 1)
            return null;
        var mappedStart = matches[0] + start - contextStart + 1;
        var mappedEnd = mappedStart + end - start;
        var proposed = anchor with
        {
            HeadSha = target.HeadSha,
            BaseSha = target.BaseSha,
            Path = newPath,
            Line = mappedEnd,
            StartLine = anchor.StartLine is null ? null : mappedStart
        };
        return new(anchor, proposed, string.Join("\n", block), string.Join("\n", newLines[matches[0]..(matches[0] + block.Length)]));
    }
}
