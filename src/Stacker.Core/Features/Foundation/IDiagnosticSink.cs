using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Appends bounded operation metadata and exports the retained records.</summary>
/// <remarks>Recording sits on the operation path, so it must be cheap and must never change the operation's outcome:
/// failures come back as problems instead of exceptions.</remarks>
public interface IDiagnosticSink
{
    /// <summary>Appends one record to the retained metadata log.</summary>
    /// <param name="entry">Record to append; it should carry identifiers and measurements only.</param>
    /// <returns>A successful empty result, or a failed result carrying the store's write code.</returns>
    Result<Unit> Record(DiagnosticEvent entry);
    /// <summary>Copies the retained log files into <paramref name="destination"/> so they can be handed to support.</summary>
    /// <param name="destination">Folder that receives the copies; the retained logs themselves are left in place.</param>
    /// <param name="ct">Cancels the copy.</param>
    /// <returns>The destination folder path.</returns>
    Task<Result<string>> ExportAsync(string destination, CancellationToken ct = default);
}
