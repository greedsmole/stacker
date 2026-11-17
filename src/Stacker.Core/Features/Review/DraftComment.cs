using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One line comment the user has written but not yet submitted as part of a review.</summary>
/// <param name="Anchor">The exact pull request version and diff range the comment targets.</param>
/// <param name="Body">Raw Markdown body, including any suggestion fence.</param>
public sealed record DraftComment(ReviewAnchor Anchor, string Body)
{
    /// <summary>Local identity of the comment, used to match it across draft edits and to clear only what was published.</summary>
    /// <remarks>A GUID in compact form. Reanchoring replaces the anchor through a <c>with</c> expression, which keeps this
    /// id, so a submitted review can still tell which draft comment it consumed.</remarks>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}
