using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Distinct collections of PR conversation, reviewer submissions, and code threads.</summary>
/// <remarks>The three are separate GitHub objects with separate actions; merging them into one list would make a reply,
/// a resolve and a review decision look alike in the UI.</remarks>
/// <param name="Comments">Conversation comments on the pull request.</param>
/// <param name="Reviews">Review submissions with their decisions and summaries.</param>
/// <param name="Threads">Code threads, including file-level and outdated ones.</param>
public sealed record ReviewDiscussion(IReadOnlyList<DiscussionComment> Comments, IReadOnlyList<ReviewSummary> Reviews, IReadOnlyList<ReviewThread> Threads);
