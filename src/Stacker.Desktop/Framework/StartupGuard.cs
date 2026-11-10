using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace Stacker.Desktop;

/// <summary>Checks display-link availability before Avalonia initializes its non-reentrant native backend.</summary>
public static class StartupGuard
{
    /// <summary>Retries a display probe until success or the positive attempt limit is exhausted.</summary>
    /// <param name="probe">Probe returning zero on success.</param><param name="attempts">Positive maximum probe count.</param><param name="wait">Delay between failed probes.</param>
    public static int WaitForDisplay(Func<int> probe, int attempts, Action wait)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        for (var i = 0; ; i++)
        {
            var status = probe();
            if (status == 0 || i == attempts - 1) return status;
            wait();
        }
    }
    /// <summary>Runs startup and records a diagnostic crash report when native initialization fails.</summary>
    /// <param name="dataRoot">Application data directory.</param><param name="start">Startup action.</param><param name="display">Optional display probe.</param><param name="wait">Optional retry delay.</param>
    public static int Run(string dataRoot, Action start, Func<int>? display = null, Action? wait = null)
    {
        try
        {
            if (display is not null)
            {
                var status = WaitForDisplay(display, 31, wait ?? (() => Thread.Sleep(1000)));
                if (status != 0) throw new InvalidOperationException($"macOS display link is unavailable (CoreVideo {status}). Wake/connect a display and start Stacker again. No workspace was opened.");
            }
            start();
            return 0;
        }
        catch (Exception ex)
        {
            Record(dataRoot, ex);
            return 1;
        }
    }
    /// <summary>Writes a timestamped startup failure to stderr and the diagnostics directory.</summary>
    /// <param name="dataRoot">Application data directory.</param><param name="error">Failure to record.</param>
    public static void Record(string dataRoot, Exception error)
    {
        var text = $"{DateTimeOffset.UtcNow:O}\nStacker {typeof(StartupGuard).Assembly.GetName().Version}\n{error}\n";
        Console.Error.WriteLine(text);
        try
        {
            var folder = Path.Combine(dataRoot, "diagnostics"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "last-crash.log"), text);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Cannot save crash report: " + logError.Message); }
    }
    [SupportedOSPlatform("macos")]
    /// <summary>Returns CoreVideo's active-display probe status on macOS.</summary>
    public static int ProbeMacDisplay()
    {
        var result = CVDisplayLinkCreateWithActiveCGDisplays(out var link);
        if (link != IntPtr.Zero) CVDisplayLinkRelease(link);
        return result;
    }
    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr displayLink);
    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern void CVDisplayLinkRelease(IntPtr displayLink);
}
