namespace Stacker.Core;

public static class DemoReviewData
{
    public static ReviewDiscussion Discussion(PullRequest pr) => new(
        [new("1", "reviewer", "Please check the contribution of this layer, then compare the entire stack.", pr.Url, DateTimeOffset.UtcNow)],
        [new("1", "reviewer", "The shape looks good; one line needs clarification.", "COMMENTED", DateTimeOffset.UtcNow)],
        [new("demo-current", false, false, false, new(pr.Number, pr.HeadSha, pr.BaseSha, "Auth.cs", "RIGHT", 4), [new("2", "reviewer", "Should authentication failure be represented explicitly?\n\n```suggestion\n    public static string Login() => throw new UnauthorizedAccessException();\n```", pr.Url, DateTimeOffset.UtcNow)]),
         new("demo-file", false, false, false, null, [new("4", "reviewer", "Could this file also document the session expiry policy?", pr.Url, DateTimeOffset.UtcNow)], "Auth.cs", true),
         new("demo-outdated", false, true, false, null, [new("3", "reviewer", "This comment refers to an older version of the file.", pr.Url, DateTimeOffset.UtcNow)], "Auth.cs")]);
}
