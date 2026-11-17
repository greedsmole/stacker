namespace Stacker.Core;

/// <summary>Parsed and raw forms of a single file patch.</summary>
/// <param name="File">The metadata this patch belongs to; its kind decides the binary, submodule and mode notes in the header.</param>
/// <param name="Patch">The raw patch text exactly as Git produced it, kept for the copy-patch action.</param>
/// <param name="Hunks">Hunks with parsed coordinates, used for rendering and for remapping a line to the other side.</param>
/// <param name="Lines">Flat stream of every patch row, including one header row per hunk and notices outside hunks, so a lookup by line number
/// can scan once.</param>
/// <remarks><c>Hunks</c> and <c>Lines</c> are two views of the same <c>Patch</c> and cannot disagree: rendering and history remapping walk the
/// hunks, while search, selection and review anchoring use the flat stream.</remarks>
public sealed record FileDiff(FileChange File, string Patch, IReadOnlyList<DiffHunk> Hunks, IReadOnlyList<DiffLine> Lines);
