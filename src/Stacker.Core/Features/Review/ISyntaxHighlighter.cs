using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Optional syntax coloring service; callers can render plain text while tokenization is pending or unavailable.</summary>
public interface ISyntaxHighlighter
{
    /// <summary>Tokenizes one file version and returns spans per one-based line number.</summary>
    /// <remarks>Highlighting is optional: the shipped implementation answers with an empty map, not a problem, when the
    /// extension has no grammar, the file exceeds its size or line bound, or tokenization runs past its time budget, so
    /// every caller must be able to render the plain text. Only a genuine adapter failure is returned as a problem.</remarks>
    /// <param name="blobId">Blob object id, used as the content identity so two revisions of the same bytes share work.</param>
    /// <param name="path">File path whose extension selects the grammar; unknown extensions yield no spans.</param>
    /// <param name="text">File content to tokenize.</param>
    /// <param name="theme">Theme name selecting the color palette.</param>
    /// <param name="ct">Cancels tokenization; cancellation is rethrown rather than reported as a problem.</param>
    /// <returns>Spans keyed by one-based line number; a line missing from the map is rendered as plain text.</returns>
    Task<Result<IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>>> HighlightAsync(string blobId, string path, string text, string theme, CancellationToken ct = default);
}
