// Purpose: Real-Git checks that publishing a restack uses a lease and never overwrites a remote branch that moved.
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class PushTests
{
    private static GitBranchWriter Writer(GitFixture f) => f.Writer();

    /// <summary>Publishes the stack, moves the base, and restacks it, leaving a plan whose push steps are ready.</summary>
    private static async Task<(GitBranchWriter Writer, RestackPlan Plan)> RestackedStack(GitFixture f)
    {
        await f.Linear(); await f.AddOrigin();
        await f.Git("push", "origin", "main", "A", "B", "C");
        await f.Git("checkout", "main"); await f.Write("main2.txt", "moved\n"); await f.Commit("base moved");
        var writer = Writer(f);
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Blockers));
        Assert.True((await writer.RestackAsync(plan)).Applied);
        return (writer, plan);
    }

    // Scenario: A restacked stack is published under a lease and the remote matches the local branches.
    [Fact] public async Task Push_publishes_restacked_layers_under_a_lease()
    {
        await using var f = new GitFixture();
        var (writer, plan) = await RestackedStack(f);
        Assert.All(plan.PushSteps, step => Assert.NotNull(step.RemoteTip));
        Assert.Equal(new[] { "A", "B", "C" }, await writer.PushAsync(plan));
        foreach (var branch in new[] { "A", "B", "C" })
            Assert.Equal(await f.Git("rev-parse", branch), await f.RemoteSha("refs/heads/" + branch));
        Assert.Equal(JournalPhase.Applied, (await writer.LastJournalAsync(await f.ResolvedRoot()))?.Phase);
    }

    // Scenario: A branch that moved on the remote is never overwritten, and the remaining layers are left alone.
    [Fact] public async Task Push_refuses_when_a_remote_branch_moved_after_the_preview()
    {
        await using var f = new GitFixture();
        var (writer, plan) = await RestackedStack(f);
        var stranger = await f.RemoteSha("refs/heads/B");
        var untouched = await f.RemoteSha("refs/heads/B");
        await f.MoveRemote("refs/heads/A", stranger);
        var error = await Assert.ThrowsAsync<StackerException>(() => writer.PushAsync(plan));
        Assert.Contains("moved on origin", error.Message);
        Assert.Equal(stranger, await f.RemoteSha("refs/heads/A"));
        Assert.Equal(untouched, await f.RemoteSha("refs/heads/B"));
        Assert.Equal(JournalPhase.Applied, (await writer.LastJournalAsync(await f.ResolvedRoot()))?.Phase);
    }

    // Scenario: A layer that was never published is pushed without a lease.
    [Fact] public async Task Push_publishes_a_new_branch_without_a_lease()
    {
        await using var f = new GitFixture(); await f.Linear(); await f.AddOrigin();
        await f.Git("push", "origin", "main");
        await f.Git("checkout", "main"); await f.Write("main2.txt", "moved\n"); await f.Commit("base moved");
        var writer = Writer(f);
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.All(plan.PushSteps, step => Assert.Null(step.RemoteTip));
        Assert.True((await writer.RestackAsync(plan)).Applied);
        Assert.Equal(new[] { "A", "B", "C" }, await writer.PushAsync(plan));
        foreach (var branch in new[] { "A", "B", "C" })
            Assert.Equal(await f.Git("rev-parse", branch), await f.RemoteSha("refs/heads/" + branch));
    }

    // Scenario: Without an origin the restack stays local and pushing is refused.
    [Fact] public async Task Push_without_an_origin_is_refused()
    {
        await using var f = new GitFixture(); await f.Linear();
        var writer = Writer(f);
        var plan = await writer.PlanAsync(await f.Reader.OpenAsync(f.Root), f.Stack);
        Assert.False(plan.HasOrigin);
        Assert.Contains("no origin remote", (await Assert.ThrowsAsync<StackerException>(() => writer.PushAsync(plan))).Message);
    }
}
