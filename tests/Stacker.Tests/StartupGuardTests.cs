using Stacker.Desktop;
namespace Stacker.Tests;

public sealed class StartupGuardTests
{
    [Fact]
    public void A_waking_display_is_rechecked_before_opening_the_workspace()
    {
        var probes = 0; var waits = 0; var started = false;
        Assert.Equal(0, StartupGuard.Run("unused", () => started = true, () => ++probes < 3 ? -6661 : 0, () => waits++));
        Assert.True(started); Assert.Equal(3, probes); Assert.Equal(2, waits);
    }
    [Fact]
    public void Unavailable_display_exits_with_diagnostics_without_initializing_Avalonia()
    {
        var root = Directory.CreateTempSubdirectory("startup-display-").FullName;
        try
        {
            var started = false; var waits = 0;
            Assert.Equal(1, StartupGuard.Run(root, () => started = true, () => -6661, () => waits++));
            Assert.False(started); Assert.Equal(30, waits);
            var log = File.ReadAllText(Path.Combine(root, "diagnostics", "last-crash.log"));
            Assert.Contains("-6661", log); Assert.Contains("Wake/connect", log);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Display_race_or_other_startup_failure_is_preserved_and_returns_failure()
    {
        var root = Directory.CreateTempSubdirectory("startup-error-").FullName;
        try
        {
            Assert.Equal(1, StartupGuard.Run(root, () => throw new InvalidOperationException("RenderTimer failure"), () => 0));
            Assert.Contains("RenderTimer failure", File.ReadAllText(Path.Combine(root, "diagnostics", "last-crash.log")));
        }
        finally { Directory.Delete(root, true); }
    }
}
