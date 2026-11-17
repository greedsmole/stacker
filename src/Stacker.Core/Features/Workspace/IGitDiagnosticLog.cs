using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Bounded, thread-safe log of Git and GitHub CLI invocations behind the diagnostics tab.</summary>
/// <remarks>The process runner appends an entry when a command starts and replaces it when it finishes; the UI
/// mirrors the same notifications into its own list bounded the same way. It is a rolling window for the current
/// session: the implementation keeps the newest 500 entries and drops the oldest when a further command arrives, so it
/// is diagnostic evidence, not an audit trail.</remarks>
public interface IGitDiagnosticLog
{
    /// <summary>The resolved Git binary, so a command using a configured custom path is still recognized as Git and
    /// logged.</summary>
    /// <remarks>Set from settings whenever they change; a command run through a path that matches it is logged even
    /// though the program is not named <c>git</c>.</remarks>
    string? GitExecutablePath
    {
        get; set;
    }
    /// <summary>Raised when an entry starts or is updated, and with <see langword="null"/> when the log is cleared.</summary>
    /// <remarks>Two notifications describe one invocation: the running entry and its finished replacement, so a
    /// consumer matches on <see cref="GitCommandEntry.Id"/> instead of appending blindly. Raised on the thread that
    /// ran the command, so a UI consumer marshals to its own dispatcher.</remarks>
    event Action<GitCommandEntry?>? Changed;
    /// <summary>Takes a stable copy of the entries, oldest first.</summary>
    /// <returns>A snapshot that is unaffected by later appends, for the initial population of the UI list or for a
    /// clipboard export.</returns>
    IReadOnlyList<GitCommandEntry> Snapshot();
    /// <summary>Drops every entry and raises <see cref="Changed"/> with <see langword="null"/>.</summary>
    void Clear();
}
