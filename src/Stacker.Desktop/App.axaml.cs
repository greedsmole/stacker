using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Infrastructure;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;
public partial class App : Application
{
    private ServiceProvider? _services;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = new ServiceCollection()
                .AddSingleton<GitExecutable>().AddSingleton<GhExecutable>().AddSingleton<IProcessRunner, ProcessRunner>()
                .AddSingleton<ApplicationStore>().AddSingleton<GitHubCli>()
                .AddSingleton<IGitHubReader>(s => s.GetRequiredService<GitHubCli>()).AddSingleton<IGitHubWriter>(s => s.GetRequiredService<GitHubCli>())
                .AddSingleton<IGitObjectCache, GitObjectCache>().AddSingleton<ISyntaxHighlighter, TextMateSyntaxHighlighter>()
                .AddSingleton<DemoRepositoryGenerator>()
                .AddSingleton<IGitRepositoryReader, GitRepositoryReader>()
                .AddSingleton<IStackStore, YamlStackStore>().AddSingleton<ISettingsStore>(_ => new JsonSettingsStore())
                .AddSingleton<StackService>().AddSingleton<DiffService>().AddSingleton<MainViewModel>()
                .BuildServiceProvider();
            desktop.MainWindow = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
            desktop.Exit += (_, _) => _services.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
