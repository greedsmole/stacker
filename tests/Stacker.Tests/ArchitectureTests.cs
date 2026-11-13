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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stacker.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static IEnumerable<(string Path, string Text)> ProductSources(params string[] projects) =>
        projects.SelectMany(project => Directory.EnumerateFiles(Path.Combine(Root, "src", "Stacker." + project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Path.GetRelativePath(Root, path), File.ReadAllText(path)));

    // Scenario: No git invocation depends on a device path, which Windows git versions refuse to read.
    [Fact]
    public void No_git_call_uses_a_device_path_for_configuration_or_hooks()
    {
        string[] forbidden = ["core.hooksPath=/dev/null", "GIT_CONFIG_GLOBAL\"] = \"NUL\"", "IsWindows() ? \"NUL\""];
        var offenders = ProductSources("Core", "Infrastructure", "Desktop")
            // The rule itself names the forbidden tokens, so it is excluded from its own scan.
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "tests"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Where(path => !path.EndsWith("ArchitectureTests.cs", StringComparison.Ordinal))
                .Select(path => (Path.GetRelativePath(Root, path), File.ReadAllText(path))))
            .Where(source => forbidden.Any(token => source.Item2.Contains(token, StringComparison.Ordinal)))
            .Select(source => source.Item1);
        Assert.Empty(offenders);
    }

    // Scenario: The domain layer states the rules and never reaches for a process, a file or the network.
    [Fact]
    public void Core_does_not_touch_the_operating_system()
    {
        string[] forbidden = ["System.Diagnostics", "File.", "Directory.", "HttpClient", "Process.Start"];
        var offenders = ProductSources("Core").Where(source => forbidden.Any(token => source.Text.Contains(token, StringComparison.Ordinal))).ToArray();
        Assert.Empty(offenders.Select(offender => offender.Path));
    }

    // Scenario: Only the UI may use fire-and-forget handlers, and only through the reporting wrapper.
    [Fact]
    public void Only_the_ui_may_use_async_void()
    {
        var offenders = ProductSources("Core", "Infrastructure").Where(source => source.Text.Contains("async void", StringComparison.Ordinal)).ToArray();
        Assert.Empty(offenders.Select(offender => offender.Path));
    }

    // Scenario: Problem codes are a closed set; a literal code would escape the catalogue.
    [Fact]
    public void Problem_codes_come_from_the_catalogue()
    {
        var offenders = ProductSources(Products).Where(source => ProblemLiteral().IsMatch(source.Text)).ToArray();
        Assert.Empty(offenders.Select(offender => offender.Path));
    }

    // Scenario: No source file grows into a monolith again (the review found one with nine responsibilities).
    [Fact]
    public void No_source_file_is_a_monolith()
    {
        var biggest = ProductSources(Products).OrderByDescending(source => CodeLineCount(source.Text)).First();
        Assert.True(CodeLineCount(biggest.Text) <= 300, $"{biggest.Path} has grown past the limit (XML documentation lines are excluded)");
    }

    // XML documentation is reference material, not structural growth, so it does not count toward the limit.
    private static int CodeLineCount(string text) =>
        text.Count(character => character == '\n')
        - text.Split('\n').Count(line => line.TrimStart().StartsWith("///", StringComparison.Ordinal));

    // Scenario: Only Infrastructure implements the git ports, so no other layer can run a command.
    [Fact]
    public void Only_infrastructure_implements_the_git_ports()
    {
        foreach (var port in new[] { typeof(IGitBranchWriter), typeof(IGitRepositoryReader), typeof(IJournal) })
            foreach (var assembly in new[] { Core, Desktop })
                Assert.DoesNotContain(assembly.GetTypes(), type => !type.IsAbstract && !type.IsInterface && port.IsAssignableFrom(type));
    }

    // Scenario: The whole object graph resolves, so a missing registration cannot ship as a dead app.
    [Fact]
    public void The_composition_root_resolves_every_service()
    {
        var root = Path.Combine(Path.GetTempPath(), "stacker-compose-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var services = Stacker.Desktop.Composition.Build(root);
            Assert.NotNull(services.GetRequiredService<MainViewModel>());
            foreach (var contract in new[] { typeof(IGitBranchWriter), typeof(IGitRepositoryReader), typeof(IJournal), typeof(IEditorLauncher), typeof(IStackStore), typeof(ISettingsStore), typeof(Stacker.Core.Framework.IClock), typeof(IHistoryStore), typeof(IHistoryGit), typeof(IPullRequestObjects), typeof(IChangeIdentityService), typeof(IReviewDraftRepository), typeof(IWorkspaceBackupService), typeof(IObjectStore), typeof(IDiagnosticSink) })
                Assert.NotNull(services.GetRequiredService(contract));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Scenario: A command that exists must be reachable from the composition root.
    [Fact]
    public void Every_command_is_reachable_from_the_composition_root()
    {
        var adapters = string.Join("\n", ProductSources("Infrastructure").Select(source => source.Text));
        var commands = Core.GetTypes().Where(type => !type.IsAbstract && !type.IsInterface && typeof(IStackCommand).IsAssignableFrom(type)).ToArray();
        Assert.NotEmpty(commands);
        foreach (var type in commands)
            Assert.Contains("new " + type.Name + "(", adapters, StringComparison.Ordinal);

    }

    [Fact]
    public void Fallible_ports_return_results()
    {
        Type[] ports = [typeof(IProcessRunner), typeof(IProcessLauncher), typeof(IEditorLauncher),
            typeof(IGitRepositoryReader), typeof(IGitBranchWriter), typeof(IStackStore), typeof(ISettingsStore),
            typeof(IBlobReader), typeof(IGitHubReader), typeof(IGitHubWriter), typeof(IGitHubPullRequestWriter),
            typeof(IPullRequestObjects), typeof(ISyntaxHighlighter), typeof(ISymbolNavigation), typeof(IJournal), typeof(IApplicationState),
            typeof(IDemoRepository), typeof(IDemoSnapshotReader), typeof(IAtomicFile), typeof(IHistoryStore), typeof(IHistoryGit), typeof(IHistoryRemoteReader),typeof(IChangeIdentityService),typeof(IReviewDraftRepository),typeof(IWorkspaceBackupService),typeof(IObjectStore),typeof(IDiagnosticSink)];
        foreach (var port in ports)
            foreach (var method in port.GetMethods().Where(method => !method.IsSpecialName))
            {
                var type = method.ReturnType;
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                    type = type.GetGenericArguments()[0];
                Assert.True(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>), $"{port.Name}.{method.Name} must return Result<T>");
            }
    }

    [Fact]
    public void Ui_features_use_ports_and_the_event_error_boundary()
    {
        foreach (var source in ProductSources("Desktop").Where(source => source.Path.Contains("Features") || source.Path.Contains("Shell")))
        {
            Assert.DoesNotContain("using Stacker.Infrastructure", source.Text);
            Assert.DoesNotContain("async void", source.Text);
            Assert.DoesNotMatch(@"\b(File|Directory)\.(Read|Write|Exists|Open|Create|Delete|Move|Enumerate)", source.Text);
        }
    }

    [Fact]
    public void Workspace_controllers_depend_on_typed_presentations()
    {
        foreach (var source in ProductSources("Desktop").Where(s => s.Path.EndsWith("Controller.cs", StringComparison.Ordinal) && !s.Path.Contains("Review")))
            Assert.DoesNotContain("MainViewModel", source.Text);
    }

    [Fact]
    public void Object_operations_require_the_selected_repository_and_never_create_a_private_clone()
    {
        foreach (var port in new[] { typeof(IHistoryGit), typeof(IPullRequestObjects) })
            foreach (var method in port.GetMethods())
                Assert.Equal(typeof(RepositoryIdentity), method.GetParameters()[0].ParameterType);
        foreach (var source in ProductSources("Infrastructure").Where(s => s.Path.Contains("History") || s.Path.Contains("Repositories") || s.Path.EndsWith("LocalPullRequestObjects.cs", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("[\"init\", \"--bare\"]", source.Text);
            Assert.DoesNotContain("[\"clone\"", source.Text);
        }
    }

    [GeneratedRegex("Problem\\.Of\\(\"|Result<[^>]+>\\.Fail\\(\"")]
    private static partial Regex ProblemLiteral();
}
