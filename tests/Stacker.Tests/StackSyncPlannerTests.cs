// Purpose: Examples specifying what a restack preview promises and which repository states block it.
using Stacker.Core;
namespace Stacker.Tests;
public sealed class StackSyncPlannerTests
{
    private static string Sha(char value) => new(value, 40);
    private static readonly StackDefinition Stack = new("s", "Stack", "refs/heads/main", ["refs/heads/A", "refs/heads/B"]);
    private static RepositorySnapshot Repo(params (string Name, string Sha)[] refs) =>
        new("/repo", refs.Select(r => new BranchRef(r.Name, r.Sha)).ToArray(), 0);
    private static SyncLayerState Layer(string branch, int behind, int commits = 1, bool merges = false) => new(branch, behind, commits, merges);
    private static SyncPreflight Preflight(params SyncLayerState[] layers) => new("/repo", "main", false, null, [], layers, true);
    private static (string Name, string Sha)[] Refs(string main, string a, string b) =>
        [("refs/heads/main", main), ("refs/heads/A", a), ("refs/heads/B", b)];

    // Scenario: A stack whose parents did not move has nothing to execute.
    [Fact] public void Up_to_date_stack_has_nothing_to_execute()
    {
        var plan = StackSyncPlanner.Plan(Repo(Refs(Sha('a'), Sha('b'), Sha('c'))), Stack, Preflight(Layer("refs/heads/A", 0), Layer("refs/heads/B", 0)));
        Assert.False(plan.CanExecute); Assert.Equal(0, plan.RebaseCount); Assert.Empty(plan.Blockers); Assert.Empty(plan.PushSteps);
        Assert.All(plan.Steps, step => Assert.Equal("Up to date", step.Status));
    }
    // Scenario: A moved base rewrites every layer above it, even one that never diverged itself.
    [Fact] public void A_moved_base_rebases_every_layer_above_it()
    {
        var plan = StackSyncPlanner.Plan(Repo(Refs(Sha('a'), Sha('b'), Sha('c'))), Stack, Preflight(Layer("refs/heads/A", 1, 3), Layer("refs/heads/B", 0, 2)));
        Assert.True(plan.CanExecute); Assert.Equal(2, plan.RebaseCount);
        Assert.Equal("refs/heads/main", plan.Steps[0].ParentRef); Assert.Equal("refs/heads/A", plan.Steps[1].ParentRef);
        Assert.Equal(3, plan.Steps[0].CommitCount);
        Assert.Equal("Rebase 3 commit(s) onto main", plan.Steps[0].Status);
        Assert.Equal(new[] { "refs/heads/A", "refs/heads/B" }, plan.PushSteps.Select(s => s.Branch));
    }
    // Scenario: Only the top layer diverged, so only it moves.
    [Fact] public void Only_the_top_layer_diverged()
    {
        var plan = StackSyncPlanner.Plan(Repo(Refs(Sha('a'), Sha('b'), Sha('c'))), Stack, Preflight(Layer("refs/heads/A", 0), Layer("refs/heads/B", 1)));
        Assert.Equal(1, plan.RebaseCount); Assert.False(plan.Steps[0].NeedsRebase); Assert.True(plan.Steps[1].NeedsRebase);
    }
    // Scenario: A layer with no commits of its own follows its parent instead of being skipped.
    [Fact] public void A_layer_with_no_commits_follows_its_parent()
    {
        var plan = StackSyncPlanner.Plan(Repo(Refs(Sha('a'), Sha('b'), Sha('c'))), Stack, Preflight(Layer("refs/heads/A", 1, 2), Layer("refs/heads/B", 0, 0)));
        Assert.True(plan.Steps[1].NeedsRebase); Assert.Equal(0, plan.Steps[1].CommitCount);
        Assert.Contains("follows its parent", plan.Steps[1].Note!);
    }
    // Scenario: Unsafe repository states block execution but still show what would happen.
    [Fact] public void Unsafe_repository_states_block_execution()
    {
        var refs = Repo(Refs(Sha('a'), Sha('b'), Sha('c')));
        var layers = Preflight(Layer("refs/heads/A", 1), Layer("refs/heads/B", 1));
        Assert.False(StackSyncPlanner.Plan(refs, Stack, layers with { HasUncommittedChanges = true }).CanExecute);
        Assert.Contains("uncommitted", Assert.Single(StackSyncPlanner.Plan(refs, Stack, layers with { HasUncommittedChanges = true }).Blockers));
        Assert.Contains("A rebase", Assert.Single(StackSyncPlanner.Plan(refs, Stack, layers with { InProgressOperation = "A rebase" }).Blockers));
        Assert.Contains("detached", Assert.Single(StackSyncPlanner.Plan(refs, Stack, layers with { CurrentBranch = null }).Blockers));
        Assert.Equal(2, StackSyncPlanner.Plan(refs, Stack, layers).RebaseCount);
    }
    // Scenario: Per-layer hazards name the branch that causes them.
    [Fact] public void Per_layer_hazards_name_the_branch()
    {
        var refs = Repo(Refs(Sha('a'), Sha('b'), Sha('c')));
        var elsewhere = StackSyncPlanner.Plan(refs, Stack, Preflight(Layer("refs/heads/A", 1)) with { BranchesCheckedOutElsewhere = ["refs/heads/A"] });
        Assert.Contains("another worktree", Assert.Single(elsewhere.Blockers)); Assert.False(elsewhere.CanExecute);
        var merges = StackSyncPlanner.Plan(refs, Stack, Preflight(Layer("refs/heads/A", 1, 2, merges: true)));
        Assert.Contains("merge commits", Assert.Single(merges.Blockers));
    }
    // Scenario: A missing ref blocks the plan and every layer above it.
    [Fact] public void A_missing_ref_blocks_the_plan_and_the_layers_above_it()
    {
        var stack = new StackDefinition("s", "Stack", "refs/heads/main", ["refs/heads/A", "refs/heads/B", "refs/heads/C"]);
        var plan = StackSyncPlanner.Plan(Repo(("refs/heads/main", Sha('a')), ("refs/heads/A", Sha('b')), ("refs/heads/C", Sha('e'))), stack, Preflight(Layer("refs/heads/A", 1)));
        Assert.False(plan.CanExecute); Assert.Contains("Missing ref: refs/heads/B", Assert.Single(plan.Blockers));
        Assert.Equal("Missing ref", plan.Steps[1].Note); Assert.True(plan.Steps[1].Blocked);
        Assert.True(plan.Steps[2].Blocked); Assert.True(plan.Steps[1].OldParentTip is null);
    }
    // Scenario: A layer that is not a local branch cannot be rewritten and blocks the layers above it.
    [Fact] public void A_layer_that_is_not_a_local_branch_blocks_the_plan()
    {
        var stack = new StackDefinition("s", "Stack", "refs/heads/main", ["refs/remotes/origin/A", "refs/heads/B"]);
        var refs = Repo(("refs/heads/main", Sha('a')), ("refs/remotes/origin/A", Sha('b')), ("refs/heads/B", Sha('c')));
        var plan = StackSyncPlanner.Plan(refs, stack, Preflight(Layer("refs/remotes/origin/A", 1)));
        Assert.False(plan.CanExecute); Assert.Contains("is not a local branch", Assert.Single(plan.Blockers));
        Assert.True(plan.Steps[0].Blocked); Assert.Equal("Not a local branch", plan.Steps[0].Note);
        Assert.Contains("Blocked by a layer below", plan.Steps[1].Note!);
    }
    // Scenario: The remote commit a push must lease against is captured before anything moves.
    [Fact] public void The_remote_commit_to_lease_against_is_captured()
    {
        var refs = Repo([.. Refs(Sha('a'), Sha('b'), Sha('c')), ("refs/remotes/origin/A", Sha('d'))]);
        var plan = StackSyncPlanner.Plan(refs, Stack, Preflight(Layer("refs/heads/A", 1), Layer("refs/heads/B", 1)));
        Assert.Equal(Sha('d'), plan.PushSteps[0].RemoteTip); Assert.Null(plan.PushSteps[1].RemoteTip);
    }
}
