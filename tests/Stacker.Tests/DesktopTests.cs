using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

[assembly: AvaloniaTestApplication(typeof(Stacker.Tests.HeadlessApp))]
namespace Stacker.Tests;
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
public sealed class DesktopTests
{
    [AvaloniaFact]
    public async Task Window_loads_repository_stack_and_diff()
    {
        await using var fixture = new GitFixture(); await fixture.Linear();
        var store = new YamlStackStore(); await store.SaveAsync(fixture.Root, [fixture.Stack], null);
        using var vm = CreateVm(fixture.Reader, store, fixture.Root);
        var window = new MainWindow { DataContext = vm }; window.Show();
        await vm.OpenRepositoryAsync(fixture.Root);
        Assert.Equal(3, vm.Layers.Count); Assert.Single(vm.Sections);
        Assert.Equal(3, vm.Sections[0].Files.Count);
        await vm.SelectLayerAsync(vm.LocalGroups[0].Layers[0], false);
        Assert.Equal("A.txt", Assert.Single(vm.Sections[0].Files).NewPath);
        await vm.Sections[0].LoadAsync(vm.Sections[0].SelectedFile);
        Assert.Contains(vm.Sections[0].Lines, l => l.Text == "+A");
        Assert.False(vm.HasError, vm.Error);
        vm.Mode = DiffMode.MultiLayer;
        vm.SelectLayers([vm.Layers[0], vm.Layers[2]]);
        await vm.CompareAsync();
        Assert.Equal(2, vm.Sections.Count); Assert.Equal("refs/heads/B", vm.Sections[1].Result.ParentRef);
        await vm.RefreshAsync();
        Assert.Equal(new[] { 0, 2 }, vm.SelectedPositions);
        Assert.Equal(2, vm.Sections.Count);
        window.Close();
    }
    [AvaloniaFact]
    public async Task Slow_old_file_result_cannot_replace_new_selection()
    {
        var reader = new DelayedReader();
        var a = new FileChange("a", "a", FileChangeKind.Added, 1, 0, "000000", "100644");
        var b = a with { OldPath = "b", NewPath = "b" };
        using var section = new DiffSectionViewModel(reader, "root", new("layer", "base", new('a', 40), new('b', 40), null, [a, b]));
        section.SelectedFile = b;
        await section.LoadAsync(b);
        reader.Pending.TrySetResult(new(a, "old", [], [new(DiffLineKind.Added, null, 1, "old")]));
        await Task.Yield();
        Assert.Equal("new", section.Patch); Assert.DoesNotContain(section.Lines, l => l.Text == "old");
    }
    private static MainViewModel CreateVm(IGitRepositoryReader reader, IStackStore store, string path) => new(reader, store, new JsonSettingsStore(Path.Combine(path, "settings")), new(), new(reader), new(reader));
    private sealed class DelayedReader : IGitRepositoryReader
    {
        public TaskCompletionSource<FileDiff> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default) => file.NewPath == "a" ? Pending.Task : Task.FromResult(new FileDiff(file, "new", [], [new(DiffLineKind.Added, null, 1, "new")]));
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
