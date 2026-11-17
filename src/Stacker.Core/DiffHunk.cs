namespace Stacker.Core;

/// <summary>A patch hunk and its parsed line coordinates, used to place inline review anchors.</summary>
/// <param name="Header">The raw <c>@@</c> line exactly as Git wrote it, including any trailing section heading.</param>
/// <param name="Lines">The hunk body only: context, added and removed rows in order, plus a notice for the no-final-newline marker. The header
/// row is not repeated here.</param>
/// <param name="OldStart">First old-side line number; for an empty old side the range is empty, and Git uses 0 for a new file or the preceding
/// line otherwise.</param>
/// <param name="OldCount">How many old-side lines the hunk covers, or 0 when it only adds. Git omits the count when it is 1, and the parser
/// normalizes that to 1.</param>
/// <param name="NewStart">First new-side line number, with the same empty-range convention as <c>OldStart</c>.</param>
/// <param name="NewCount">How many new-side lines the hunk covers, or 0 when it only removes; an omitted count normalizes to 1.</param>
/// <param name="Context">Text after the closing <c>@@</c>, trimmed: Git's enclosing function or section hint, empty when Git found none.</param>
/// <remarks>Coordinates are inclusive on both sides, so a hunk covering <c>OldCount</c> lines from <c>OldStart</c> ends at
/// <c>OldStart + OldCount - 1</c>; that is why the renderer clamps with <c>Math.Max(0, count - 1)</c> for an empty side. Mapping a new-side line
/// back to the old side across a hunk above it adds <c>OldStart + OldCount - NewStart - NewCount</c>, the shift that hunk introduced. The Git
/// adapter fills every field from Git's header; the defaults exist so a caller can construct a hunk from just a header and its lines.</remarks>
public sealed record DiffHunk(string Header, IReadOnlyList<DiffLine> Lines, int OldStart = 0, int OldCount = 0, int NewStart = 0, int NewCount = 0, string Context = "");
