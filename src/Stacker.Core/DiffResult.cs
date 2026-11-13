namespace Stacker.Core;

/// <summary>Diff metadata and file list for one layer; BaseSha and HeadSha record the exact compared objects.</summary>
/// <param name="Layer">Full ref of the compared layer, also used as the section title.</param>
/// <param name="ParentRef">Full ref of the parent side that was requested; it is not necessarily the merge base.</param>
/// <param name="BaseSha">Merge base the patch is taken from, so the file list shows exactly where parent and layer diverge.</param>
/// <param name="HeadSha">Commit the layer pointed at when the request was captured.</param>
/// <param name="Warning">Display-ready caveat when the merge base is not the parent tip, otherwise null.</param>
/// <param name="Files">File metadata only; each patch is requested later, per file.</param>
public sealed record DiffResult(string Layer, string ParentRef, string BaseSha, string HeadSha,
    string? Warning, IReadOnlyList<FileChange> Files)
{
    /// <summary>Total added lines across the listed files, with a binary file's null count counted as zero.</summary>
    /// <remarks>The sum describes textual changes only, so it matches the file rows the user can open rather than raw byte growth.</remarks>
    public int Additions => Files.Sum(f => f.Additions ?? 0);
    /// <summary>Total removed lines across the listed files, with a binary file's null count counted as zero.</summary>
    public int Deletions => Files.Sum(f => f.Deletions ?? 0);
}
