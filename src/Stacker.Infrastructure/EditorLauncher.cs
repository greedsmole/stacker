// Purpose: Resolves the editor the user chose and opens files in it, never through a shell.
using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed class EditorLauncher(IProcessRunner runner, GitExecutable git, IProcessLauncher launcher) : IEditorLauncher
{
    private static readonly IReadOnlyDictionary<string, string> EnvironmentOverrides = new Dictionary<string, string>
    { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_PAGER"] = "cat", ["LC_ALL"] = "C" };

    /// <summary>Explicit setting, then git core.editor, then the environment, then the platform default.</summary>
    public async Task<string?> ResolveAsync(string root, string? overrideCommand, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(overrideCommand)) return overrideCommand.Trim();
        var configured = await runner.RunAsync(new(git.Resolve(), ["-c", "core.hooksPath=" + Framework.Neutral.HooksPath, "config", "--get", "core.editor"],
            root, TimeSpan.FromSeconds(15), Environment: EnvironmentOverrides), ct);
        if (configured.ExitCode == 0 && configured.StdOut.Trim().Length > 0) return configured.StdOut.Trim();
        foreach (var name in new[] { "GIT_EDITOR", "VISUAL", "EDITOR" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && value.Trim().Length > 0) return value.Trim();
        return OperatingSystem.IsMacOS() ? "open -t" : OperatingSystem.IsWindows() ? "notepad" : "xdg-open";
    }

    /// <summary>Opens the paths in the resolved editor and reports which command was used.</summary>
    public async Task<string?> OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct = default)
    {
        var command = await ResolveAsync(root, overrideCommand, ct);
        if (string.IsNullOrWhiteSpace(command)) return null;
        var (program, arguments) = CommandLine.Split(command);
        launcher.Start(new(program, [.. arguments, .. paths], root));
        return command;
    }
}
