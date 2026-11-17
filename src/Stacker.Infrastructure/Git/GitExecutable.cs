using System.Diagnostics;
using System.Text;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

/// <summary>Resolves the Git program once, from the user's setting or the usual installation locations.</summary>
/// <remarks>Resolution is lazy and cached because every Git command needs the path, and walking PATH for each one
/// would add a directory scan per invocation. A configured override always wins, so a user pointing at a specific
/// build is obeyed; otherwise PATH is searched first and the platform's common install directories second, so a Git
/// that exists outside the application's inherited PATH is still found. When nothing exists the bare program name is
/// returned instead of a fabricated path, which keeps the failure at start time with the message that names the tool
/// and points at Settings.</remarks>
public sealed class GitExecutable : IGitToolSettings
{
    /// <summary>The executable path taken from settings, or <see langword="null"/> when the user configured none.</summary>
    /// <remarks>Kept beside the memoized path because the setting can be cleared: assigning through
    /// <see cref="Override"/> clears the memoized value, so the next <see cref="Resolve"/> looks again instead of
    /// returning a path that no longer matches the setting.</remarks>
    private string? _override;
    /// <summary>The value produced by the last resolution, or <see langword="null"/> when nothing has been resolved
    /// yet.</summary>
    /// <remarks>The whole result is memoized, including a bare fallback name, so a Git installed after start-up is not
    /// noticed until the setting is reassigned or the application restarts.</remarks>
    private string? _resolved;
    /// <inheritdoc/>
    /// <remarks>The setter clears the memoized path so an edited setting takes effect on the next command without a
    /// restart. A blank value behaves as no override, which leaves exactly one decision between the setting and the
    /// lookup.</remarks>
    public string? Override
    {
        get => _override; set
        {
            _override = value;
            _resolved = null;
        }
    }
    /// <inheritdoc/>
    /// <remarks>Candidates are tried in a fixed order: the override, the memoized value, every PATH entry (each
    /// trimmed of quotes some installers leave in PATH), then the platform defaults, so a GUI application that
    /// inherited a minimal PATH still finds a normal installation. The first candidate that exists becomes the
    /// memoized value; when none exists the bare program name is memoized too, and the failure surfaces when the
    /// process is started rather than here.</remarks>
    public string Resolve()
    {
        if (!string.IsNullOrWhiteSpace(_override))
            return _override;
        if (_resolved is not null)
            return _resolved;
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), name));
        var defaults = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe") }
            : new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" };
        return _resolved = paths.Concat(defaults).FirstOrDefault(File.Exists) ?? name;
    }
}
