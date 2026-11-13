using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One ordinary PR conversation comment, separate from review decisions and code threads.</summary>
/// <param name="Id">Server id. For a thread comment this is the database id used as the reply target; for a conversation
/// comment it is the issue-comment id used for display.</param>
/// <param name="Author">Login of the comment author, or <c>deleted</c> when GitHub no longer reports an account.</param>
/// <param name="Body">Raw Markdown body, which may still contain Stacker's trailing publication marker.</param>
/// <param name="Url">Server page URL for opening the comment in a browser.</param>
/// <param name="CreatedAt">Server creation timestamp; parsed as the minimum value if GitHub omits it.</param>
public sealed record DiscussionComment(string Id, string Author, string Body, string Url, DateTimeOffset CreatedAt)
{
    /// <summary>The body as the review UI renders it, with Stacker's trailing publication marker removed.</summary>
    public string DisplayBody => ReviewText.Visible(Body);
}
