using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One reviewer submission summary such as approved, commented, or requested changes.</summary>
/// <param name="Id">Server review id.</param>
/// <param name="Author">Login of the reviewer.</param>
/// <param name="Body">Review summary Markdown, often empty for an approval.</param>
/// <param name="State">GitHub review state such as <c>APPROVED</c>, <c>COMMENTED</c> or <c>CHANGES_REQUESTED</c>.</param>
/// <param name="SubmittedAt">Submission timestamp; null for a pending review.</param>
public sealed record ReviewSummary(string Id, string Author, string Body, string State, DateTimeOffset? SubmittedAt)
{
    /// <summary>The summary as the review UI renders it, with Stacker's trailing publication marker removed.</summary>
    public string DisplayBody => ReviewText.Visible(Body);
}
