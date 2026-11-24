namespace Stacker.Tests;

public sealed class GitFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seeded_repositories_keep_refs_config_index_and_objects_isolated(bool linear)
    {
        await using var first = new GitFixture();
        await using var second = new GitFixture();
        await Task.WhenAll(linear ? first.Linear() : first.Init(), linear ? second.Linear() : second.Init());
        var branch = linear ? "C" : "main";
        var original = await second.Git("rev-parse", "HEAD");
        Assert.Equal(branch, await first.Git("branch", "--show-current"));
        Assert.Equal(original, await first.Git("rev-parse", "HEAD"));
        var index = await File.ReadAllBytesAsync(Path.Combine(first.Root, ".git", "index"));
        Assert.Empty(await first.Git("status", "--porcelain"));
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(first.Root, ".git", "index")));
        Assert.Empty(await second.Git("status", "--porcelain"));

        await first.Git("config", "user.name", "changed identity");
        await first.Write("base.txt", "changed in one fixture\n");
        await first.Commit("independent change");
        var changed = await first.Git("rev-parse", "HEAD");
        Assert.NotEqual(original, changed);
        Assert.Equal(original, await second.Git("rev-parse", "HEAD"));
        Assert.Equal("Stacker tests", await second.Git("config", "user.name"));
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(second.Root, "base.txt")));
        Assert.Empty(await second.Git("status", "--porcelain"));
        Assert.NotEqual(0, await second.ExitCode("cat-file", "-e", changed));

        await using var third = new GitFixture();
        if (linear)
            await third.Linear();
        else
            await third.Init();
        Assert.Equal(original, await third.Git("rev-parse", "HEAD"));
        Assert.Empty(await third.Git("status", "--porcelain"));
        Assert.Equal(Path.GetFullPath(Path.Combine(await third.ResolvedRoot(), ".git")),
            Path.GetFullPath(await third.Git("rev-parse", "--absolute-git-dir")));
    }
}
