using System.Text.RegularExpressions;
using Stacker.Core.Framework;
namespace Stacker.Core;

public sealed class SearchRequest
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    public const int MaxResults = 500;
    private readonly string _query;
    private readonly Regex? _pattern;
    private SearchRequest(string query, Regex? pattern) { _query = query; _pattern = pattern; }
    public static Result<SearchRequest> Create(string query, bool regex)
    {
        if (string.IsNullOrWhiteSpace(query)) return Result<SearchRequest>.Fail(ProblemCodes.StateInvalid, "Enter a file name or text to search.");
        try { return Result<SearchRequest>.Ok(new(query.Trim(), regex ? new Regex(query.Trim(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : null)); }
        catch (ArgumentException ex) { return Result<SearchRequest>.Fail(ProblemCodes.StateInvalid, "Invalid regex: " + ex.Message); }
    }
    public bool Matches(string text) => _pattern?.IsMatch(text) ?? text.Contains(_query, StringComparison.OrdinalIgnoreCase);
}
