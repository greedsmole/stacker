// Purpose: Platform-neutral stand-ins for the device paths Git cannot always use on Windows.
namespace Stacker.Infrastructure.Framework;

/// <summary>Paths that make Git ignore hooks and user configuration without relying on "/dev/null" or "NUL".</summary>
/// <remarks>Windows Git refuses a device path where it expects a readable directory or file, so isolation cannot be
/// requested through a platform device name. One temporary directory per process instead holds an empty
/// <c>no-hooks</c> directory, usable as <c>core.hooksPath</c>, and an empty <c>empty-gitconfig</c> file, usable as
/// <c>GIT_CONFIG_GLOBAL</c> usually together with <c>GIT_CONFIG_NOSYSTEM=1</c> for a fully isolated configuration.
/// Each value is created on first use and then reused, so repeated commands do not accumulate directories; the
/// directory is never deleted, because the process owns it for its lifetime and the operating system reclaims the
/// temporary area afterwards. Nothing here reads repository state, so the paths are safe to resolve from any thread.</remarks>
public static class Neutral
{
    /// <summary>The temporary root both stand-ins live in, created in the system temporary directory with the
    /// <c>stacker-policy-</c> prefix.</summary>
    /// <remarks>Created once: <see cref="Lazy{T}"/> publishes a single value under concurrent first access, so Git
    /// commands issued in parallel during start-up share one directory instead of racing to create several. The prefix
    /// is what identifies the folder as Stacker's if the temporary area is ever inspected by hand.</remarks>
    private static readonly Lazy<string> PolicyRoot = new(() => Directory.CreateTempSubdirectory("stacker-policy-").FullName);
    /// <summary>The empty directory passed as <c>core.hooksPath</c>, created under <see cref="PolicyRoot"/> on first
    /// use.</summary>
    /// <remarks>Git resolves every hook name under <c>core.hooksPath</c>, so pointing it at an empty directory means no
    /// repository, global or template hook can run during an application-driven command: a hook cannot block a rebase,
    /// rewrite a commit or change the result the user approved. The directory is created because Git requires the path
    /// to exist, and creating it once per process is enough.</remarks>
    private static readonly Lazy<string> HooksDirectory = new(() =>
    {
        var path = Path.Combine(PolicyRoot.Value, "no-hooks");
        Directory.CreateDirectory(path);
        return path;
    });
    /// <summary>An empty configuration file used as <c>GIT_CONFIG_GLOBAL</c> so user-level settings and aliases do not
    /// apply.</summary>
    /// <remarks>The file is written only when missing, so a leftover file from an earlier process is reused and the
    /// cost is one existence check afterwards. Empty is a valid configuration rather than "no file": Git reads it and
    /// finds no settings, which is what makes a demo or verification command independent of the user's aliases,
    /// signing and pull/push defaults. A repository's own configuration still applies, so this isolates user-level
    /// configuration rather than all configuration.</remarks>
    private static readonly Lazy<string> EmptyConfig = new(() =>
    {
        var path = Path.Combine(PolicyRoot.Value, "empty-gitconfig");
        if (!File.Exists(path))
            File.WriteAllText(path, "");
        return path;
    });

    /// <summary>An empty directory usable as <c>core.hooksPath</c> on every platform.</summary>
    /// <remarks>Use it as <c>-c core.hooksPath=&lt;path&gt;</c> when a command must not run repository hooks. The value
    /// is resolved once and is stable for the process, so it can be compared between calls; a directory is created on
    /// first access, which is the only side effect.</remarks>
    public static string HooksPath => HooksDirectory.Value;
    /// <summary>An empty file usable as <c>GIT_CONFIG_GLOBAL</c> on every platform.</summary>
    /// <remarks>Set it in a child process's environment, normally with <c>GIT_CONFIG_NOSYSTEM=1</c>, to run that
    /// command without the user's global configuration. The path is resolved once per process; the empty file is
    /// written only when it is absent.</remarks>
    public static string EmptyConfigFile => EmptyConfig.Value;
}
