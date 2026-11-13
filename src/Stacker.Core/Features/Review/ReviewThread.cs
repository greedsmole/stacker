using System.Text.Json.Serialization;
using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Code-review thread, which may be resolved, outdated, or anchored to a file or line range.</summary>
/// <param name="Id">GraphQL node id used for the resolve mutation.</param>
/// <param name="IsResolved">Whether the thread is resolved on the server.</param>
/// <param name="IsOutdated">Whether the thread refers to a diff version that has since changed. GitHub may then stop
/// reporting its line, in which case the anchor is null while the path remains.</param>
/// <param name="CanResolve">Whether the active viewer may change resolution in its current state, resolved or not.</param>
/// <param name="Anchor">Line anchor for a line thread; null for a file-level thread or an outdated thread without a line.</param>
/// <param name="Comments">Thread comments in server order, with the first comment starting the thread.</param>
/// <param name="FilePath">Server-reported path, kept even when the anchor is unavailable.</param>
/// <param name="IsFileLevel">True when GitHub reports the thread's subject as a file rather than a line.</param>
public sealed record ReviewThread(string Id, bool IsResolved, bool IsOutdated, bool CanResolve, ReviewAnchor? Anchor,
    IReadOnlyList<DiscussionComment> Comments, string? FilePath = null, bool IsFileLevel = false)
{
    /// <summary>The best available path: the server path, falling back to the anchor's path.</summary>
    public string? Path => FilePath ?? Anchor?.Path;
    /// <summary>One-line thread label with its location and resolution state, as shown in the thread list.</summary>
    public string Label => $"{Path ?? "Unknown file"}{(IsFileLevel ? " · File comment" : Anchor?.Line is { } line ? $":{line}" : " · Line comment")} · {(IsOutdated ? "Outdated" : IsResolved ? "Resolved" : "Open")}";
    /// <summary>All comments flattened into an author-prefixed transcript, separated by blank lines.</summary>
    public string Text => string.Join("\n\n", Comments.Select(c => $"@{c.Author}\n{c.Body}"));
}
