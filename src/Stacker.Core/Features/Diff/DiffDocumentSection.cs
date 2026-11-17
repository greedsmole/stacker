namespace Stacker.Core;

/// <summary>One independently rendered comparison inside a history document, pinned to a root and an exact commit pair.</summary>
/// <remarks>Sections are rendered in the order given; each supplies its own file list and diff view. A version comparison usually contributes two sections (base changes, then own changes after replay), while file evolution contributes one per layer. <see cref="BaseSha"/> and <see cref="HeadSha"/> are full commit ids used verbatim for patch reads, so earlier sections stay stable while later ones are built.</remarks>
/// <param name="Title">Heading of the section; also used as the comparison's layer label.</param>
/// <param name="Root">Repository root that patch reads use.</param>
/// <param name="BaseSha">Left side of the comparison.</param>
/// <param name="HeadSha">Right side of the comparison.</param>
/// <param name="Files">Files of this comparison, each loaded on demand; the section does not truncate the list.</param>
public sealed record DiffDocumentSection(string Title, string Root, string BaseSha, string HeadSha, IReadOnlyList<FileChange> Files);
