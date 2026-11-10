using Stacker.Core.Framework;
// Purpose: Resolves the editor the user chose and opens files in it, never through a shell.
using Stacker.Core;
namespace Stacker.Infrastructure;

/// <summary>Resolves the editor the user chose and opens files in it, without ever involving a shell.</summary>
/// <remarks>Resolution is layered so a machine that already configured Git or the environment needs no duplicate setting, while a
/// bare machine still gets a usable default. The resolved command is returned so the UI can show exactly what will run, and the
/// launch is detached through <see cref="IProcessLauncher"/>: the app does not wait for the editor, capture its output or track its
/// exit code. Git's configuration is read once per resolution with a 15-second budget and with prompting and paging disabled; a
/// configuration read that cannot start or exceeds the budget is a failure rather than an empty result.</remarks>
/// <param name="runner">Process boundary used to read Git's configuration.</param>
/// <param name="git">Resolved Git binary, so a configured custom installation is honoured.</param>
/// <param name="launcher">Fire-and-forget launcher that starts the editor process.</param>
public sealed class EditorLauncher(IProcessRunner runner, GitExecutable git, IProcessLauncher launcher) : IEditorLauncher
{
    /// <summary>Environment for the configuration read: never prompt for credentials, never page, and parse messages in a stable C locale.</summary>
    /// <remarks>The read is a single <c>git config --get core.editor</c>; a terminal prompt or a pager would block a child process that
    /// has no terminal attached, so both are disabled up front.</remarks>
    private static readonly IReadOnlyDictionary<string, string> EnvironmentOverrides = new Dictionary<string, string>
    { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_PAGER"] = "cat", ["LC_ALL"] = "C" };

    /// <summary>Explicit setting, then git core.editor, then the environment, then the platform default.</summary>
    /// <param name="root">Repository root used as the working directory for the configuration read.</param>
    /// <param name="overrideCommand">The user's saved editor setting, or null/blank to fall through to the next layer.</param>
    /// <param name="ct">Cancels the configuration read; a launched editor is unaffected because nothing is launched here.</param>
    /// <returns>The command text to show and reuse. The shipped implementation always resolves the platform default, so this is null
    /// only in theory.</returns>
    /// <remarks>An override is trimmed and must be non-blank, so an empty setting never wins. Git's <c>core.editor</c> is read with
    /// <c>config --get</c> under the neutral empty hooks directory, which keeps user hooks out of a setting read; the command is
    /// given 15 seconds, after which the child is killed and the read fails. A successful read must exit 0 and print a non-blank
    /// value. The environment layer checks <c>GIT_EDITOR</c>, then <c>VISUAL</c>, then <c>EDITOR</c>, again skipping blanks. The
    /// platform default is <c>open -t</c> on macOS, <c>notepad</c> on Windows and <c>xdg-open</c> elsewhere. Why the configuration
    /// read got 15 seconds rather than another budget is not recorded.</remarks>
    public async Task<string?> ResolveAsync(string root, string? overrideCommand, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(overrideCommand)) return overrideCommand.Trim();
        var configured = (await runner.RunAsync(new(git.Resolve(), ["-c", "core.hooksPath=" + Framework.Neutral.HooksPath, "config", "--get", "core.editor"],
            root, TimeSpan.FromSeconds(15), Environment: EnvironmentOverrides), ct)).OrThrow();
        if (configured.ExitCode == 0 && configured.StdOut.Trim().Length > 0) return configured.StdOut.Trim();
        foreach (var name in new[] { "GIT_EDITOR", "VISUAL", "EDITOR" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && value.Trim().Length > 0) return value.Trim();
        return OperatingSystem.IsMacOS() ? "open -t" : OperatingSystem.IsWindows() ? "notepad" : "xdg-open";
    }

    /// <summary>Opens the paths in the resolved editor and reports which command was used.</summary>
    /// <param name="root">Working directory for the editor process, normally the repository root.</param>
    /// <param name="paths">Files to open; each is appended as its own argument, so a path with spaces needs no quoting.</param>
    /// <param name="overrideCommand">The user's saved editor setting, or null/blank to resolve from the usual layers.</param>
    /// <param name="ct">Cancels only the resolution step; the launched editor keeps running.</param>
    /// <returns>The command text that was launched, or null when the resolution produced no command.</returns>
    /// <remarks>The command text is split by <see cref="CommandLine.Split"/>, which is quote-aware and hands the program and its
    /// arguments separately to <see cref="IProcessLauncher.Start"/>; no shell is involved, so quotes and shell metacharacters in a
    /// path are literal. The call returns as soon as the process has started: a successful result means the operating system accepted
    /// the launch, not that the editor opened the files. A blank resolution returns null instead of launching anything.</remarks>
    /// <exception cref="StackerException">When the command text is empty (from <see cref="CommandLine.Split"/>) or the editor cannot
    /// be started (from the launcher boundary).</exception>
    public async Task<string?> OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct = default)
    {
        var command = await ResolveAsync(root, overrideCommand, ct);
        if (string.IsNullOrWhiteSpace(command)) return null;
        var (program, arguments) = CommandLine.Split(command);
        (launcher.Start(new(program, [.. arguments, .. paths], root))).OrThrow();
        return command;
    }

    /// <inheritdoc cref="IEditorLauncher.ResolveAsync"/>
    /// <remarks>The port member returns a failed result whose problem carries the catalogued <see cref="ProblemCodes.GitFailed"/> code,
    /// or the exception's own problem when it has one, instead of throwing; cancellation is rethrown as
    /// <see cref="OperationCanceledException"/>. The resolution order and the 15-second configuration budget are those of the public
    /// method.</remarks>
    Task<Result<string?>> IEditorLauncher.ResolveAsync(string root, string? overrideCommand, CancellationToken ct) => Boundary.CaptureAsync(() => ResolveAsync(root, overrideCommand, ct), ProblemCodes.GitFailed);

    /// <inheritdoc cref="IEditorLauncher.OpenAsync"/>
    /// <remarks>The port member captures a resolution or launch failure as a failed result with <see cref="ProblemCodes.GitFailed"/>
    /// rather than throwing; the returned command and the detached launch behave exactly as in the public method.</remarks>
    Task<Result<string?>> IEditorLauncher.OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct) => Boundary.CaptureAsync(() => OpenAsync(root, paths, overrideCommand, ct), ProblemCodes.GitFailed);
}
