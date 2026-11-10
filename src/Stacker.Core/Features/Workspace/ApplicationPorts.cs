using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Application-owned documents kept outside the user's repository, addressed by kind and identity.</summary>
/// <remarks>The profile implementation maps <c>kind</c> to a folder under the application data root and hashes
/// <c>identity</c> into a name, so an identity only has to be stable and unique, not a valid path. Reads never throw
/// for missing or damaged data: an absent document is a successful <see langword="null"/>, a damaged one is
/// quarantined and returned as a failure that is also reported through <see cref="LastProblem"/>. Writes replace the
/// whole document, and this port has no expected-revision check, so a caller that needs optimistic concurrency
/// carries its own marker (as <see cref="StackViewState.Revision"/> does for workspace stack views).</remarks>
public interface IApplicationState
{
    /// <summary>Absolute root of the application data profile that every <c>kind</c> folder lives under.</summary>
    /// <remarks>For callers that need one shared location rather than a single document: generated demo repositories
    /// are created beneath it and the profile backup walks it. The transient implementation reports
    /// <c>"transient"</c>, which is relative, so it is only meaningful to a host that never persists anything.</remarks>
    string Root { get; }
    /// <summary>The most recent read or write problem, kept so the UI can report it after the failing call returned.</summary>
    /// <remarks>Advisory only; the failure itself is also returned by the call that saw it. Set when a document was
    /// damaged or a write failed, and when the legacy profile import could not complete. It is not cleared on
    /// success, so it describes the last problem rather than a live state.</remarks>
    string? LastProblem { get; }
    /// <summary>Reads one document, or reports no document when nothing has been stored under this identity.</summary>
    /// <typeparam name="T">Reference type of the stored document; a value type could not express "absent".</typeparam>
    /// <param name="kind">Document family, which the profile implementation turns into a folder name.</param>
    /// <param name="identity">Stable logical key of the document within its family.</param>
    /// <param name="ct">Cancels the read where the implementation can honor it.</param>
    /// <returns>The document, <see langword="null"/> when it does not exist, or a problem when it exists but could
    /// not be read or parsed.</returns>
    /// <remarks>A damaged document is moved aside by the profile implementation and reported with the corrupt-store
    /// code, so a caller can fall back to defaults instead of being locked out of its repository.</remarks>
    Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class;
    /// <summary>Replaces one document and returns the outcome instead of throwing.</summary>
    /// <typeparam name="T">Reference type of the stored document.</typeparam>
    /// <param name="kind">Document family, which the profile implementation turns into a folder name.</param>
    /// <param name="identity">Stable logical key of the document within its family.</param>
    /// <param name="value">The complete document to persist; nothing is merged with the previous content.</param>
    /// <param name="ct">Cancels the write before it replaces the previous document.</param>
    /// <returns><see cref="Unit"/> once the replacement landed, or a problem whose message is ready to display.</returns>
    /// <remarks>Writes are serialized by the profile implementation, so queued saves replace each other in order
    /// rather than interleaving. A reader initiated at the same time can still observe either the old or the new
    /// document, never a partially written one.</remarks>
    Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class;
}
/// <summary>The Git binary Stacker should run, as a configured path or as a resolved one.</summary>
/// <remarks>Resolution searches PATH and the usual installation locations, so the implementation caches the answer
/// instead of repeating the search for every command. Assigning <see cref="Override"/> is the invalidation signal:
/// the next <see cref="Resolve"/> call resolves again.</remarks>
public interface IGitToolSettings
{
    /// <summary>Executable path from settings, or <see langword="null"/> to search PATH and the common install
    /// locations.</summary>
    /// <remarks>Assigning clears any cached resolution, so an edited setting takes effect without a restart.</remarks>
    string? Override { get; set; }
    /// <summary>Returns the executable to use now, resolving and remembering it on first use.</summary>
    /// <returns>A full path when a candidate was found, otherwise the bare program name.</returns>
    /// <remarks>Returning the bare name instead of an empty value keeps one behaviour at the boundary: the process
    /// fails to start and the failure names the missing tool, rather than the caller silently running whatever a
    /// later PATH lookup happens to find.</remarks>
    string Resolve();
}
/// <summary>A settings store that can also report the problem from its most recent load or save.</summary>
/// <remarks>Implemented beside the settings port rather than inside it, so a caller that only reads settings stays
/// unaffected. The navigation controller checks it after loading settings and surfaces the message once.</remarks>
public interface ISettingsDiagnostics
{
    /// <summary>The most recent settings problem, or <see langword="null"/> when none was recorded.</summary>
    /// <remarks>Sticky: it stays set until a later failing load or save replaces it. It is set when a damaged
    /// document was quarantined and defaults were used, and when a write failed. An unsupported future schema is
    /// reported by the load itself and blocks writes, so that case does not appear here.</remarks>
    string? LastProblem { get; }
}
/// <summary>The GitHub CLI binary and the credential source the CLI should use.</summary>
/// <remarks>Settings are pushed into the adapter once after they load, so a changed path or credential choice
/// applies to every later command without rebuilding the adapter.</remarks>
public interface IGhToolSettings
{
    /// <summary>Executable path from settings, or <see langword="null"/> to look on PATH.</summary>
    string? Override { get; set; }
    /// <summary>Whether inherited GitHub token variables are removed from the child environment so the CLI resolves
    /// the account the user saved with its own login.</summary>
    /// <remarks>Without this, a token exported for another tool would override the saved account and reads or writes
    /// could reach a different identity than the one the user selected. Both the public and enterprise token pairs
    /// are removed when this is true.</remarks>
    bool UseSavedCredentials { get; set; }
}
/// <summary>Creates a fresh offline demo repository so the workspace can be explored without a network account.</summary>
public interface IDemoRepository
{
    /// <summary>Builds a new demo repository and returns the root that should be opened.</summary>
    /// <param name="ct">Cancels generation, which removes the partially built demo before the failure propagates.</param>
    /// <returns>The root of the created repository.</returns>
    /// <remarks>Each call builds a uniquely named repository, so reopening the demo never overwrites work in a
    /// previous one. Older generated demos are pruned so repeated use cannot grow without bound: the new demo plus
    /// the two most recent earlier ones are kept. The demo is disposable, not a user repository.</remarks>
    Task<Result<string>> CreateAsync(CancellationToken ct = default);
}
/// <summary>Supplies the recorded pull-request snapshot of an offline demo repository.</summary>
public interface IDemoSnapshotReader
{
    /// <summary>Reads the offline snapshot stored inside a demo repository.</summary>
    /// <param name="root">Root of the repository being opened.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The recorded snapshot, or <see langword="null"/> when this reader cannot supply demo data at all.</returns>
    /// <remarks>Used instead of a live GitHub read while a demo is open. A demo repository whose snapshot document is
    /// missing is a failure rather than an empty result, because the demo cannot work without it; the caller reports
    /// the missing snapshot instead of showing an empty PR list.</remarks>
    Task<Result<GitHubSnapshot?>> ReadDemoSnapshotAsync(string root, CancellationToken ct = default);
}
/// <summary>Bounded, thread-safe log of Git invocations behind the diagnostics tab.</summary>
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
    string? GitExecutablePath { get; set; }
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

/// <summary>One Git invocation as the diagnostics tab shows it: where it ran, how long it took and how it ended.</summary>
/// <remarks>Created when the process starts, while it is still running and has no exit code, and replaced on
/// completion, which is why <see cref="Id"/> is the stable handle across both notifications. The adapter redacts
/// embedded credentials before the entry is created, so a username and password in a remote URL never appear in the
/// log or in the diagnostics export.</remarks>
/// <param name="Id">Identity of this invocation, used to update the same row instead of appending a second one.</param>
/// <param name="StartedAt">When the process was started, with its offset preserved for local display.</param>
/// <param name="WorkingDirectory">The explicit working directory the command ran in; empty when none was set.</param>
/// <param name="Command">The rendered command line, prefixed with <c>git</c>, with arguments quoted and credentials
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
    /// <summary>One-line rendering for the log list and the clipboard export: time, duration, outcome and command.</summary>
    /// <remarks>The duration is formatted with one decimal place, so a sub-second command still shows a value
    /// (for example <c>0.4s</c>). While the command runs, the duration column reads <c>running</c> and the outcome
    /// column is empty, which leaves its two spaces behind. A finished entry without an exit code reads
    /// <c>failed</c>, because a process that never produced a code did not complete normally. Columns are separated
    /// by two spaces so the text stays parseable when copied out.</remarks>
    public string Summary => $"{StartedAt.LocalDateTime:HH:mm:ss}  {(IsRunning ? "running" : Duration.TotalSeconds.ToString("0.0") + "s")}  {(IsRunning ? "" : ExitCode is { } code ? $"exit {code}" : "failed")}  {Command}";
}

/// <summary>Explicit transient state for previews/tests without an OS-owned application store.</summary>
/// <remarks>Documents are held as JSON text rather than as objects, so a value is never shared between the writer
/// and the reader and members excluded from serialization do not survive the round-trip, exactly as with the profile
/// implementation. Nothing is persisted and nothing is shared between instances: the same instance must be handed to
/// every feature that should see the same state, and its lifetime is the process. Used as the fallback when a host
/// supplies no store, which keeps a preview or a test from writing into the real user profile.</remarks>
public sealed class MemoryApplicationState : IApplicationState
{
    /// <summary>Serialized documents keyed by (kind, identity); tuple equality is ordinal on both strings.</summary>
    private readonly Dictionary<(string, string), string> _values = [];
    /// <summary>A synthetic root. The value is relative, so a caller that combines it into a path resolves it
    /// against the process working directory; hosts that persist anything use the profile implementation.</summary>
    public string Root => "transient";
    /// <summary>Always <see langword="null"/>: nothing here can be damaged or fail to write, so there is no
    /// diagnostic to report.</summary>
    public string? LastProblem => null;
    /// <summary>Deserializes the stored document, or reports no document for an identity that was never written.</summary>
    /// <typeparam name="T">Reference type of the stored document.</typeparam>
    /// <param name="kind">Document family.</param>
    /// <param name="identity">Logical key within the family.</param>
    /// <param name="ct">Ignored; there is no I/O to cancel.</param>
    /// <returns>The deserialized document, and always a success: this store has no damaged data to report.</returns>
    public Task<Result<T?>> ReadAsync<T>(string kind, string identity, CancellationToken ct = default) where T : class =>
        Task.FromResult(Result<T?>.Ok(_values.TryGetValue((kind, identity), out var json) ? System.Text.Json.JsonSerializer.Deserialize<T>(json) : null));
    /// <summary>Serializes the document into the in-memory table, replacing any previous value for the identity.</summary>
    /// <typeparam name="T">Reference type of the stored document.</typeparam>
    /// <param name="kind">Document family.</param>
    /// <param name="identity">Logical key within the family.</param>
    /// <param name="value">The complete document to keep.</param>
    /// <param name="ct">Ignored; there is no I/O to cancel.</param>
    /// <returns>Always <see cref="Result.Ok"/>, since no write can fail here.</returns>
    public Task<Result<Unit>> WriteAsync<T>(string kind, string identity, T value, CancellationToken ct = default) where T : class
    { _values[(kind, identity)] = System.Text.Json.JsonSerializer.Serialize(value); return Task.FromResult(Result.Ok); }
}
