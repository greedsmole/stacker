using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

namespace Stacker.Tests;

[Collection(HeadlessApp.CollectionName)]
public sealed partial class SymbolNavigationTests
{
    [AvaloniaFact]
    public async Task Keyboard_commands_pass_the_exact_side_and_position_from_the_diff()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        await fixture.Write("Code.cs", "class Before { }\n");
        await fixture.Commit("base source");
        var basis = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("checkout", "-b", "feature");
        await fixture.Write("Code.cs", "class After { }\n");
        await fixture.Commit("new source");
        var head = await fixture.Git("rev-parse", "HEAD");
        var store = new YamlStackStore();
        await store.SaveAsync(fixture.Root, [new("feature", "Feature", "refs/heads/main", ["refs/heads/feature"])], null);
        var recorder = new RecordingNavigation();
        using var vm = new MainViewModel(fixture.Reader, store, new JsonSettingsStore(Path.Combine(fixture.Root, "settings")),
            new GitExecutable(), new(fixture.Reader), new(fixture.Reader), symbolNavigation: recorder);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            await vm.OpenRepositoryAsync(fixture.Root);
            await vm.SelectLayerAsync(Assert.Single(vm.LocalGroups).Layers[0], false);
            var section = Assert.Single(vm.Sections);
            await section.LoadAsync(section.SelectedFile);
            window.UpdateLayout();

            foreach (var (kind, commit, modifiers, action) in new[]
            {
                (DiffLineKind.Removed, basis, RawInputModifiers.None, SymbolNavigationKind.Definition),
                (DiffLineKind.Added, head, RawInputModifiers.Shift, SymbolNavigationKind.References)
            })
            {
                var text = Assert.Single(window.GetVisualDescendants().OfType<SyntaxText>(), control => control.Line?.Kind == kind);
                Assert.True(text.Focus());
                text.SelectionStart = 6;
                text.SelectionEnd = 7;
                window.KeyPress(Key.F12, modifiers, PhysicalKey.None, "");
                window.KeyRelease(Key.F12, modifiers, PhysicalKey.None, "");
                var request = Assert.Single(recorder.Requests);
                recorder.Requests.Clear();
                Assert.Equal(new SymbolNavigationRequest(section.Root, commit, "Code.cs", 1, 6, action), request);
            }
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Uses_the_matching_checkout_and_distinguishes_overloads_across_projects()
    {
        await using var fixture = new GitFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        await fixture.Init();
        var prior = await fixture.Git("rev-parse", "HEAD");
        await fixture.Write(".gitignore", "bin/\nobj/\n");
        await fixture.Write("Solution.slnx", """
            <Solution>
              <Project Path="Library/Library.csproj" />
              <Project Path="Client/Client.csproj" />
            </Solution>
            """);
        await fixture.Write("Library/Library.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        await fixture.Write("Client/Client.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
            </Project>
            """);
        await fixture.Write("Library/Calculator.cs", """
            namespace Library;
            public static class Calculator
            {
                public static int Sum(int x) => x;
                public static string Sum(string x) => x;
            }
            """);
        const string firstUse = "    public int Result() => Library.Calculator.Sum(1);";
        await fixture.Write("Client/Call.cs", "public class Client\n{\n" + firstUse + """

                public string Other() => Library.Calculator.Sum("same name");
                public int Again() => Library.Calculator.Sum(2);
            }
            """);
        await fixture.Commit("C# solution");
        var head = await fixture.Git("rev-parse", "HEAD");
        var root = await fixture.ResolvedRoot();
        var restore = await fixture.Runner.RunAsync(new("dotnet",
            ["restore", "Solution.slnx", "--source", root, "-p:NuGetAudit=false", "--disable-build-servers", "-m:1", "-nr:false"], root,
            TimeSpan.FromSeconds(20)), timeout.Token);
        Assert.True(restore.ExitCode == 0, restore.StdOut + restore.StdErr);

        ISymbolNavigation navigator = new RoslynSymbolNavigation(fixture.Reader);
        var request = new SymbolNavigationRequest(root, head, "Client/Call.cs", 3,
            firstUse.IndexOf("Sum", StringComparison.Ordinal), SymbolNavigationKind.Definition);
        var project = Assert.Single((await navigator.ProjectsAsync(request, timeout.Token)).OrThrow());
        var definition = Assert.Single((await navigator.FindAsync(request, project, timeout.Token)).OrThrow().Locations);
        Assert.Equal("Library/Calculator.cs", definition.Path);
        Assert.Equal(4, definition.Line);

        var references = (await navigator.FindAsync(request with
        {
            Kind = SymbolNavigationKind.References
        }, project, timeout.Token)).OrThrow();
        Assert.Equal([3, 5], references.Locations.Select(location => location.Line));
        Assert.All(references.Locations, location => Assert.Equal("Client/Call.cs", location.Path));
        var wrongCommit = await navigator.ProjectsAsync(request with
        {
            Commit = prior
        }, timeout.Token);
        Assert.Contains(prior[..8], wrongCommit.Problem!.Message);
        await fixture.Write("Client/Call.cs", "local changes");
        var dirty = await navigator.FindAsync(request, project, timeout.Token);
        Assert.Contains("clean checkout", dirty.Problem!.Message);
    }
}
