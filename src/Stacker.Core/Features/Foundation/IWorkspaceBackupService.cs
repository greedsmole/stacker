using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Exports and restores a workspace profile, and saves unsent review text when closing cannot persist it.</summary>
/// <remarks>A backup carries the SQLite history database plus the application's settings, journal, workspace and draft
/// folders and, when a repository identity is supplied, a Git bundle of that repository's history pins. It never
/// contains the user's checkout, ordinary branches or credentials. Export writes manifest schema 2; restore accepts
/// schema 1 and 2, validates the manifest, per-file hashes, database integrity and foreign keys, and writes into a
/// separate empty profile so a bad archive cannot damage the workspace that is currently open. Every method reports
/// failure through its result instead of throwing.</remarks>
public interface IWorkspaceBackupService
{
    /// <summary>Writes a profile archive and returns its path.</summary>
    /// <param name="destination">Archive file to create or replace. The implementation rejects a destination inside the
    /// active profile, and asks for an export location outside it.</param>
    /// <param name="repository">Repository whose history pins should be bundled. <see langword="null"/> exports
    /// metadata only, and restoring such an archive needs the original repository for any code that was never
    /// bundled.</param>
    /// <param name="ct">Cancels file copying and hashing.</param>
    /// <returns>The archive path on success.</returns>
    Task<Result<string>> ExportAsync(string destination, RepositoryIdentity? repository = null, CancellationToken ct = default);
    /// <summary>Restores an archive into a new, empty profile folder.</summary>
    /// <param name="archive">Archive produced by <see cref="ExportAsync"/>.</param>
    /// <param name="emptyDestination">Folder for the restored copy. It must be absent or empty; existing content is
    /// never merged or overwritten.</param>
    /// <param name="ct">Cancels extraction and verification.</param>
    /// <returns>The restored profile root, suitable for <see cref="OpenRestoredWorkspace"/>.</returns>
    /// <remarks>A non-empty destination is rejected before anything is extracted. Verification failures — missing
    /// manifest, unsupported schema, missing entry, checksum mismatch, failed integrity check or broken references —
    /// leave the destination untouched and are returned as a failed result. Code that exists only as history pins in
    /// another repository needs that repository or the bundled pins before it can be read.</remarks>
    Task<Result<string>> RestoreAsync(string archive, string emptyDestination, CancellationToken ct = default);
    /// <summary>Saves unsent review text as plain text so a failed close does not lose it.</summary>
    /// <param name="destination">File chosen by the user; it must lie outside the active profile.</param>
    /// <param name="text">Text to write, composed by the caller from the unsaved drafts.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>A successful empty result.</returns>
    /// <remarks>This is a last-resort copy for a close that cannot persist drafts, not a backup: nothing reads it back
    /// automatically, and saving to the same path again replaces it.</remarks>
    Task<Result<Unit>> SaveRecoveryTextAsync(string destination, string text, CancellationToken ct = default);
    /// <summary>Starts a separate application instance that uses a restored profile instead of reloading this one.</summary>
    /// <param name="root">Restored profile root returned by <see cref="RestoreAsync"/>.</param>
    /// <returns>A successful empty result when the new instance was started; a failed result when the executable cannot
    /// be located or the launch fails.</returns>
    /// <remarks>Deliberately not a reload: this profile and its in-memory state are left as they are, and the restored
    /// copy is opened by another process pointed at <paramref name="root"/>. The call returns as soon as that process is
    /// started, so it says nothing about whether the restored profile will open successfully; the caller decides whether
    /// to close the current window.</remarks>
    Result<Unit> OpenRestoredWorkspace(string root);
}
