// Purpose: Examples specifying which pull request each stack layer needs, matched only by repository and branch identity.
using Stacker.Core;
namespace Stacker.Tests;
public sealed class PullRequestPublicationTests
{
    private static readonly StackDefinition Stack = new("s", "Stack", "refs/heads/main", ["refs/heads/A", "refs/heads/B", "refs/heads/C"]);
    private static PullRequest Pr(long number, string baseRef, string headRef, long headRepo = 1) =>
        new(number, "PR " + number, 1, baseRef, new('a', 40), headRepo, headRef, new('b', 40), false, "https://example.invalid/" + number);

    // Scenario: A stack with no pull requests needs one per layer, each into its own parent.
    [Fact] public void A_stack_without_pull_requests_creates_one_per_layer()
    {
        var plan = PullRequestPlanner.Plan(Stack, [], 1);
        Assert.True(plan.CanPublish); Assert.Equal(3, plan.PendingCount);
        Assert.All(plan.Steps, step => Assert.Equal(PullRequestAction.Create, step.Action));
        Assert.Equal("main", plan.Steps[0].ParentName); Assert.Equal("A", plan.Steps[1].ParentName); Assert.Equal("B", plan.Steps[2].ParentName);
        Assert.Equal("Create a pull request into A", plan.Steps[1].Status);
    }
    // Scenario: Existing pull requests are kept, retargeted only when their base is wrong.
    [Fact] public void Existing_pull_requests_are_kept_and_retargeted_only_when_the_base_is_wrong()
    {
        var plan = PullRequestPlanner.Plan(Stack, [Pr(7, "main", "A"), Pr(8, "main", "B"), Pr(9, "B", "C")], 1);
        Assert.Equal(1, plan.PendingCount);
        Assert.Equal(PullRequestAction.Unchanged, plan.Steps[0].Action); Assert.Equal(7, plan.Steps[0].Number);
        Assert.Equal(PullRequestAction.UpdateBase, plan.Steps[1].Action); Assert.Equal("main", plan.Steps[1].CurrentBase);
        Assert.Equal(PullRequestAction.Unchanged, plan.Steps[2].Action);
        Assert.Equal("#8 · base main → A", plan.Steps[1].Status);
    }
    // Scenario: Pull requests from a fork or another branch are never matched to a layer.
    [Fact] public void Pull_requests_from_a_fork_or_another_branch_are_not_matched()
    {
        var plan = PullRequestPlanner.Plan(Stack, [Pr(7, "main", "A", headRepo: 2), Pr(8, "main", "other")], 1);
        Assert.Equal(3, plan.PendingCount); Assert.All(plan.Steps, step => Assert.Equal(PullRequestAction.Create, step.Action));
    }
    // Scenario: Ambiguous and non-local layers are refused instead of guessed.
    [Fact] public void Ambiguous_and_non_local_layers_are_refused()
    {
        var duplicates = PullRequestPlanner.Plan(Stack, [Pr(7, "main", "A"), Pr(8, "main", "A")], 1);
        Assert.False(duplicates.CanPublish); Assert.Contains("2 open pull requests", Assert.Single(duplicates.Blockers));
        var remote = new StackDefinition("s", "Stack", "refs/heads/main", ["refs/remotes/origin/A", "refs/heads/B"]);
        var plan = PullRequestPlanner.Plan(remote, [], 1);
        Assert.False(plan.CanPublish); Assert.Contains("is not a local branch", Assert.Single(plan.Blockers));
        Assert.Equal(PullRequestAction.Unchanged, plan.Steps[0].Action);
    }
    // Scenario: A remote-tracking base is compared and sent as the branch GitHub knows, not as "origin/main".
    [Fact] public void A_remote_tracking_base_is_normalized_to_its_branch_name()
    {
        var stack = new StackDefinition("s", "Stack", "refs/remotes/origin/main", ["refs/heads/A"]);
        var plan = PullRequestPlanner.Plan(stack, [Pr(7, "main", "A")], 1);
        Assert.Equal(PullRequestAction.Unchanged, plan.Steps[0].Action);
        Assert.Equal("main", plan.Steps[0].ParentName);
        Assert.Equal(PullRequestAction.Create, PullRequestPlanner.Plan(stack, [], 1).Steps[0].Action);
    }

    // Scenario: A complete stack has nothing to publish.
    [Fact] public void A_complete_stack_has_nothing_to_publish()
    {
        var plan = PullRequestPlanner.Plan(Stack, [Pr(7, "main", "A"), Pr(8, "A", "B"), Pr(9, "B", "C")], 1);
        Assert.False(plan.CanPublish); Assert.Equal(0, plan.PendingCount); Assert.Empty(plan.Blockers);
    }
}
