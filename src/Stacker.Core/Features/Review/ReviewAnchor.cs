using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>GitHub comment target tied to the pull request version and a specific side and line range of its diff.</summary>
/// <remarks>
/// GitHub addresses review comments by commit id, path, line and side, never by the legacy positional offset, so both
/// SHAs travel with the anchor and are revalidated against the live pull request before anything is posted. A
/// single-line anchor leaves <see cref="StartLine"/> and <see cref="StartSide"/> null; a range sets them to the first
/// line and side, while <see cref="Line"/> always stays the last line.
/// </remarks>
/// <param name="PullRequest">Number of the pull request the diff belongs to.</param>
/// <param name="HeadSha">Head commit the selection was made against.</param>
/// <param name="BaseSha">Base commit the selection was made against, used to detect a rebased or retargeted pull request.</param>
/// <param name="Path">Repository-relative path of the changed file.</param>
/// <param name="Side"><c>LEFT</c> for a removed line, <c>RIGHT</c> for an added or unchanged line.</param>
/// <param name="Line">Last line of the selection, counted on its own side.</param>
/// <param name="StartLine">First line of a multi-line selection, or null for a single-line comment.</param>
/// <param name="StartSide">Side of <paramref name="StartLine"/>; null when the selection is a single line.</param>
public sealed record ReviewAnchor(long PullRequest, string HeadSha, string BaseSha, string Path, string Side,
    int Line, int? StartLine = null, string? StartSide = null)
{
    [JsonIgnore]
    public string LocationLabel => StartLine is int start && start != Line
        ? $"{Path}:{start}–{Line} · {Side}"
        : $"{Path}:{Line} · {Side}";
}
