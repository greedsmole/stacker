using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Stores per-user application preferences outside the repository.</summary>
/// <remarks>Preferences are not part of a repository's history, so they live in application data and are saved as one document. There is no
/// revision parameter: the shell is the only writer in normal use, and a save replaces the whole object. An absent document loads as
/// defaults; a damaged one is preserved and reported by the adapter instead of being deleted, and a document written by a newer schema
/// blocks later writes so an older build cannot drop fields it does not understand.</remarks>
public interface ISettingsStore
{
    /// <summary>Reads the stored preferences, falling back to defaults when nothing usable is stored.</summary>
    /// <remarks>Defaults are the property initializers on <see cref="AppSettings"/>, so the loaded object is always complete and safe to bind.
    /// Loading also normalizes values that cannot be trusted - window and column sizes, theme name - which is why callers do not need to
    /// re-validate what they read.</remarks>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>A complete settings object; never null.</returns>
    Task<Result<AppSettings>> LoadAsync(CancellationToken ct = default);
    /// <summary>Replaces the stored preferences with one complete object.</summary>
    /// <remarks>The write is atomic and serialized inside the adapter, so a crash leaves the previous document readable. A document from a
    /// newer schema is preserved: once a load has reported it, writes stay blocked with a problem that explains the version conflict.</remarks>
    /// <param name="settings">The object to store; it is stamped with the current schema version.</param>
    /// <param name="ct">Cancels before the replace.</param>
    /// <returns><see cref="Unit"/> when the document was replaced.</returns>
    Task<Result<Unit>> SaveAsync(AppSettings settings, CancellationToken ct = default);
}
