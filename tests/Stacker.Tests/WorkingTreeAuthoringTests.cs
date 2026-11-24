using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;

namespace Stacker.Tests;

public sealed class WorkingTreeAuthoringTests
{
    [Fact]
    public async Task Staged_and_unstaged_patches_are_distinct_and_unstage_preserves_bytes()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        var author = Create(fixture);
        await fixture.Write("base.txt", "staged\n");
        await fixture.Git("add", "--", "base.txt");
        await fixture.Write("base.txt", "working copy\n");

        var snapshot = (await author.ReadAsync(fixture.Root)).OrThrow();
        var entry = Assert.Single(snapshot.State.Entries);
        Assert.True(entry.IsStaged && entry.IsUnstaged);
        var staged = new WorkingTreeFile(entry, WorkingTreeSide.Staged);
        var unstaged = staged with { Side = WorkingTreeSide.Unstaged };
        var stagedPatch = (await author.ReadPatchAsync(snapshot, staged)).OrThrow().Patch;
        var unstagedPatch = (await author.ReadPatchAsync(snapshot, unstaged)).OrThrow().Patch;
        Assert.Contains("-base\n+staged", stagedPatch);
        Assert.Contains("-staged\n+working copy", unstagedPatch);

        var head = await fixture.Git("rev-parse", "HEAD");
        (await author.UpdateIndexAsync(snapshot, WorkingTreeSide.Staged, [staged])).OrThrow();
        Assert.Equal("working copy\n", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "base.txt")));
        Assert.Equal(head, await fixture.Git("rev-parse", "HEAD"));
        Assert.Empty(await fixture.Git("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task Stages_literal_untracked_paths_and_refuses_a_stale_index_revision()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        var author = Create(fixture);
        var exact = OperatingSystem.IsWindows() ? "[x].txt" : ":(glob)*.txt";
        await fixture.Write(exact, "literal\n");
        await fixture.Write("x.txt", "working\n");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "payload.bin"), [0, 1, 2, 3]);

        var snapshot = (await author.ReadAsync(fixture.Root)).OrThrow();
        var file = new WorkingTreeFile(snapshot.State.Entries.Single(entry => entry.Path == exact), WorkingTreeSide.Unstaged);
        var binary = new WorkingTreeFile(snapshot.State.Entries.Single(entry => entry.Path == "payload.bin"), WorkingTreeSide.Unstaged);
        Assert.Contains("+literal", (await author.ReadPatchAsync(snapshot, file)).OrThrow().Patch);
        Assert.True((await author.ReadPatchAsync(snapshot, binary)).OrThrow().File.IsBinary);
        (await author.UpdateIndexAsync(snapshot, WorkingTreeSide.Unstaged, [file])).OrThrow();
        Assert.Equal(exact, await fixture.Git("diff", "--cached", "--name-only"));
        Assert.True((await author.UpdateIndexAsync(snapshot, WorkingTreeSide.Unstaged, [binary])).IsFailed);
        Assert.True((await author.CommitStagedAsync(snapshot, "stale")).IsFailed);

        var current = (await author.ReadAsync(fixture.Root)).OrThrow();
        var commit = (await author.CommitStagedAsync(current, "Stage the literal path")).OrThrow();
        Assert.Equal(commit, await fixture.Git("rev-parse", "HEAD"));
        Assert.Equal(exact, await fixture.Git("diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD"));
        Assert.Equal("working\n", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "x.txt")));
    }

    [Fact]
    public async Task Unstages_a_rename_and_a_first_commit_without_touching_files()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        var author = Create(fixture);
        await fixture.Git("mv", "base.txt", "renamed.txt");
        var snapshot = (await author.ReadAsync(fixture.Root)).OrThrow();
        var rename = new WorkingTreeFile(Assert.Single(snapshot.State.Entries), WorkingTreeSide.Staged);
        Assert.Equal("base.txt", rename.OldPath);
        Assert.Contains("rename from base.txt", (await author.ReadPatchAsync(snapshot, rename)).OrThrow().Patch);
        (await author.UpdateIndexAsync(snapshot, WorkingTreeSide.Staged, [rename])).OrThrow();
        Assert.Empty(await fixture.Git("diff", "--cached", "--name-only"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "base.txt")));
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "renamed.txt")));

        await using var unborn = new GitFixture();
        await unborn.Git("init", "-b", "main");
        await unborn.Write("first.txt", "first\n");
        await unborn.Git("add", "--", "first.txt");
        var first = (await author.ReadAsync(unborn.Root)).OrThrow();
        Assert.Null(first.Head);
        var added = new WorkingTreeFile(Assert.Single(first.State.Entries), WorkingTreeSide.Staged);
        Assert.Contains("+first", (await author.ReadPatchAsync(first, added)).OrThrow().Patch);
        (await author.UpdateIndexAsync(first, WorkingTreeSide.Staged, [added])).OrThrow();
        Assert.Empty(await unborn.Git("ls-files"));
        Assert.Equal("first\n", await File.ReadAllTextAsync(Path.Combine(unborn.Root, "first.txt")));
    }

    private static WorkingTreeAuthoring Create(GitFixture fixture)
    {
        var journal = fixture.CreateJournal();
        return new(fixture.Runner, new(), fixture.Reader, new(new(journal)), journal);
    }
}
