// Purpose: Fitness functions that fail the build when the architecture drifts away from the contracts.
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

/// <summary>Rules the codebase is expected to keep; each one points at a class of defect found in review.</summary>
public sealed partial class ArchitectureTests
{
    private static readonly Assembly Core = typeof(StackRefs).Assembly;
    private static readonly Assembly Infrastructure = typeof(GitRepositoryReader).Assembly;
    private static readonly Assembly Desktop = typeof(MainViewModel).Assembly;
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string[] Products = ["Core", "Infrastructure", "Desktop"];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stacker.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static IEnumerable<(string Path, string Text)> ProductSources(params string[] projects) =>
        projects.SelectMany(project => Directory.EnumerateFiles(Path.Combine(Root, "src", "Stacker." + project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Path.GetRelativePath(Root, path), File.ReadAllText(path)));

    // Scenario: The domain layer states the rules and never reaches for a process, a file or the network.
    [Fact] public void Core_does_not_touch_the_operating_system()
    {
        string[] forbidden = ["System.Diagnostics", "File.", "Directory.", "HttpClient", "Process.Start"];
        var offenders = ProductSources("Core").Where(source => forbidden.Any(token => source.Text.Contains(token, StringComparison.Ordinal))).ToArray();
        Assert.Empty(offenders.Select(offender => offender.Path));
    }

    // Scenario: Only the UI may use fire-and-forget handlers, and only through the reporting wrapper.
    [Fact] public void Only_the_ui_may_use_async_void()
    {
        var offenders = ProductSources("Core", "Infrastructure").Where(source => source.Text.Contains("async void", StringComparison.Ordinal)).ToArray();
        Assert.Empty(offenders.Select(offender => offender.Path));
    }

    // Scenario: Problem codes are a closed set; a literal code would escape the catalogue.
    [Fact] public void Problem_codes_come_from_the_catalogue()
    {
        var offenders = ProductSources(Products).Where(source => ProblemLiteral().IsMatch(source.Text)).ToArray();
        Assert.Empty(offenders.Select(offender => offender.Path));
    }

    // Scenario: No source file grows into a monolith again (the review found one with nine responsibilities).
    [Fact] public void No_source_file_is_a_monolith()
    {
        var biggest = ProductSources(Products).OrderByDescending(source => source.Text.Count(c => c == '\n')).First();
        Assert.True(biggest.Text.Count(c => c == '\n') <= 700, $"{biggest.Path} has grown past the limit");
    }

    // Scenario: Only Infrastructure implements the git ports, so no other layer can run a command.
    [Fact] public void Only_infrastructure_implements_the_git_ports()
    {
        foreach (var port in new[] { typeof(IGitBranchWriter), typeof(IGitRepositoryReader), typeof(IJournal) })
            foreach (var assembly in new[] { Core, Desktop })
                Assert.DoesNotContain(assembly.GetTypes(), type => !type.IsAbstract && !type.IsInterface && port.IsAssignableFrom(type));
    }

    // Scenario: The whole object graph resolves, so a missing registration cannot ship as a dead app.
    [Fact] public void The_composition_root_resolves_every_service()
    {
        var root = Path.Combine(Path.GetTempPath(), "stacker-compose-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var services = Stacker.Desktop.Composition.Build(root);
            Assert.NotNull(services.GetRequiredService<MainViewModel>());
            foreach (var contract in new[] { typeof(IGitBranchWriter), typeof(IGitRepositoryReader), typeof(IJournal), typeof(IEditorLauncher), typeof(IStackStore), typeof(ISettingsStore), typeof(Stacker.Core.Framework.IClock) })
                Assert.NotNull(services.GetRequiredService(contract));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Scenario: A command that exists must be reachable from the composition root.
    [Fact] public void Every_command_is_reachable_from_the_composition_root()
    {
        var composition = File.ReadAllText(Path.Combine(Root, "src", "Stacker.Desktop", "App.axaml.cs"))
            + File.ReadAllText(Path.Combine(Root, "src", "Stacker.Desktop", "ViewModels", "MainViewModel.cs"));
        foreach (var assembly in new[] { Core, Infrastructure, Desktop })
            foreach (var type in assembly.GetTypes().Where(type => !type.IsAbstract && !type.IsInterface && typeof(IStackCommand).IsAssignableFrom(type)))
                Assert.Contains(type.Name, composition, StringComparison.Ordinal);
    }

    [GeneratedRegex("Problem\\.Of\\(\"|Result<[^>]+>\\.Fail\\(\"")]
    private static partial Regex ProblemLiteral();
}
