using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;

namespace Stacker.Tests;

public sealed partial class GhStackClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pinned_extension_creates_and_recovers_a_local_rebase(bool abort)
    {
        // The binary is supplied by the isolated integration run. Regular CI uses the scripted contract test below.
        var release = Environment.GetEnvironmentVariable("STACKER_GH_STACK_TEST_RELEASE");
        if (string.IsNullOrEmpty(release))
            return;
        await using var fixture = new GitFixture();
        await fixture.Init();
        var journal = fixture.CreateJournal();
        var config = Path.Combine(fixture.Root, ".git", "stacker-test-gh");
        Directory.CreateDirectory(config);
        var processes = new GhProcesses(release, config, fixture.Runner);
        GhStackClient Client() => new(processes, new(), new() { UseSavedCredentials = true }, new(new(journal)), journal);
        var target = new GhStackTarget(fixture.Root, null);
        var client = Client();

        Assert.True((await client.ProbeAsync(target)).OrThrow().CanManage);
        Assert.Null((await client.LoadAsync(target)).OrThrow().Stack);
        var created = await client.CreateAsync(target, "main", "first");
        Assert.True(created.Ok, created.Message);
        await fixture.Write("base.txt", "first\n");
        await fixture.Commit("first change");
        var added = await client.AddAsync(target, "second");
        Assert.True(added.Ok, added.Message);
        await fixture.Write("base.txt", "second\n");
        await fixture.Commit("second change");
        var second = await fixture.Git("rev-parse", "second");
        await fixture.Git("checkout", "first");
        await fixture.Write("base.txt", "first changed again\n");
        await fixture.Commit("first changed again");
        var otherWorktree = await fixture.AddWorktree();
        await fixture.GitAt(otherWorktree, "checkout", "second");
        var occupied = await client.PreviewRebaseAsync(target);
        Assert.True(occupied.IsFailed);
        Assert.Contains("second is checked out in another worktree", occupied.Problem!.Message);
        await fixture.GitAt(otherWorktree, "checkout", "--detach");
        var stale = (await client.PreviewRebaseAsync(target)).OrThrow();
        await fixture.Git("commit", "--allow-empty", "-m", "first moved again");
        Assert.Equal(CommandStatus.Refused, (await client.RebaseAsync(stale)).Status);
        var preview = (await client.PreviewRebaseAsync(target)).OrThrow();
        Assert.Equal("refs/heads/second", Assert.Single(preview.UpperLayers).Name);
        var conflict = await client.RebaseAsync(preview);
        Assert.True(conflict.NeedsUser, conflict.Message);

        // A fresh adapter sees the original journal entry and delegates recovery to the extension.
        client = Client();
        var recovery = Assert.IsType<GhStackRecovery>((await client.RecoveryAsync(fixture.Root)).OrThrow());
        Assert.True(recovery.CanAbort);
        Assert.False(recovery.CanContinue);
        if (!abort)
        {
            await fixture.Write("base.txt", "resolved\n");
            await fixture.Git("add", "--", "base.txt");
            Assert.True((await client.RecoveryAsync(fixture.Root)).OrThrow()!.CanContinue);
        }
        var finished = await client.RecoverAsync(target, recovery.OperationId, abort);
        Assert.True(finished.Ok, finished.Message);
        Assert.Null((await client.RecoveryAsync(fixture.Root)).OrThrow());
        Assert.Empty(await fixture.Git("status", "--porcelain"));
        if (abort)
            Assert.Equal(second, await fixture.Git("rev-parse", "second"));
        else
            Assert.Equal(0, await fixture.ExitCode("merge-base", "--is-ancestor", "first", "second"));
    }

    [Fact]
    public async Task Installed_earlier_version_can_read_ordered_branches_but_cannot_write()
    {
        await using var fixture = new GitFixture();
        await fixture.Linear();
        var journal = fixture.CreateJournal();
        var processes = new GhProcesses(null, fixture.Root, fixture.Runner);
        var client = new GhStackClient(processes, new(), new(), new(new(journal)), journal);
        var target = new GhStackTarget(fixture.Root, null);

        Assert.False((await client.ProbeAsync(target)).OrThrow().CanManage);
        var stack = (await client.LoadAsync(target)).OrThrow().Stack;
        Assert.Equal(new[] { "A", "B", "C" }, stack!.Branches.Select(branch => branch.Name));
        var outcome = await client.AddAsync(target, "ignored");
        Assert.Equal(CommandStatus.Refused, outcome.Status);
        Assert.Equal("C", await fixture.Git("branch", "--show-current"));
    }

    internal static GhStackClient ScriptedClient(GitFixture fixture, IJournal journal) =>
        new(new GhProcesses(null, fixture.Root, fixture.Runner, "0.3.0"), new(), new(), new(new(journal)), journal);

    internal static GhStackClient ReleaseClient(string binary, GitFixture fixture, IJournal journal, CommandBus commands)
    {
        var config = Path.Combine(fixture.Root, ".git", "stacker-test-gh");
        Directory.CreateDirectory(config);
        return new(new GhProcesses(binary, config, fixture.Runner), new(), new() { UseSavedCredentials = true }, commands, journal);
    }
}
