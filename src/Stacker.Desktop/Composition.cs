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
            .AddSingleton<IGitToolSettings>(s => s.GetRequiredService<GitExecutable>())
            .AddSingleton<IGhToolSettings>(s => s.GetRequiredService<GhExecutable>())
            .AddSingleton(s => new GitCommandLog { GitExecutablePath = s.GetRequiredService<GitExecutable>().Resolve() })
            .AddSingleton<ProcessRunner>(s => new ProcessRunner(s.GetRequiredService<GitCommandLog>()))
            .AddSingleton<IGitDiagnosticLog>(s => s.GetRequiredService<GitCommandLog>())
            .AddSingleton<IProcessRunner>(s => s.GetRequiredService<ProcessRunner>())
            .AddSingleton<IProcessLauncher>(s => s.GetRequiredService<ProcessRunner>())
            .AddSingleton<IEditorLauncher, EditorLauncher>()
            .AddSingleton<IAtomicFile, AtomicFile>().AddSingleton<IClock, SystemClock>()
            .AddSingleton<IFileLockFactory>(_ => new FileLockFactory(Path.Combine(root, "locks")))
            .AddSingleton<IStateStore<JournalLog>>(s => new JsonStateStore<JournalLog>(s.GetRequiredService<IAtomicFile>(), Path.Combine(root, "journal"), quarantineCorrupt: false))
            .AddSingleton<IJournal>(s => new RoutingJournal(new JsonJournal(s.GetRequiredService<IStateStore<JournalLog>>(), s.GetRequiredService<IClock>()), s.GetRequiredService<SqliteHistoryStore>(), s.GetRequiredService<IClock>()))
            .AddSingleton<CommandRunner>().AddSingleton<CommandBus>()
            .AddSingleton(_ => new ApplicationStore(root))
            .AddSingleton(s => new SqliteHistoryStore(root))
            .AddSingleton<IHistoryStore>(s => s.GetRequiredService<SqliteHistoryStore>())
            .AddSingleton<IReviewDraftRepository>(s => s.GetRequiredService<SqliteHistoryStore>())
            .AddSingleton<IChangeIdentityService, ChangeIdentityService>()
            .AddSingleton<IApplicationState>(s => new ReviewApplicationState(s.GetRequiredService<ApplicationStore>(), s.GetRequiredService<SqliteHistoryStore>()))
            .AddSingleton<IWorkspaceBackupService, WorkspaceBackupService>()
            .AddSingleton<IObjectStore, ObjectStore>().AddSingleton<IDiagnosticSink, DiagnosticSink>()
            .AddSingleton<IHistoryGit, HistoryGit>().AddSingleton<ChangeGraphService>()
            .AddSingleton<VersionComparisonService>().AddSingleton<ReviewProgressService>().AddSingleton<GitHubCli>()
            .AddSingleton<CommandGitHubWriter>()
            .AddSingleton<IHistoryRemoteReader>(s => s.GetRequiredService<GitHubCli>())
            .AddSingleton<IGitHubReader>(s => s.GetRequiredService<GitHubCli>()).AddSingleton<IGitHubWriter>(s => s.GetRequiredService<CommandGitHubWriter>())
            .AddSingleton<IGitHubPullRequestWriter>(s => s.GetRequiredService<CommandGitHubWriter>())
            .AddSingleton<IGitObjectCache, GitObjectCache>().AddSingleton<ISyntaxHighlighter, TextMateSyntaxHighlighter>()
            .AddSingleton<DemoRepositoryGenerator>().AddSingleton<IDemoRepository>(s => s.GetRequiredService<DemoRepositoryGenerator>())
            .AddSingleton<GitRepositoryReader>()
            .AddSingleton<IGitRepositoryReader>(s => new CachingGitReader(s.GetRequiredService<GitRepositoryReader>(),diagnostics:s.GetRequiredService<IDiagnosticSink>(),objects:s.GetRequiredService<IObjectStore>(),state:s.GetRequiredService<IApplicationState>()))
            .AddSingleton<IGitBranchWriter, LocalGitClient>()
            .AddSingleton(s => new YamlStackStore(s.GetRequiredService<IAtomicFile>(), s.GetRequiredService<IFileLockFactory>()))
            .AddSingleton<IStackStore, CommandStackStore>()
            .AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(Path.Combine(root, "settings")))
            .AddSingleton<StackService>().AddSingleton<DiffService>().AddSingleton<MainViewModel>()
            .BuildServiceProvider();
    }
}
