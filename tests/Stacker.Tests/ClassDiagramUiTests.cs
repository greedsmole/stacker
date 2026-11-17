using Avalonia.Controls;
using Line = Avalonia.Controls.Shapes.Line;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed partial class ClassDiagramUiTests
{
    [AvaloniaFact]
    public async Task Branch_comparison_steps_revisions_filters_relationships_and_rejects_stale_analysis()
    {
        await using var fixture = new GitFixture();
        await fixture.Linear();
        var store = new YamlStackStore();
        await store.SaveAsync(fixture.Root, [fixture.Stack], null);
        var reader = new DiagramReader();
        using var vm = new MainViewModel(fixture.Reader, store, new JsonSettingsStore(Path.Combine(fixture.Root, "settings")),
            new GitExecutable(), new(fixture.Reader), new(fixture.Reader), csharpGraph: reader);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            await vm.OpenRepositoryAsync(fixture.Root);
            await vm.SelectLayerAsync(Assert.Single(vm.LocalGroups).Layers[1], false);
            var comparison = Assert.Single(vm.Sections).Result;
            Find<Button>(window, "OpenClassDiagramButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var diagram = Assert.Single(window.OwnedWindows);
            var revisions = Find<ComboBox>(diagram, "Revisions");
            var modes = Find<ComboBox>(diagram, "Modes");
            var load = Find<Button>(diagram, "LoadButton");
            var range = Find<SelectableTextBlock>(diagram, "Range");
            var canvas = Find<Canvas>(diagram, "GraphCanvas");
            await UntilAsync(() => load.IsEnabled);
            Assert.Equal(4, revisions.ItemCount);
            Assert.Contains(comparison.BaseSha[..8], range.Text!);
            Assert.Contains(comparison.HeadSha[..8], range.Text!);
            Assert.Empty(reader.Requests);

            load.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => canvas.Children.OfType<Line>().Any());
            Assert.Equal([comparison.HeadSha, comparison.BaseSha], reader.Requests.Select(item => item.Commit));
            Assert.Contains("changed types", Find<SelectableTextBlock>(diagram, "Status").Text!);
            Capture(diagram, ThemeVariant.Dark, "dark");
            Capture(diagram, ThemeVariant.Light, "light");

            var list = Find<ListBox>(diagram, "TypeList");
            list.SelectedIndex = 0;
            Assert.Contains("Caller", Find<TextBlock>(diagram, "InspectorTitle").Text!);
            var members = Find<ListBox>(diagram, "MemberList");
            members.SelectedIndex = 1;
            Assert.True(Find<Button>(diagram, "SourceButton").IsEnabled);
            Find<Button>(diagram, "SourceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var declaration = Assert.Single(diagram.OwnedWindows);
            Assert.Equal("Definition", declaration.Title);
            await UntilAsync(() => declaration.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Any(text => text.Text?.Contains("   1  A") == true));
            declaration.Close();
            Find<ComboBox>(diagram, "EdgeKinds").SelectedIndex = 1;
            Assert.Empty(canvas.Children.OfType<Line>());
            Find<ComboBox>(diagram, "EdgeKinds").SelectedIndex = 0;
            Assert.NotEmpty(canvas.Children.OfType<Line>());
            Click(diagram, "+");
            Assert.Equal("120%", Find<Button>(diagram, "ZoomReset").Content);

            modes.SelectedIndex = 1;
            var basis = await fixture.Git("rev-parse", "main");
            await UntilAsync(() => load.IsEnabled && range.Text!.Contains(basis[..8]));
            Assert.True(reader.Requests.Any(item => item.Commit == basis), Find<SelectableTextBlock>(diagram, "Status").Text);
            Assert.Contains(comparison.HeadSha[..8], range.Text!);

            reader.BlockNext();
            revisions.SelectedIndex = 3;
            await reader.WaitForBlockedRequest.WaitAsync(TimeSpan.FromSeconds(10));
            revisions.SelectedIndex = 0;
            await UntilAsync(() => load.IsEnabled && range.Text!.Contains(basis[..8]) && !range.Text!.Contains('→'));
            reader.ReleaseBlockedRequest();
            await UntilAsync(() => reader.BlockedRequestReturned);
            Assert.DoesNotContain('→', range.Text!);
            Assert.Contains("types. Select a type", Find<SelectableTextBlock>(diagram, "Status").Text!);

            await fixture.Write("tip.txt", "new revision");
            await fixture.Commit("move top ref");
            var newTip = await fixture.Git("rev-parse", "HEAD");
            Click(diagram, "Refresh revisions");
            await UntilAsync(() => load.IsEnabled && revisions.Items.Cast<object>().Any(item => item.ToString()!.Contains(newTip[..8])));
            Click(diagram, "Close");
            Assert.False(diagram.IsVisible);

            await vm.SelectOverviewAsync(Assert.Single(vm.LocalGroups));
            Find<Button>(window, "OpenClassDiagramButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var overview = Assert.Single(window.OwnedWindows);
            var overviewModes = Find<ComboBox>(overview, "Modes");
            Assert.Equal("Displayed changes", overviewModes.SelectedItem!.ToString());
            Assert.Contains(overviewModes.Items.Cast<object>(), item => item.ToString() == "This layer");
            overview.Close();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Pull_request_review_uses_that_prs_diff_and_offers_its_stack_base()
    {
        var root = Directory.CreateTempSubdirectory("stacker-diagram-pr-");
        using var services = Composition.Build(root.FullName);
        var vm = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            await vm.OpenDemoAsync();
            await vm.RefreshGitHubAsync();
            var group = Assert.Single(vm.RemoteGroups, item => item.PullRequests.Any(pr => pr.Number == 102));
            var pr = Assert.Single(group.PullRequests, pr => pr.Number == 102);
            var layer = group.Layers.Single(item => item.Snapshot.HeadSha == pr.HeadSha);
            await vm.SelectLayerAsync(layer, false);
            await vm.ShowReviewAsync();
            Assert.NotNull(vm.ActiveReview);
            window.UpdateLayout();
            var panel = window.GetLogicalDescendants().OfType<ReviewPanel>().Single();
            var button = panel.FindControl<Button>("ReviewClassDiagramButton")!;
            Assert.True(button.IsEffectivelyVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var diagram = Assert.Single(window.OwnedWindows);
            var diff = Assert.Single(vm.Sections).Result;
            var range = Find<SelectableTextBlock>(diagram, "Range");
            Assert.Contains(diff.BaseSha[..8], range.Text!);
            Assert.Contains(pr.HeadSha[..8], range.Text!);
            var revisions = Find<ComboBox>(diagram, "Revisions");
            Assert.Contains(group.PullRequests[0].BaseSha[..8], revisions.Items[0]!.ToString()!);
            Assert.Contains("#102", revisions.SelectedItem!.ToString()!);
            diagram.Close();
        }
        finally
        {
            window.Close();
            await vm.FlushReviewsAsync();
            services.Dispose();
            root.Delete(recursive: true);
        }
    }

    private static T Find<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;

    private static void Click(Window window, string label) => window.GetLogicalDescendants().OfType<Button>()
        .Single(button => button.IsEffectivelyVisible && Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
            await Task.Delay(15, timeout.Token);
    }

    private static void Capture(Window window, ThemeVariant theme, string name)
    {
        window.RequestedThemeVariant = theme;
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        if (Environment.GetEnvironmentVariable("STACKER_UI_CAPTURE_DIR") is not { } directory)
            return;
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name + "-class-diagram.png"));
    }
}
