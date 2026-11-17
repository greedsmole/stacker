using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>One tool invocation as the diagnostics tab shows it: where it ran, how long it took and how it ended.</summary>
/// <remarks>Created when the process starts, while it is still running and has no exit code, and replaced on
/// completion, which is why <see cref="Id"/> is the stable handle across both notifications. The adapter redacts
/// embedded credentials before the entry is created, so a username and password in a remote URL never appear in the
/// log or in the diagnostics export.</remarks>
/// <param name="Id">Identity of this invocation, used to update the same row instead of appending a second one.</param>
/// <param name="StartedAt">When the process was started, with its offset preserved for local display.</param>
/// <param name="WorkingDirectory">The explicit working directory the command ran in; empty when none was set.</param>
/// <param name="Command">The rendered command line with its tool name, arguments quoted and credentials
/// redacted.</param>
/// <param name="Duration">Elapsed time from start to exit; <see cref="TimeSpan.Zero"/> while the entry is running.</param>
/// <param name="ExitCode">The process exit code; <see langword="null"/> while running and when no code was produced
/// because the process could not start or was cancelled.</param>
/// <param name="Detail">Diagnostic text from the command: standard error on completion, or the start failure.
/// The implementation truncates it to 2,048 characters and appends an ellipsis, so a chatty command cannot grow the
/// log without bound.</param>
/// <param name="IsRunning">Whether the process is still running, so the UI can show progress instead of a
/// duration.</param>
public sealed record GitCommandEntry(Guid Id, DateTimeOffset StartedAt, string WorkingDirectory, string Command,
    TimeSpan Duration, int? ExitCode, string Detail, bool IsRunning)
{
    public bool IsGitHub
    {
        get; init;
    }
    public bool IsFailure
    {
        get; init;
    }

    /// <summary>One-line rendering for the log list and the clipboard export: time, duration, outcome and command.</summary>
    /// <remarks>The duration is formatted with one decimal place, so a sub-second command still shows a value
    /// (for example <c>0.4s</c>). While the command runs, the duration column reads <c>running</c> and the outcome
    /// column is empty, which leaves its two spaces behind. A finished entry without an exit code reads
    /// <c>failed</c>, because a process that never produced a code did not complete normally. Columns are separated
    /// by two spaces so the text stays parseable when copied out.</remarks>
    public string Summary => $"{StartedAt.LocalDateTime:HH:mm:ss}  {(IsRunning ? "running" : Duration.TotalSeconds.ToString("0.0") + "s")}  {Outcome}  {Command}";
    private string Outcome => IsRunning ? "" : ExitCode switch
    {
        0 when IsFailure => "API error (exit 0)",
        int code => $"exit {code}",
        _ => "failed"
    };
}
