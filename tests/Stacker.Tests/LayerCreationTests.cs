// Purpose: Real-Git checks that starting a layer creates exactly one branch and refuses anything unsafe.
using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class LayerCreationTests
{
    private static GitBranchWriter Writer(GitFixture f) => f.Writer();

    // Scenario: A layer starts at the chosen ref, is checked out, and leaves the working tree clean.
    [Fact] public async Task A_layer_is_created_at_the_chosen_start_point_and_checked_out()
    {
        await using var f = new GitFixture(); await f.Linear();
        var writer = Writer(f);
        var reference = await writer.CreateLayerAsync(f.Root, "feature/new-layer", "refs/heads/A");
        Assert.Equal("refs/heads/feature/new-layer", reference);
        Assert.Equal("feature/new-layer", await f.Git("branch", "--show-current"));
        Assert.Equal(await f.Git("rev-parse", "A"), await f.Git("rev-parse", "HEAD"));
        Assert.Empty((await f.Git("status", "--porcelain")).Trim());
        Assert.Equal(await f.Git("rev-parse", "A"), await f.Git("rev-parse", "feature/new-layer"));
    }
    // Scenario: Duplicate names, invalid names, a dirty tree, and a missing start are all refused without writing.
    [Fact] public async Task Layer_creation_refuses_duplicates_invalid_names_a_dirty_tree_and_a_missing_start()
    {
        await using var f = new GitFixture(); await f.Linear();
        var writer = Writer(f);
        Assert.Contains("already exists", (await Assert.ThrowsAsync<StackerException>(() => writer.CreateLayerAsync(f.Root, "A", "refs/heads/main"))).Message);
        Assert.Contains("not a valid branch name", (await Assert.ThrowsAsync<StackerException>(() => writer.CreateLayerAsync(f.Root, "bad..name", "refs/heads/main"))).Message);
        Assert.Contains("Enter a branch name", (await Assert.ThrowsAsync<StackerException>(() => writer.CreateLayerAsync(f.Root, "   ", "refs/heads/main"))).Message);
        // A tracked edit blocks the checkout, an untracked scratch file does not.
        await f.Write("base.txt", "edited\n");
        Assert.Contains("uncommitted", (await Assert.ThrowsAsync<StackerException>(() => writer.CreateLayerAsync(f.Root, "later", "refs/heads/main"))).Message);
        await f.Git("checkout", "--", "base.txt");
        await f.Write("scratch.txt", "notes\n");
        Assert.Equal("refs/heads/scratch-layer", await writer.CreateLayerAsync(f.Root, "scratch-layer", "refs/heads/main"));
        await f.Git("checkout", "C"); await f.Git("branch", "-D", "scratch-layer");
        Assert.Contains("no longer exists", (await Assert.ThrowsAsync<StackerException>(() => writer.CreateLayerAsync(f.Root, "later", "refs/heads/missing"))).Message);
        Assert.Equal("C", await f.Git("branch", "--show-current"));
        Assert.Equal(1, await f.ExitCode("rev-parse", "--verify", "--quiet", "refs/heads/later"));
    }
}
