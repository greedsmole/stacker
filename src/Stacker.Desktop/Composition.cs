// Purpose: The single composition root, extracted so the whole object graph can be resolved by a test.
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
namespace Stacker.Desktop;

public static class Composition
{
    /// <summary>Builds every service the application uses; the root can be redirected for tests.</summary>
    public static ServiceProvider Build(string? appRoot = null)
    {
        var root = appRoot ?? App.AppRoot;
        return new ServiceCollection()
            .AddSingleton<GitExecutable>().AddSingleton<GhExecutable>()
            .AddSingleton(s => new GitCommandLog { GitExecutablePath = s.GetRequiredService<GitExecutable>().Resolve() })
            .AddSingleton<ProcessRunner>(s => new ProcessRunner(s.GetRequiredService<GitCommandLog>()))
            .AddSingleton<IProcessRunner>(s => s.GetRequiredService<ProcessRunner>())
            .AddSingleton<IProcessLauncher>(s => s.GetRequiredService<ProcessRunner>())
            .AddSingleton<IEditorLauncher, EditorLauncher>()
            .AddSingleton<IAtomicFile, AtomicFile>().AddSingleton<IClock, SystemClock>()
            .AddSingleton<IFileLockFactory>(_ => new FileLockFactory(Path.Combine(root, "locks")))
            .AddSingleton<IStateStore<JournalLog>>(s => new JsonStateStore<JournalLog>(s.GetRequiredService<IAtomicFile>(), Path.Combine(root, "journal")))
            .AddSingleton<IJournal>(s => new JsonJournal(s.GetRequiredService<IStateStore<JournalLog>>(), s.GetRequiredService<IClock>()))
            .AddSingleton(_ => new ApplicationStore(root)).AddSingleton<GitHubCli>()
            .AddSingleton<IGitHubReader>(s => s.GetRequiredService<GitHubCli>()).AddSingleton<IGitHubWriter>(s => s.GetRequiredService<GitHubCli>())
            .AddSingleton<IGitHubPullRequestWriter>(s => s.GetRequiredService<GitHubCli>())
            .AddSingleton<IGitObjectCache, GitObjectCache>().AddSingleton<ISyntaxHighlighter, TextMateSyntaxHighlighter>()
            .AddSingleton<DemoRepositoryGenerator>()
            .AddSingleton<GitRepositoryReader>()
            .AddSingleton<IGitRepositoryReader>(s => new CachingGitReader(s.GetRequiredService<GitRepositoryReader>()))
            .AddSingleton<IGitBranchWriter, GitBranchWriter>()
            .AddSingleton<IStackStore>(s => new YamlStackStore(s.GetRequiredService<IAtomicFile>(), s.GetRequiredService<IFileLockFactory>()))
            .AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(Path.Combine(root, "settings")))
            .AddSingleton<StackService>().AddSingleton<DiffService>().AddSingleton<MainViewModel>()
            .BuildServiceProvider();
    }
}
