using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Strips Stacker's own publication marker from text that is about to be displayed.</summary>
internal static class ReviewText
{
    /// <summary>Returns the body without the trailing <c>&lt;!-- stacker:marker --&gt;</c> comment that marks a send.</summary>
    /// <remarks>
    /// The marker is 32 lowercase hex characters because it is a GUID in its compact form, appended after a blank line
    /// by the review view. Only a marker at the very end of the body is removed, so a marker quoted inside the comment
    /// text stays visible rather than being trimmed as internal state; a damaged marker is also left alone instead of
    /// guessing at a prefix.
    /// </remarks>
    /// <param name="body">Raw comment body, possibly with the trailing marker.</param>
    public static string Visible(string body) => System.Text.RegularExpressions.Regex.Replace(body, @"\s*<!-- stacker:[0-9a-f]{32} -->\s*$", "");
}
