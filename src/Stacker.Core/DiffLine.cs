namespace Stacker.Core;

/// <summary>One patch row with its semantic role and the line numbers it occupies on each side.</summary>
/// <param name="Kind">Role of the row, which also decides whether it can carry a review comment.</param>
/// <param name="OldLineNumber">Line number in the old file, or null for an added line, a header or a notice.</param>
/// <param name="NewLineNumber">Line number in the new file, or null for a removed line, a header or a notice.</param>
/// <param name="Text">The raw patch line including its leading marker (<c>+</c>, <c>-</c> or space) for code rows; renderers drop the marker
/// for display and keep the original text for a patch copy.</param>
public sealed record DiffLine(DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);
