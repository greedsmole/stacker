// Purpose: Checks for the diff and search surfaces: pinned diff output, explained empty patches, and search results that follow their sections.
using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;
public sealed class DiffQualityTests
{
    /// <summary>Returns a patch with no hunks: a rename, an empty file or a mode change looks like this.</summary>
    private sealed class HunklessReader(FileDiff diff) : IGitRepositoryReader
    {
        public Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default) => Task.FromResult(diff);
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // Scenario: Git's submodule format is pinned, so a user's diff.submodule setting cannot change the patch shape we parse.
    [Fact] public async Task The_diff_command_pins_the_submodule_format()
    {
        await using var f = new GitFixture(); await f.Init();
        await f.Git("config", "diff.submodule", "diff");
        var log = new GitCommandLog();
        var reader = new GitRepositoryReader(new ProcessRunner(log), new GitExecutable());
        var head = await f.Git("rev-parse", "HEAD");
        await reader.FilesAsync(f.Root, head, head);
        Assert.Contains(log.Snapshot(), entry => entry.Command.Contains("--submodule=short", StringComparison.Ordinal));
    }

    // Scenario: A patch with no hunks explains itself instead of showing a blank pane.
    [Fact] public async Task A_patch_without_hunks_explains_itself()
    {
        var file = new FileChange("old.txt", "new.txt", FileChangeKind.Renamed, 0, 0, "100644", "100644");
        var diff = new FileDiff(file, "", [], [new(DiffLineKind.Header, null, null, "diff --git a/old.txt b/new.txt")]);
        var section = new DiffSectionViewModel(new HunklessReader(diff), "/repo", new("layer", "main", new string('a', 40), new string('b', 40), null, [file]));
        await section.LoadAsync(file);
        Assert.Empty(section.Lines);
        Assert.Contains("no textual changes", section.Message);
        Assert.Contains("Renamed", section.Message);
        section.Dispose();
    }

    // Scenario: Search results never outlive the sections they point at.
    [AvaloniaFact]
    public async Task Search_results_are_cleared_with_their_sections()
    {
        await using var f = new GitFixture(); await f.Linear();
        var store = new YamlStackStore(); await store.SaveAsync(f.Root, [f.Stack], null); await f.Commit("stack definition");
        using var vm = new MainViewModel(f.Reader, store, new JsonSettingsStore(f.Root + " settings"), new GitExecutable(),
            new StackService(f.Reader), new DiffService(f.Reader), branchWriter: f.Writer());
        await vm.OpenRepositoryAsync(f.Root);
        vm.SearchScope = "Files"; vm.SearchQuery = "A.txt";
        await vm.SearchAsync();
        Assert.NotEmpty(vm.SearchResults);
        vm.Sections.Clear();
        Assert.Empty(vm.SearchResults);
    }
}
