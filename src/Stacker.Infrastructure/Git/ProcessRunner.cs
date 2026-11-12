using Stacker.Core.Framework;
// Purpose: Runs external tools without a shell, concurrently drains streams, enforces cancellation/time/output bounds, and records Git diagnostics.
using System.Diagnostics;
using System.Text;
using Stacker.Core;

namespace Stacker.Infrastructure;

/// <summary>Runs external tools without a shell, drains both output pipes concurrently, and enforces the request's
/// timeout and output cap.</summary>
/// <remarks>This is the one process boundary for Git, GitHub CLI and the editor hand-off, so it never builds a command
/// line: arguments are added one by one and no shell parses them. Every bound arrives on the request, which means a
/// caller cannot run a tool without a timeout or an output cap. Git requests have repository-redirecting environment
/// variables removed first, so a variable inherited from a terminal cannot point a command at a different repository.
/// Only Git invocations reach the diagnostics log; a failed run still returns the tool's own output to the caller, and
/// the log is optional, so a runner without one behaves identically.</remarks>
/// <param name="commandLog">Diagnostics log that records Git invocations. When omitted, a throwaway instance decides
/// whether a request is Git, so the environment cleanup still applies but nothing is logged or displayed.</param>
public sealed class ProcessRunner(GitCommandLog? commandLog = null) : IProcessRunner, IProcessLauncher
{
    /// <summary>Starts a program without waiting for it, for editors and other interactive tools.</summary>
    /// <remarks>Fire-and-forget by contract: no stream is redirected, so the child keeps running after this call and
    /// after the application exits, and its exit code is never observed. The process handle is disposed immediately,
    /// which releases the handle without terminating the child. The only reported failure is a program that cannot be
    /// started, and the message points at Settings, where a full path can be entered when the program is not on PATH.</remarks>
    /// <param name="request">The executable, its already-split arguments, working directory and environment edits.</param>
    /// <exception cref="StackerException">The program could not be started, for example because it is missing or the
    /// configured path is wrong.</exception>
    public void Start(ProcessRequest request)
    {
        if ((commandLog ?? new GitCommandLog()).IsGit(request)) request = GitEnvironment.ForRepository(request);
        var info = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in request.Arguments) info.ArgumentList.Add(arg);
        if (request.Environment is not null)
            foreach (var (key, value) in request.Environment) info.Environment[key] = value;
        if (request.UnsetEnvironment is not null) foreach (var key in request.UnsetEnvironment) info.Environment.Remove(key);
        try { using var process = Process.Start(info); }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new StackerException($"Cannot start {request.Executable}. Set the full path in Settings if it is not on PATH. {ex.Message}"); }
    }

    /// <summary>Executes without a shell, logs Git-only diagnostics, and returns structured process output.</summary>
    /// <remarks>One run produces two log events: a running entry before the process starts and its finished replacement
    /// afterwards, carrying the elapsed time and either the exit code with the error stream or the exception message.
    /// The logged detail is redacted and truncated by the log itself, so a credential echoed by a tool is not shown
    /// verbatim. Logging never changes the result: a failure is logged and then rethrown unchanged.</remarks>
    /// <param name="request">The executable, arguments and resource bounds for this run.</param>
    /// <param name="ct">Cancels the run and kills the child process tree.</param>
    /// <returns>The exit code and both decoded output streams; an empty stream is an empty string.</returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled; this is the one failure that is
    /// not converted into a <see cref="StackerException"/>.</exception>
    /// <exception cref="OutputLimitException">Combined output passed <see cref="ProcessRequest.MaxOutputBytes"/>.</exception>
    /// <exception cref="StackerException">The program could not be started, or the run passed its timeout.</exception>
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        if ((commandLog ?? new GitCommandLog()).IsGit(request)) request = GitEnvironment.ForRepository(request);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var logId = commandLog?.Begin(request, started);
        try
        {
            var result = await RunCoreAsync(request, ct);
            commandLog?.Finish(logId, watch.Elapsed, result.ExitCode, result.StdErr.Trim());
            return result;
        }
        catch (Exception ex)
        {
            commandLog?.Finish(logId, watch.Elapsed, null, ex.Message);
            throw;
        }
    }
    // Both pipes must be drained concurrently: waiting on only one can deadlock when the other fills its OS buffer.
    /// <summary>Starts the child with all three streams redirected, enforces the timeout and the combined output cap,
    /// and guarantees the process tree is killed before a failure is reported.</summary>
    /// <remarks>The caller's token and the timeout token are linked into one source, so cancellation, the deadline and
    /// a failing drain all stop the same run. The caller's token is checked before the process exists, so a cancelled
    /// call never spawns anything. Input is written and then closed, which lets a tool waiting for end-of-input
    /// proceed, and all three stream tasks are started before the exit is awaited, which is what keeps two full pipes
    /// from deadlocking. The timeout defaults to 30 seconds when the request carries none; when it elapses the child
    /// is killed.
    /// <para>The output cap is combined across stdout and stderr: both readers add the bytes they took to one counter
    /// and the run fails as soon as the total passes <see cref="ProcessRequest.MaxOutputBytes"/> (exactly the cap is
    /// still accepted). The check bounds the memory one run may retain and turns an oversized answer into a clear
    /// message instead of memory pressure.</para>
    /// <para>Cleanup after a failure is bounded: the whole process tree is killed, the linked source is cancelled so
    /// both readers stop, and the exit and the drain tasks get 5 seconds to finish, because a child that cannot be
    /// reaped must not hang the cancellation path. When either reader observed the output limit, that exception is
    /// rethrown in preference to the cancellation the kill caused, so the user sees the real reason; the caller's
    /// cancellation is rethrown next, the deadline becomes a timeout message, and anything else is rethrown
    /// unchanged.</para></remarks>
    /// <param name="request">The executable, arguments, timeout, output cap and environment for this run.</param>
    /// <param name="ct">The caller's token, linked with the timeout for the whole run.</param>
    /// <returns>The exit code and both streams decoded as UTF-8.</returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    /// <exception cref="OutputLimitException">Combined output passed the request's cap.</exception>
    /// <exception cref="StackerException">The program could not be started, or the timeout elapsed.</exception>
    private static async Task<ProcessResult> RunCoreAsync(ProcessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(request.Timeout ?? TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var info = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in request.Arguments) info.ArgumentList.Add(arg);
        if (request.Environment is not null)
            foreach (var (key, value) in request.Environment) info.Environment[key] = value;
        if (request.UnsetEnvironment is not null)
            foreach (var key in request.UnsetEnvironment) info.Environment.Remove(key);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new StackerException(Problem.Of(ProblemCodes.GitFailed, $"Cannot start {request.Executable}. Check its installation and path. {ex.Message}")
                with { Certainty = EffectCertainty.NotStarted });
        }
        async Task WriteInputAsync()
        {
            try
            {
                if (request.StandardInput is not null) await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), linked.Token);
                process.StandardInput.Close();
            }
            catch { await linked.CancelAsync(); throw; }
        }
        var total = 0;
        async Task<string> ReadAsync(Stream stream)
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer, linked.Token)) != 0)
                {
                    if (Interlocked.Add(ref total, count) > request.MaxOutputBytes) throw new OutputLimitException();
                    output.Write(buffer, 0, count);
                }
                return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            }
            catch { await linked.CancelAsync(); throw; }
        }
        var stdout = ReadAsync(process.StandardOutput.BaseStream);
        var stderr = ReadAsync(process.StandardError.BaseStream);
        var stdin = WriteInputAsync();
        try
        {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdout, stderr, stdin);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* The process exited between HasExited and Kill. */ }
            await linked.CancelAsync();
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(reap.Token); }
            catch (OperationCanceledException) { /* An unreapable child must not hang the timeout path. */ }
            catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr, stdin).WaitAsync(reap.Token); } catch { /* Drain only within the cleanup budget. */ }
            if (stdout.Exception?.GetBaseException() is OutputLimitException || stderr.Exception?.GetBaseException() is OutputLimitException)
                throw new OutputLimitException();
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new StackerException("Operation timed out. Retry or select a smaller comparison.");
            throw;
        }
    }

    /// <inheritdoc cref="IProcessRunner.RunAsync"/>
    /// <remarks>The port returns a <see cref="Result{T}"/>, so a failure inside the runner is translated into a problem
    /// carrying <see cref="ProblemCodes.GitFailed"/> instead of crossing the boundary as an exception; cancellation is
    /// rethrown unchanged so a cancelled command stays distinguishable from a failed one. The translated problem keeps
    /// the default unknown certainty, which is what stops a failed command whose effect may have landed from being
    /// presented as safe to retry.</remarks>
    Task<Result<ProcessResult>> IProcessRunner.RunAsync(ProcessRequest request, CancellationToken ct) => Boundary.CaptureAsync(() => RunAsync(request, ct), ProblemCodes.GitFailed);

    /// <inheritdoc cref="IProcessLauncher.Start"/>
    /// <remarks>Only the start can fail, and it fails as a <see cref="ProblemCodes.GitFailed"/> problem so the caller
    /// can show it without handling an exception. Once the child has started, this boundary has no further knowledge
    /// of it: no exit code, no duration and no output are reported.</remarks>
    Result<Unit> IProcessLauncher.Start(ProcessRequest request) => Boundary.Capture(() => Start(request), ProblemCodes.GitFailed);
}

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
    public string? Override { get => _override; set { _override = value; _resolved = null; } }
    /// <inheritdoc/>
    /// <remarks>Candidates are tried in a fixed order: the override, the memoized value, every PATH entry (each
    /// trimmed of quotes some installers leave in PATH), then the platform defaults, so a GUI application that
    /// inherited a minimal PATH still finds a normal installation. The first candidate that exists becomes the
    /// memoized value; when none exists the bare program name is memoized too, and the failure surfaces when the
    /// process is started rather than here.</remarks>
    public string Resolve()
    {
        if (!string.IsNullOrWhiteSpace(_override)) return _override;
        if (_resolved is not null) return _resolved;
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), name));
        var defaults = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe") }
            : new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" };
        return _resolved = paths.Concat(defaults).FirstOrDefault(File.Exists) ?? name;
    }
}
