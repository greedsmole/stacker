namespace Stacker.Core;

/// <summary>Which comparison a history document shows and, with it, which action can recreate the document.</summary>
/// <remarks>The mode is carried on <see cref="DiffDocument"/> and on every <see cref="NavigationTarget"/> because back and forward must rebuild the same view from the change, version and path alone. Each mode maps to one producer in the history workspace rather than acting as a general-purpose diff filter.</remarks>
public enum DiffDocumentMode
{
    /// <summary>The selected version's own layer diff, from its merge base to its head; the default read-only comparison.</summary>
    Current,
    /// <summary>The version comparison, anchored at the review checkpoint recorded for the selected file instead of the previously observed version.</summary>
    SinceReviewed,
    /// <summary>The difference between the previously observed version and the current one, split into base movement and replayed commits.</summary>
    BetweenVersions,
    /// <summary>One path traced through the stack path, with one section per layer in which the path changed.</summary>
    FileEvolution
}

/// <summary>One independently rendered comparison inside a history document, pinned to a root and an exact commit pair.</summary>
/// <remarks>Sections are rendered in the order given; each supplies its own file list and diff view. A version comparison usually contributes two sections (base changes, then own changes after replay), while file evolution contributes one per layer. <see cref="BaseSha"/> and <see cref="HeadSha"/> are full commit ids used verbatim for patch reads, so earlier sections stay stable while later ones are built.</remarks>
/// <param name="Title">Heading of the section; also used as the comparison's layer label.</param>
/// <param name="Root">Repository root that patch reads use.</param>
/// <param name="BaseSha">Left side of the comparison.</param>
/// <param name="HeadSha">Right side of the comparison.</param>
/// <param name="Files">Files of this comparison, each loaded on demand; the section does not truncate the list.</param>
public sealed record DiffDocumentSection(string Title, string Root, string BaseSha, string HeadSha, IReadOnlyList<FileChange> Files);

/// <summary>Complete read-only comparison shown by the history workspace, together with the mode needed to rebuild it.</summary>
/// <remarks>The document is the navigation unit: showing one replaces and disposes the previous sections, while a <see cref="NavigationTarget"/> records enough state to rebuild it later. Sections render independently, so one document can combine different commit pairs. <see cref="Explanation"/> is free text carrying the document's description (such as "Original layer diff" or a comparison's reliability warning); it is never parsed, and no current view binds to it, so it is metadata rather than visible text.</remarks>
/// <param name="Mode">Comparison mode that produced the document and that back and forward will replay.</param>
/// <param name="Sections">Ordered comparisons to render; empty when the comparison is unavailable.</param>
/// <param name="Explanation">Description the history workspace sets when it builds the document; no current view displays it.</param>
public sealed record DiffDocument(DiffDocumentMode Mode, IReadOnlyList<DiffDocumentSection> Sections, string Explanation);

/// <summary>One entry in the history back and forward stacks: the state needed to recreate the document the user was looking at.</summary>
/// <remarks>Recorded whenever a document is shown and updated when the selected file changes. Replay dispatches on <see cref="Mode"/>: <see cref="DiffDocumentMode.Current"/> reopens the version's layer diff, <see cref="DiffDocumentMode.SinceReviewed"/> and <see cref="DiffDocumentMode.BetweenVersions"/> rebuild the version comparison, and <see cref="DiffDocumentMode.FileEvolution"/> rebuilds the path's evolution. <see cref="VersionId"/> is null for file evolution, and <c>Path</c> can be null when the document was not opened on a file.</remarks>
/// <param name="ChangeId">Change graph node the document belongs to.</param>
/// <param name="VersionId">Observed version the document is anchored at; null when the document is not version-specific.</param>
/// <param name="Path">File to reselect after rebuilding; null when no file was selected.</param>
/// <param name="Mode">Comparison to rebuild.</param>
public sealed record NavigationTarget(string ChangeId, string? VersionId, string? Path, DiffDocumentMode Mode);
