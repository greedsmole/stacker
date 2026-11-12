// Purpose: Bounded, thread-safe log of Git and GitHub CLI commands shown in the diagnostics tab.
using System.Text.RegularExpressions;
using Stacker.Core;

namespace Stacker.Infrastructure;

/// <summary>Bounded, thread-safe record of Git and GitHub CLI invocations, with credentials removed.</summary>
/// <remarks>One private lock guards the entry list, so a command thread can append or update while the UI takes a stable snapshot. The log
/// is a rolling window for the current session, not an audit trail: it keeps the newest 500 entries and drops the oldest when a further
/// command arrives, and each entry's detail text is cut at 2,048 characters. An HTTP(S) URL with embedded user information is rewritten
/// before any text is stored or raised. Notifications are raised outside the lock, so a subscriber may call <see cref="Snapshot"/> or
/// <see cref="Clear"/> without deadlocking.</remarks>
public sealed class GitCommandLog : IGitDiagnosticLog
{
    /// <summary>Guards the entry list so an append, an update, a snapshot and a clear cannot interleave.</summary>
    private readonly object _sync = new();
    /// <summary>Entries oldest first; a 501st append removes index 0, which is what keeps the window at 500.</summary>
    private readonly List<GitCommandEntry> _entries = [];
    /// <inheritdoc/>
    public event Action<GitCommandEntry?>? Changed;
    /// <summary>The git binary the application resolved, so the log follows a custom path.</summary>
    /// <remarks>Set from settings when they change. A command whose executable matches this full path is logged even when the program is not
    /// named <c>git</c>, which is what keeps a renamed or symlinked binary visible in diagnostics; the comparison is case-insensitive because
    /// Windows and default macOS file systems are. Null falls back to recognizing the conventional names only.</remarks>
    public string? GitExecutablePath { get; set; }
    /// <summary>Expands a path for comparison, falling back to the input when Git's path is not a valid file-system path.</summary>
    /// <param name="path">Configured or requested executable path.</param>
    /// <returns>The absolute path, or the original text when expansion throws.</returns>
    /// <remarks>Diagnostics must never break a process run, so an invalid path is compared verbatim instead of letting an exception escape
    /// from the logging path before the child is even started.</remarks>
    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); } catch (Exception) { return path; }
    }
    /// <inheritdoc/>
    /// <remarks>The copy is taken under the lock, so a later append or update cannot change the returned list.</remarks>
    public IReadOnlyList<GitCommandEntry> Snapshot()
    { lock (_sync) return _entries.ToArray(); }
    /// <inheritdoc/>
    /// <remarks>The list is cleared under the lock, then the null notification is raised outside it so a subscriber that snapshots the log sees
    /// the cleared state rather than blocking the clearing thread.</remarks>
    public void Clear() { lock (_sync) _entries.Clear(); Changed?.Invoke(null); }
    /// <summary>Decides whether a request is a Git invocation worth logging.</summary>
    /// <param name="request">The request about to be run.</param>
    /// <returns>True for the configured Git binary or for a program named <c>git</c>/<c>git.exe</c>.</returns>
    /// <remarks>The configured path is compared in full and case-insensitively, which is what logs a custom or renamed binary; the name
    /// fallback keeps logging working before settings are loaded. Requests that are not Git are passed through untouched and create no entry.</remarks>
    internal bool IsGit(ProcessRequest request)
    {
        var name = Path.GetFileName(request.Executable);
        var configured = GitExecutablePath is { Length: > 0 } path && string.Equals(SafeFullPath(path), SafeFullPath(request.Executable), StringComparison.OrdinalIgnoreCase);
        return configured || name.Equals("git", StringComparison.OrdinalIgnoreCase) || name.Equals("git.exe", StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>Appends a running entry for a Git command and returns its id, or null when the request is not Git.</summary>
    /// <param name="request">The request about to run; its arguments are rendered into the displayed command line.</param>
    /// <param name="started">Start timestamp from the caller's clock, kept with its offset for local display.</param>
    /// <returns>The entry id that <see cref="Finish"/> must update, or null when nothing was logged.</returns>
    /// <remarks>The command is rendered as <c>git</c> followed by its arguments, quoting only arguments that contain whitespace. That text
    /// is display text, not a shell command line: the process is always started with arguments already separated. Credentials in a URL are
    /// redacted before the entry is stored, which is why the unredacted request is never kept. Appending the 501st entry removes the oldest
    /// one, so the log holds at most 500 commands; why 500 was chosen is not recorded. The notification is raised after the lock is released
    /// and carries the new running entry.</remarks>
    internal Guid? Begin(ProcessRequest request, DateTimeOffset started)
    {
        if (!IsGit(request)) return null;
        return Begin("git", request.Arguments, request.WorkingDirectory ?? "", started);
    }

    internal Guid BeginGitHub(IReadOnlyList<string> arguments, DateTimeOffset started) => Begin("gh", arguments, "", started);

    private Guid Begin(string tool, IReadOnlyList<string> arguments, string workingDirectory, DateTimeOffset started)
    {
        var command = tool + " " + string.Join(' ', arguments.Select(Quote));
        var entry = new GitCommandEntry(Guid.NewGuid(), started, workingDirectory, Redact(command), TimeSpan.Zero, null, "", true)
        { IsGitHub = tool == "gh" };
        lock (_sync) { _entries.Add(entry); if (_entries.Count > 500) _entries.RemoveAt(0); }
        Changed?.Invoke(entry);
        return entry.Id;
    }
    /// <summary>Replaces a running entry with its duration, exit code and redacted detail text.</summary>
    /// <param name="id">Id returned by <see cref="Begin"/>; null when the command was not Git and must not create an entry.</param>
    /// <param name="elapsed">Measured wall-clock duration from start to exit.</param>
    /// <param name="exitCode">Process exit code, or null when the process could not start, was killed or timed out.</param>
    /// <param name="detail">Diagnostic text, normally standard error on completion or the start failure message.</param>
    /// <remarks>An id that is no longer in the list is ignored rather than appended: a long-running command can have been evicted by 500 later
    /// commands, and resurrecting it would put a stale invocation at the end of the window. Detail is cut to its first 2,048 characters with an
    /// ellipsis appended after redaction, so a chatty command cannot grow the log without bound. The notification carries the
    /// finished entry and is raised outside the lock.</remarks>
    internal void Finish(Guid? id, TimeSpan elapsed, int? exitCode, string detail, bool failed = false)
    {
        if (id is null) return;
        var safe = Redact(detail);
        GitCommandEntry entry;
        lock (_sync)
        {
            var index = _entries.FindIndex(item => item.Id == id);
            if (index < 0) return;
            entry = _entries[index] with
            {
                Duration = elapsed,
                ExitCode = exitCode,
                Detail = safe.Length > 2048 ? safe[..2048] + "…" : safe,
                IsRunning = false,
                IsFailure = failed || exitCode != 0
            };
            _entries[index] = entry;
        }
        Changed?.Invoke(entry);
    }
    /// <summary>Wraps an argument in quotes when it contains whitespace, escaping embedded quotes, for the displayed command line.</summary>
    /// <param name="value">One argument exactly as the request supplied it.</param>
    /// <returns>The argument, quoted and escaped only when it contains whitespace.</returns>
    /// <remarks>This exists so the diagnostics row reads like a command line; it is never parsed back. Escaping a quote does not make a Windows
    /// or POSIX shell round-trip, and it does not need to: execution passes the arguments as separate values and never goes through a shell.</remarks>
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
    /// <summary>Removes HTTP user information and credential formats GitHub can echo in diagnostics.</summary>
    internal static string Redact(string value)
    {
        value = Regex.Replace(value, @"(?i)(https?://)[^\s/@]+@", "$1[credentials]@");
        value = Regex.Replace(value, @"(?i)(\b(?:authorization\s*[:=]\s*(?:bearer|basic)|bearer)\s+)[a-z0-9._~+/=-]+", "$1[credentials]");
        return Regex.Replace(value, @"(?i)\b(?:gh[pousr]_|github_pat_)[a-z0-9_]+", "[credentials]");
    }
}
