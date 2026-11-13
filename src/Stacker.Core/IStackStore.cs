using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Persists user-authored stack definitions in the repository configuration.</summary>
/// <remarks>Revisioned rather than locked for its whole lifetime: a load returns the revision of the bytes it read, and a save
/// must present that revision so a second window or an external editor is not overwritten silently. A write takes an exclusive
/// lock kept outside the working tree and replaces the configuration atomically, so a crash cannot leave a half-written document
/// and a failed save leaves the previous one intact.</remarks>
public interface IStackStore
{
    /// <summary>Loads and validates definitions while capturing a revision for optimistic concurrency.</summary>
    /// <remarks>An absent configuration is not a failure: it loads as an empty document with a null revision, and a save can then
    /// create the document. Stored definitions are validated with the same rules as saving, so a hand-edited document that would
    /// break the workspace is reported with the file untouched instead of rendering a broken stack list.</remarks>
    /// <param name="root">Repository root that owns the configuration.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The definitions plus the revision of the bytes that produced them; null when nothing is stored.</returns>
    Task<Result<StackDocument>> LoadAsync(string root, CancellationToken ct = default);
    /// <summary>Saves only when the on-disk revision still matches the revision last read.</summary>
    /// <remarks>Three checks run in order: the definitions are validated, the revision read back from disk is compared with
    /// <c>expectedRevision</c>, and the bytes are compared again immediately before the atomic replace, so an edit that lands
    /// between validation and writing is still detected. A null revision on disk means no document exists, so a first save is
    /// allowed even when the caller has nothing to compare against; a null expected revision against an existing document is a
    /// stale-save problem. The returned document carries the new revision, so the caller can save again without reloading.</remarks>
    /// <param name="root">Repository root that owns the configuration.</param>
    /// <param name="stacks">Every definition to write; the document is replaced as a whole.</param>
    /// <param name="expectedRevision">The revision returned by the last load, or null when the caller believes nothing exists.</param>
    /// <param name="ct">Cancels before the replace; a cancelled save leaves the previous document intact.</param>
    /// <returns>The saved definitions and their new revision.</returns>
    Task<Result<StackDocument>> SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct = default);
}
