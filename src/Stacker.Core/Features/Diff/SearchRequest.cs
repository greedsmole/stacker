using System.Text.RegularExpressions;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>A validated, reusable search request: either a literal query or a compiled regex with a bounded match time.</summary>
/// <remarks><see cref="Create"/> is the only way to construct one, so an empty or invalid query can never reach a match. The three limits act at different scopes: <see cref="Budget"/> stops the whole search, <see cref="MaxResults"/> bounds the hit list, and the 100 ms timeout carried by the compiled regex bounds a single match. Literal matching is ordinal and case-insensitive, so regex metacharacters are searched verbatim and culture-specific casing cannot change the result.</remarks>
public sealed class SearchRequest
{
    /// <summary>Wall-clock deadline for one whole search, covering every section, file and match.</summary>
    /// <remarks>The caller creates a cancellation source from this value and links it with the user's cancellation; when it expires the remaining work is cancelled, the partial hits are kept, and the UI reports "stopped after 5 s, narrow the search". Five seconds is shared on purpose: the per-match 100 ms bound can still add up to minutes across a large stack, so a per-match limit alone would not cap the whole operation.</remarks>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    /// <summary>Maximum number of hits a search returns; reaching it stops the search and marks the list truncated.</summary>
    /// <remarks>The caller stops adding hits once the count reaches 500, breaking out of the current file, and appends "first 500 shown" to the status. The bound applies to the collection handed to the UI rather than to work already done, and reaching the cap marks the result truncated even if no further hit exists. The exact provenance of 500 is not recorded in the repository; it is the only result-count bound in the search path.</remarks>
    public const int MaxResults = 500;
    /// <summary>Trimmed query text: the literal to find, or the source of <see cref="_pattern"/> when regex mode was requested.</summary>
    /// <remarks>Trimming happens in <see cref="Create"/>, so surrounding whitespace neither changes a literal match nor shifts a pattern's anchors.</remarks>
    private readonly string _query;
    /// <summary>Compiled regex for a regex request, or null when the request is a literal substring search.</summary>
    /// <remarks>Constructed once in <see cref="Create"/> so a syntax error is reported before any file is read; each match can still throw <see cref="RegexMatchTimeoutException"/> when the 100 ms bound is exceeded.</remarks>
    private readonly Regex? _pattern;
    /// <summary>Stores an already validated query and its optional pattern; only <see cref="Create"/> may invoke it.</summary>
    /// <param name="query">Non-empty, already trimmed query text.</param>
    /// <param name="pattern">Compiled pattern, or null for a literal search.</param>
    private SearchRequest(string query, Regex? pattern)
    {
        _query = query;
        _pattern = pattern;
    }
    /// <summary>Validates the user's text and builds a request, returning a user-facing problem instead of throwing.</summary>
    /// <remarks>Blank input is rejected before any regex work (an empty literal would otherwise match every path and line), and the query is trimmed so surrounding spaces do not silently change the search. Regex mode compiles once with <see cref="RegexOptions.IgnoreCase"/> and <see cref="RegexOptions.CultureInvariant"/> and a 100 ms match timeout; a syntax error from the <see cref="Regex"/> constructor becomes "Invalid regex: ...". The pattern is created before any file is read, so a bad pattern costs no diff work.</remarks>
    /// <param name="query">Raw user input; blank input is rejected.</param>
    /// <param name="regex">Whether to interpret the trimmed query as a regular expression.</param>
    /// <returns>The request, or a StateInvalid problem whose message is ready for display.</returns>
    public static Result<SearchRequest> Create(string query, bool regex)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result<SearchRequest>.Fail(ProblemCodes.StateInvalid, "Enter a file name or text to search.");
        try
        {
            return Result<SearchRequest>.Ok(new(query.Trim(), regex ? new Regex(query.Trim(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : null));
        }
        catch (ArgumentException ex) { return Result<SearchRequest>.Fail(ProblemCodes.StateInvalid, "Invalid regex: " + ex.Message); }
    }
    /// <summary>Tests one path or changed line against the request's literal text or pattern.</summary>
    /// <remarks>A regex request is case-insensitive and can throw <see cref="RegexMatchTimeoutException"/> when a single match exceeds 100 ms; callers count that per file and continue. A literal request uses ordinal case-insensitive containment, so a query containing regex syntax is matched character for character. Callers run this off the UI thread so a slow pattern cannot freeze the window.</remarks>
    /// <param name="text">Path or line text to test.</param>
    /// <returns>True when the text matches.</returns>
    /// <exception cref="RegexMatchTimeoutException">The pattern did not finish within the 100 ms match timeout.</exception>
    public bool Matches(string text) => _pattern?.IsMatch(text) ?? text.Contains(_query, StringComparison.OrdinalIgnoreCase);
}
