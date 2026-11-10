namespace Stacker.Core;

/// <summary>Builds the offline review discussion shown for the bundled demo repository, where no GitHub host is reachable.</summary>
/// <remarks>
/// The demo must exercise the same presentation paths as a live pull request, so the sample discussion deliberately
/// contains one conversation comment, one review submission, a current line thread with a suggestion fence, a
/// file-level thread and an outdated thread that no longer has a line anchor. Without the outdated and file-level
/// shapes the UI would never render those branches offline, and a defect there would only appear against a real host.
/// </remarks>
public static class DemoReviewData
{
    /// <summary>Creates a review discussion pinned to the demo pull request's current version.</summary>
    /// <remarks>
    /// Every anchor copies the pull request's head and base SHAs, so the stale-version checks behave exactly as they do
    /// for a live pull request; the line suggestion is ordinary Markdown that the suggestion parser must recognize. Ids
    /// are synthetic strings because the demo repository has no server objects behind them, and the timestamps are
    /// stamped at call time so the discussion looks current. The file and outdated threads mirror the two states GitHub
    /// reports when a thread has a path but no line.
    /// </remarks>
    /// <param name="pr">Demo pull request whose number and version SHAs the sample anchors copy.</param>
    public static ReviewDiscussion Discussion(PullRequest pr) => new(
        [new("1", "reviewer", "Please check the contribution of this layer, then compare the entire stack.", pr.Url, DateTimeOffset.UtcNow)],
        [new("1", "reviewer", "The shape looks good; one line needs clarification.", "COMMENTED", DateTimeOffset.UtcNow)],
        [new("demo-current", false, false, false, new(pr.Number, pr.HeadSha, pr.BaseSha, "Auth.cs", "RIGHT", 4), [new("2", "reviewer", "Should authentication failure be represented explicitly?\n\n```suggestion\n    public static string Login() => throw new UnauthorizedAccessException();\n```", pr.Url, DateTimeOffset.UtcNow)]),
         new("demo-file", false, false, false, null, [new("4", "reviewer", "Could this file also document the session expiry policy?", pr.Url, DateTimeOffset.UtcNow)], "Auth.cs", true),
         new("demo-outdated", false, true, false, null, [new("3", "reviewer", "This comment refers to an older version of the file.", pr.Url, DateTimeOffset.UtcNow)], "Auth.cs")]);
}
