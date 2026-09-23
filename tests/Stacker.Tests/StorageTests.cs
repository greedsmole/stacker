using Stacker.Core;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class StorageTests
{
    [Fact]
    public async Task Roundtrip_preserves_order_ids_and_missing_refs()
    {
        await using var f = new GitFixture(); var store = new YamlStackStore();
        var initial = await store.LoadAsync(f.Root); Assert.Null(initial.Revision);
        var written = await store.SaveAsync(f.Root, [f.Stack, new("two", "Other stack", "refs/remotes/origin/main", ["refs/heads/missing"])], null);
        var read = await new YamlStackStore().LoadAsync(f.Root);
        Assert.Equal(written.Revision, read.Revision); Assert.Equal(f.Stack.Branches, read.Stacks[0].Branches); Assert.Equal("two", read.Stacks[1].Id);
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp")); Assert.False(File.Exists(Path.Combine(f.Root, ".stackpr.yml.lock")));
    }
    [Theory]
    [InlineData("version: 99\nstacks: []")]
    [InlineData("version: 1\nstacks: [invalid")]
    [InlineData("version: 1\nversion: 1\nstacks: []")]
    [InlineData("version: 1\nstacks:\n - id: x\n   name: x\n   base: refs/heads/main")]
    public async Task Invalid_configuration_is_never_overwritten(string text)
    {
        await using var f = new GitFixture(); var path = Path.Combine(f.Root, ".stackpr.yml"); await File.WriteAllTextAsync(path, text);
        var store = new YamlStackStore();
        await Assert.ThrowsAsync<StackerException>(() => store.LoadAsync(f.Root));
        await Assert.ThrowsAsync<StackerException>(() => store.SaveAsync(f.Root, [f.Stack], null));
        Assert.Equal(text, await File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task External_edits_and_new_files_require_reload()
    {
        await using var f = new GitFixture(); var store = new YamlStackStore();
        var original = await store.SaveAsync(f.Root, [f.Stack], null);
        await File.AppendAllTextAsync(Path.Combine(f.Root, ".stackpr.yml"), "\n# edited externally\n");
        await Assert.ThrowsAsync<StackerException>(() => store.SaveAsync(f.Root, [], original.Revision));
        await Assert.ThrowsAsync<StackerException>(() => store.SaveAsync(f.Root, [], null));
        var fresh = await store.LoadAsync(f.Root); await store.SaveAsync(f.Root, [], fresh.Revision);
        Assert.Empty((await store.LoadAsync(f.Root)).Stacks);
    }
    [Fact]
    public void Validation_rejects_duplicates_and_base_as_layer_but_allows_shared_refs_across_stacks()
    {
        var stack = new StackDefinition("a", "a", "refs/heads/main", ["refs/heads/A"]);
        StackValidation.Validate([stack, stack with { Id = "b" }]);
        Assert.Throws<StackerException>(() => StackValidation.Validate([stack, stack]));
        Assert.Throws<StackerException>(() => StackValidation.Validate([stack with { Branches = ["refs/heads/A", "refs/heads/A"] }]));
        Assert.Throws<StackerException>(() => StackValidation.Validate([stack with { Branches = [stack.Base] }]));
    }
    [Fact]
    public async Task Settings_survive_restart_and_invalid_dimensions_are_clamped()
    {
        await using var f = new GitFixture(); var store = new JsonSettingsStore(f.Root);
        await store.SaveAsync(new() { RecentRepositories = [f.Root], GitPath = "/custom/git", Theme = "Light", WindowWidth = -1 });
        var restored = await new JsonSettingsStore(f.Root).LoadAsync();
        Assert.Equal("Light", restored.Theme); Assert.Equal("/custom/git", restored.GitPath); Assert.Equal(f.Root, Assert.Single(restored.RecentRepositories)); Assert.Equal(1000, restored.WindowWidth);
    }
}
