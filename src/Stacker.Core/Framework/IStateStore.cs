using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>Reads and writes one JSON document per identity without ever throwing at the caller.</summary>
/// <remarks>Reads never throw: IO failures are problems, absent documents are <see langword="null"/>, and damaged
/// JSON is reported as <see cref="ProblemCodes.StoreCorrupt"/>. The file-backed store names each document by the
/// hashed identity and writes through <see cref="IAtomicFile"/>; it can also quarantine a damaged document so the user
/// can inspect it. The journal store disables quarantine on purpose, because a damaged recovery record must stay in
/// place and block writes instead of being reset to an empty history.</remarks>
/// <typeparam name="T">The document type. A reference type because absence is represented by
/// <see langword="null"/>.</typeparam>
public interface IStateStore<T> where T : class
{
    /// <summary>Returns the stored value, or null when absent; a corrupt document is quarantined, not thrown.</summary>
    /// <param name="identity">The resource identity used to derive the document path.</param>
    /// <returns>The value, <see langword="null"/> when no document exists, or a read/corrupt problem.</returns>
    Result<T?> Read(string identity);
    /// <summary>Serializes the value and stores it atomically.</summary>
    /// <param name="identity">The resource identity used to derive the document path.</param>
    /// <param name="value">The document to persist.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>Success when the document landed; otherwise <see cref="ProblemCodes.StoreWrite"/>.</returns>
    Task<Result<Unit>> WriteAsync(string identity, T value, CancellationToken ct = default);
    /// <summary>Moves a damaged document aside so the user can inspect it, and reports where it went.</summary>
    /// <remarks>The file-backed store appends a UTC timestamp and a <c>.bad</c> suffix, so repeated damage never
    /// overwrites an earlier preserved copy. An implementation responsible for critical recovery evidence may refuse
    /// instead of moving it, so that the damaged document stays in place and keeps blocking writes.</remarks>
    /// <param name="identity">The resource identity whose document should be moved.</param>
    /// <returns>The new path, an empty string when there was no document, or a problem when the move was refused or
    /// failed.</returns>
    Result<string> Quarantine(string identity);
}
