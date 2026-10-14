// Purpose: Application composition root: loads Avalonia resources and registers the services shared by the desktop UI.
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;
using Stacker.Infrastructure.Framework;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;
public partial class App : Application
{
    /// <summary>Everything Stacker owns lives here: drafts, snapshots, journals, locks.</summary>
    public static string AppRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stacker", "v3");
    private ServiceProvider? _services;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = Composition.Build();
            desktop.MainWindow = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
            desktop.Exit += (_, _) => _services.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
