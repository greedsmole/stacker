// Purpose: Checks for the diff and search surfaces: pinned diff output, explained empty patches, and search results that follow their sections.
using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed partial class DiffQualityTests
{

    // Scenario: Git's submodule format is pinned, so a user's diff.submodule setting cannot change the patch shape we parse.
    [Fact]
    public async Task The_diff_command_pins_the_submodule_format()
    {
        await using var f = new GitFixture();
        await f.Init();
        await f.Git("config", "diff.submodule", "diff");
        var log = new GitCommandLog();
        var reader = new GitRepositoryReader(new ProcessRunner(log), new GitExecutable());
        var head = await f.Git("rev-parse", "HEAD");
        await reader.FilesAsync(f.Root, head, head);
        Assert.Contains(log.Snapshot(), entry => entry.Command.Contains("--submodule=short", StringComparison.Ordinal));
    }

    // Scenario: A patch with no hunks explains itself instead of showing a blank pane.
    [Fact]
    public async Task A_patch_without_hunks_explains_itself()
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

    [Fact]
    public async Task A_rename_without_text_changes_offers_the_original_file_contents()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        await fixture.Write("old.cs", "class Original {}\n\n");
        await fixture.Commit("Add source");
        var basis = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("mv", "old.cs", "new.cs");
        await fixture.Commit("Rename source");
        var head = await fixture.Git("rev-parse", "HEAD");
        var files = await fixture.Reader.FilesAsync(fixture.Root, basis, head);
        var file = Assert.Single(files);
        Assert.Equal(FileChangeKind.Renamed, file.Kind);
        using var section = new DiffSectionViewModel(fixture.Reader, fixture.Root, new("rename", "main", basis, head, null, files), selectFirstFile: false);
        await section.LoadAsync(file);

        var gap = Assert.IsType<DiffContextGap>(Assert.Single(section.Lines).ContextGap);
        Assert.Equal((1, 1, 2), (gap.OldStart, gap.NewStart, gap.Count));
        Assert.False(gap.HasHunkAbove);
        Assert.False(gap.HasHunkBelow);
        Assert.True(section.HasCollapsedContext);
    }

    // Scenario: Search results never outlive the sections they point at.
    [AvaloniaFact]
    public async Task Search_results_are_cleared_with_their_sections()
    {
        await using var f = new GitFixture();
        await f.Linear();
        var store = new YamlStackStore();
        await store.SaveAsync(f.Root, [f.Stack], null);
        await f.Commit("stack definition");
        using var vm = new MainViewModel(f.Reader, store, new JsonSettingsStore(f.Root + " settings"), new GitExecutable(),
            new StackService(f.Reader), new DiffService(f.Reader), branchWriter: f.Writer());
        await vm.OpenRepositoryAsync(f.Root);
        vm.SearchScope = "Files";
        vm.SearchQuery = "A.txt";
        await vm.SearchAsync();
        Assert.NotEmpty(vm.SearchResults);
        vm.Sections.Clear();
        Assert.Empty(vm.SearchResults);
    }
}
