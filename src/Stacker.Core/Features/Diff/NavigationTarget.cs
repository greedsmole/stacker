namespace Stacker.Core;

/// <summary>One entry in the history back and forward stacks: the state needed to recreate the document the user was looking at.</summary>
/// <remarks>Recorded whenever a document is shown and updated when the selected file changes. Replay dispatches on <see cref="Mode"/>: <see cref="DiffDocumentMode.Current"/> reopens the version's layer diff, <see cref="DiffDocumentMode.SinceReviewed"/> and <see cref="DiffDocumentMode.BetweenVersions"/> rebuild the version comparison, and <see cref="DiffDocumentMode.FileEvolution"/> rebuilds the path's evolution. <see cref="VersionId"/> is null for file evolution, and <c>Path</c> can be null when the document was not opened on a file.</remarks>
/// <param name="ChangeId">Change graph node the document belongs to.</param>
/// <param name="VersionId">Observed version the document is anchored at; null when the document is not version-specific.</param>
/// <param name="Path">File to reselect after rebuilding; null when no file was selected.</param>
/// <param name="Mode">Comparison to rebuild.</param>
public sealed record NavigationTarget(string ChangeId, string? VersionId, string? Path, DiffDocumentMode Mode)
{
    /// <summary>The displayed section and path, which can differ from the current layer's file.</summary>
    public int? SectionIndex
    {
        get; init;
    }
    public string? SectionPath
    {
        get; init;
    }
}
