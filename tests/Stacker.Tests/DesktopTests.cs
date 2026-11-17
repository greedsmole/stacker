// Purpose: Regression scenarios for view-model navigation and desktop state behavior without starting external services.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

[assembly: AvaloniaTestApplication(typeof(Stacker.Tests.HeadlessApp))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
namespace Stacker.Tests;
[Collection(HeadlessApp.CollectionName)]
public sealed partial class DesktopTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Added_and_removed_rows_keep_their_neon_accents_at_both_line_heights(bool light)
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        var basis = await fixture.Git("rev-parse", "HEAD");
        await fixture.Write("base.txt", "changed\n");
        await fixture.Commit("replace line");
        var head = await fixture.Git("rev-parse", "HEAD");
        var files = await fixture.Reader.FilesAsync(fixture.Root, basis, head);
        using var section = new DiffSectionViewModel(fixture.Reader, fixture.Root, new("head", "base", basis, head, null, files));
        await section.LoadAsync(section.SelectedFile);
        var app = Application.Current!;
        var priorHeight = app.Resources["DiffLineHeight"];
        var view = new DiffSectionView { DataContext = section };
        var window = new Window { Content = view, Width = 840, Height = 480, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show();
        try
        {
            foreach (var height in new[] { 16d, 26d })
            {
                app.Resources["DiffLineHeight"] = height;
                window.UpdateLayout();
                var rows = view.GetVisualDescendants().OfType<Grid>().Where(row => row.Name == "CodeRow").ToArray();
                foreach (var (kind, color) in new[] { (DiffLineKind.Added, "#50FF00"), (DiffLineKind.Removed, "#FF173C") })
                {
                    var row = Assert.Single(rows, row => row.DataContext is RenderedDiffLine line && line.Kind == kind);
                    var accent = Assert.Single(row.Children.OfType<Border>(), border => border.Classes.Contains("diff-accent"));
                    Assert.Equal(Color.Parse(color), Assert.IsAssignableFrom<ISolidColorBrush>(accent.Background).Color);
                    Assert.NotNull(row.Background);
                    Assert.Equal(height, row.Bounds.Height);
                }
            }
        }
        finally { window.Close(); app.Resources["DiffLineHeight"] = priorHeight; }
    }

    [AvaloniaFact]
    public void File_navigator_can_collapse_and_restore_its_width()
    {
        var view = new DiffSectionView();
        var grid = view.FindControl<Avalonia.Controls.Grid>("ContentGrid")!;
        var pane = view.FindControl<Avalonia.Controls.Grid>("FilePane")!;
        view.Measure(new Size(900, 600));
        view.Arrange(new Rect(0, 0, 900, 600));
        var original = grid.ColumnDefinitions[0].ActualWidth;
        var toggle = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(view)
            .OfType<Avalonia.Controls.Button>().Single(b => Equals(b.Content, "Files"));
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.False(pane.IsVisible);
        Assert.Equal(0, grid.ColumnDefinitions[0].Width.Value);
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.True(pane.IsVisible);
        Assert.Equal(original, grid.ColumnDefinitions[0].Width.Value);
    }
    [AvaloniaFact]
    public async Task Window_loads_repository_stack_and_diff()
    {
        await using var fixture = new GitFixture();
        await fixture.Linear();
        var store = new YamlStackStore();
        await store.SaveAsync(fixture.Root, [fixture.Stack], null);
        using var vm = CreateVm(fixture.Reader, store, fixture.Root);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.OpenRepositoryAsync(fixture.Root);
        Assert.Equal(3, vm.Layers.Count);
        Assert.Single(vm.Sections);
        Assert.Equal(3, vm.Sections[0].Files.Count);
        await vm.SelectLayerAsync(vm.LocalGroups[0].Layers[0], false);
        Assert.Equal("A.txt", Assert.Single(vm.Sections[0].Files).NewPath);
        await vm.Sections[0].LoadAsync(vm.Sections[0].SelectedFile);
        Assert.Contains(vm.Sections[0].Lines, l => l.Text == "A" && l.Kind == DiffLineKind.Added);
        Assert.False(vm.HasError, vm.Error);
        vm.Mode = DiffMode.MultiLayer;
        vm.SelectLayers([vm.Layers[0], vm.Layers[2]]);
        await vm.CompareAsync();
        Assert.Equal(2, vm.Sections.Count);
        Assert.Equal("refs/heads/B", vm.Sections[1].Result.ParentRef);
        await vm.RefreshAsync();
        Assert.Equal(new[] { 0, 2 }, vm.SelectedPositions);
        Assert.Equal(2, vm.Sections.Count);
        await vm.SetGroupFilesByFolderAsync(true);
        Assert.All(vm.Sections, section => Assert.True(section.GroupFilesByFolder));
        await vm.SelectLayerAsync(vm.LocalGroups[0].Layers[1], false);
        Assert.True(Assert.Single(vm.Sections).GroupFilesByFolder);
        Assert.True((await new JsonSettingsStore(Path.Combine(fixture.Root, "settings")).LoadAsync()).GroupFilesByFolder);
        window.Close();
    }
    [AvaloniaFact]
    public async Task Slow_old_file_result_cannot_replace_new_selection()
    {
        var reader = new DelayedReader();
        var a = new FileChange("a", "a", FileChangeKind.Added, 1, 0, "000000", "100644");
        var b = a with
        {
            OldPath = "b",
            NewPath = "b"
        };
        using var section = new DiffSectionViewModel(reader, "root", new("layer", "base", new('a', 40), new('b', 40), null, [a, b]));
        section.SelectedFile = b;
        await section.LoadAsync(b);
        reader.Pending.TrySetResult(new(a, "old", [], [new(DiffLineKind.Added, null, 1, "old")]));
        await Task.Yield();
        Assert.Equal("new", section.Patch);
        Assert.DoesNotContain(section.Lines, l => l.Text == "old");
    }
    private static MainViewModel CreateVm(IGitRepositoryReader reader, IStackStore store, string path) => new(reader, store, new JsonSettingsStore(Path.Combine(path, "settings")), new GitExecutable(), new(reader), new(reader));
}
