// Purpose: Desktop process entry point, single-instance guard, and Avalonia platform configuration.
using Avalonia;
using Stacker.Infrastructure.Framework;
namespace Stacker.Desktop;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // One writer per machine: a second launch would otherwise race the same application-data files.
        using var single = new SingleInstanceLock(App.AppRoot);
        if (!single.TryAcquire())
        {
            Console.Error.WriteLine("Stacker is already running. Close the other window first.");
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
