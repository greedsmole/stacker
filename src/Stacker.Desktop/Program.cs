// Purpose: Desktop process entry point, single-instance guard, and Avalonia platform configuration.
using Avalonia;
using Stacker.Infrastructure.Framework;
namespace Stacker.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var dataRootIndex = Array.IndexOf(args, "--data-root");
        if (dataRootIndex >= 0 && dataRootIndex + 1 < args.Length)
            App.UseDataRoot(args[dataRootIndex + 1]);
        // One writer per application-data profile: a second launch would otherwise race the same application-data files.
        using var single = new SingleInstanceLock(App.AppRoot);
        if (!single.TryAcquire())
        {
            Console.Error.WriteLine("Stacker is already running. Close the other window first.");
            return 0;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
                StartupGuard.Record(App.AppRoot, error);
        };
        return StartupGuard.Run(App.AppRoot,
            () => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args),
            OperatingSystem.IsMacOS() ? StartupGuard.ProbeMacDisplay : null);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
