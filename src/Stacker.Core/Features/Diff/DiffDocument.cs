namespace Stacker.Core;

/// <summary>Complete read-only comparison shown by the history workspace, together with the mode needed to rebuild it.</summary>
/// <remarks>The document is the navigation unit: showing one replaces and disposes the previous sections, while a <see cref="NavigationTarget"/> records enough state to rebuild it later. Sections render independently, so one document can combine different commit pairs. <see cref="Explanation"/> is free text carrying the document's description (such as "Original layer diff" or a comparison's reliability warning); it is never parsed, and no current view binds to it, so it is metadata rather than visible text.</remarks>
/// <param name="Mode">Comparison mode that produced the document and that back and forward will replay.</param>
/// <param name="Sections">Ordered comparisons to render; empty when the comparison is unavailable.</param>
/// <param name="Explanation">Description the history workspace sets when it builds the document; no current view displays it.</param>
public sealed record DiffDocument(DiffDocumentMode Mode, IReadOnlyList<DiffDocumentSection> Sections, string Explanation);
