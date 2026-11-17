using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Application-owned documents kept outside the user's repository, addressed by kind and identity.</summary>
/// <remarks>The profile implementation maps <c>kind</c> to a folder under the application data root and hashes
/// <c>identity</c> into a name, so an identity only has to be stable and unique, not a valid path. Reads never throw
/// for missing or damaged data: an absent document is a successful <see langword="null"/>, a damaged one is
/// quarantined and returned as a failure that is also reported through <see cref="LastProblem"/>. Writes replace the
/// whole document, and this port has no expected-revision check, so a caller that needs optimistic concurrency
/// carries its own marker (as <see cref="StackViewState.Revision"/> does for workspace stack views).</remarks>
public interface IApplicationState
{
    /// <summary>Absolute root of the application data profile that every <c>kind</c> folder lives under.</summary>
    /// <remarks>For callers that need one shared location rather than a single document: generated demo repositories
    /// are created beneath it and the profile backup walks it. The transient implementation reports
    /// <c>"transient"</c>, which is relative, so it is only meaningful to a host that never persists anything.</remarks>
    string Root
    {
        get;
    }
    /// <summary>The most recent read or write problem, kept so the UI can report it after the failing call returned.</summary>
    /// <remarks>Advisory only; the failure itself is also returned by the call that saw it. Set when a document was
    /// damaged or a write failed, and when the legacy profile import could not complete. It is not cleared on
    /// success, so it describes the last problem rather than a live state.</remarks>
    string? LastProblem
    {
        get;
    }
    /// <summary>Reads one document, or reports no document when nothing has been stored under this identity.</summary>
    /// <typeparam name="T">Reference type of the stored document; a value type could not express "absent".</typeparam>
    /// <param name="kind">Document family, which the profile implementation turns into a folder name.</param>
    /// <param name="identity">Stable logical key of the document within its family.</param>
    /// <param name="ct">Cancels the read where the implementation can honor it.</param>
    /// <returns>The document, <see langword="null"/> when it does not exist, or a problem when it exists but could
    /// not be read or parsed.</returns>
    /// <remarks>A damaged document is moved aside by the profile implementation and reported with the corrupt-store
    /// code, so a caller can fall back to defaults instead of being locked out of its repository.</remarks>
    Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class;
    /// <summary>Replaces one document and returns the outcome instead of throwing.</summary>
    /// <typeparam name="T">Reference type of the stored document.</typeparam>
    /// <param name="kind">Document family, which the profile implementation turns into a folder name.</param>
    /// <param name="identity">Stable logical key of the document within its family.</param>
    /// <param name="value">The complete document to persist; nothing is merged with the previous content.</param>
    /// <param name="ct">Cancels the write before it replaces the previous document.</param>
    /// <returns><see cref="Unit"/> once the replacement landed, or a problem whose message is ready to display.</returns>
    /// <remarks>Writes are serialized by the profile implementation, so queued saves replace each other in order
    /// rather than interleaving. A reader initiated at the same time can still observe either the old or the new
    /// document, never a partially written one.</remarks>
    Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class;
}
