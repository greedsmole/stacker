// Purpose: Platform-neutral stand-ins for the device paths Git cannot always use on Windows.
namespace Stacker.Infrastructure.Framework;

/// <summary>Paths that make Git ignore hooks and user configuration without relying on "/dev/null" or "NUL".</summary>
public static class Neutral
{
    private static readonly Lazy<string> HooksDirectory = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "stacker-no-hooks");
        Directory.CreateDirectory(path);
        return path;
    });
    private static readonly Lazy<string> EmptyConfig = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "stacker-empty-gitconfig");
        if (!File.Exists(path)) File.WriteAllText(path, "");
        return path;
    });

    /// <summary>An empty directory usable as <c>core.hooksPath</c> on every platform.</summary>
    public static string HooksPath => HooksDirectory.Value;
    /// <summary>An empty file usable as <c>GIT_CONFIG_GLOBAL</c> on every platform.</summary>
    public static string EmptyConfigFile => EmptyConfig.Value;
}
