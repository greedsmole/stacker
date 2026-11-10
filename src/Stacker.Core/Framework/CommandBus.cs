namespace Stacker.Core.Framework;

/// <summary>One foreground mutation at a time. Reads and ordered draft persistence remain independent.</summary>
/// <param name="runner">Executes the admitted command and owns its durable journal entry.</param>
/// <param name="diagnostics">Optional sink that records one event per reported outcome.</param>
/// <remarks>The single permit of <see cref="_gate"/> is the whole concurrency policy: a second caller is refused
/// immediately rather than queued, and local repository commands and remote publication share the same gate, so a
/// GitHub send blocks a local commit. Admission is decided under <see cref="_sync"/>, so a concurrent
/// <see cref="PauseAsync"/> or <see cref="StopAsync"/> cannot race a command into starting. Read-only work such as
/// history, patches, drafts and settings uses its ports directly and never passes through the bus.</remarks>
public sealed class CommandBus(CommandRunner runner, IDiagnosticSink? diagnostics = null)
{
    /// <summary>The admission gate: the single permit is held for exactly as long as one foreground command runs.</summary>
    /// <remarks>Acquisition is a non-blocking <c>Wait(0)</c> attempt, so a refusal costs no waiting. The permit is
    /// released in one <c>finally</c> after <see cref="_active"/> is cleared, which keeps <see cref="IsBusy"/> and the
    /// quiesce handshake consistent with the command that actually ran.</remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Guards the two refusal flags and the active cancellation source as one admission decision.</summary>
    /// <remarks>The semaphore cannot be held across an await while admission is decided, so the lock orders the
    /// stopping/paused check, the permit attempt and the assignment of <see cref="_active"/> atomically. Nothing
    /// inside the lock awaits.</remarks>
    private readonly object _sync = new();
    /// <summary>Set once by <see cref="StopAsync"/>; from then on every command is refused and
    /// <see cref="Resume"/> can no longer lift a pause.</summary>
    private bool _stopping;
    /// <summary>Set by <see cref="PauseAsync"/> and cleared by <see cref="Resume"/>; refuses new commands while the
    /// process keeps running.</summary>
    private bool _paused;
    /// <summary>Linked cancellation source of the command holding the permit, or null between commands.</summary>
    /// <remarks>Pause and stop cancel this source, and the command runs on the same token, so cancellation reaches
    /// adapter work and journal writes instead of only marking the bus as stopping.</remarks>
    private CancellationTokenSource? _active;
    /// <summary>Process-wide shutdown signal, linked into every admitted command's token.</summary>
    /// <remarks><see cref="StopAsync"/> cancels it once and never resets it, so stopping stays permanent even if a
    /// command were somehow admitted later.</remarks>
    private readonly CancellationTokenSource _shutdown = new();
    /// <summary>Whether a command currently holds the permit, for UI gating.</summary>
    /// <remarks>A lock-free snapshot of the semaphore count, so it is a hint rather than a guarantee: it does not
    /// report a paused or stopping bus, and a caller must still handle a refusal.</remarks>
    public bool IsBusy => _gate.CurrentCount == 0;
    /// <summary>Raised for every outcome, including a refusal that never executed a command.</summary>
    /// <remarks>Raised after the diagnostic event is written, from the continuation that ran the command. The shell
    /// uses it to prepend the outcome to the operation list and set its status and error text.</remarks>
    public event Action<CommandOutcome>? Reported;
    /// <summary>Raised after admission succeeds and immediately before the runner starts the command.</summary>
    /// <remarks>The shell cancels stale diff, comparison and GitHub reads here, so work started before the mutation
    /// cannot repaint from a now-untrue snapshot. It fires before the journal entry is opened. A subscriber must not
    /// throw: the permit is still released, but no outcome is reported for that run.</remarks>
    public event Action? Starting;

    /// <summary>Runs a local repository command under the single-command gate.</summary>
    /// <param name="repository">The repository whose canonical root is used as the journal scope.</param>
    /// <param name="command">The mutation to plan, execute and close.</param>
    /// <param name="progress">Optional per-step progress messages for the caller's UI.</param>
    /// <param name="ct">The caller's cancellation token, linked with the bus shutdown token.</param>
    /// <returns>The reported outcome: applied, refused, failed, cancelled, partially applied, rolled back or needing
    /// the user. A busy, paused or stopping bus returns <see cref="CommandStatus.Refused"/> instead of throwing.</returns>
    /// <remarks>The journal scope is <see cref="RepoRoot.Value"/>, the same canonical root the journal and
    /// <see cref="Recent"/> use. A failure returned or thrown by the command is converted into the outcome by the
    /// runner; this method does not rethrow it.</remarks>
    public async Task<CommandOutcome> RunAsync(RepoRoot repository, IStackCommand command,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => await ExecuteAsync(repository.Value, command, progress, ct);

    /// <summary>Runs a remote publication command under the same single-command gate.</summary>
    /// <param name="context">The GitHub identity that owns the journal scope.</param>
    /// <param name="command">The remote mutation to plan, execute and close.</param>
    /// <param name="ct">The caller's cancellation token, linked with the bus shutdown token.</param>
    /// <returns>The reported outcome, with the same status vocabulary as <see cref="RunAsync"/>.</returns>
    /// <remarks>No progress is accepted because a remote send has no per-step narration. The journal scope is
    /// <c>"github:" + context.Identity</c> (host, repository id and account), so remote entries survive without a
    /// local checkout and cannot collide with a local repository scope. Publication is never retried here: an
    /// uncertain send stays in the journal for reconciliation.</remarks>
    public Task<CommandOutcome> RunRemoteAsync(GitHubRepositoryContext context, IStackCommand command, CancellationToken ct = default)
        => ExecuteAsync("github:" + context.Identity, command, null, ct);

    /// <summary>Reads the journal entries recorded for a scope, newest first.</summary>
    /// <param name="scope">A repository root or a <c>github:</c> identity, as passed to the run methods.</param>
    /// <returns>At most the journal's default page of 20 entries, or a failed result when the journal cannot be
    /// read.</returns>
    /// <remarks>This is read-only history for the operations list; it does not touch the gate, so a command may be
    /// running while the list is built.</remarks>
    public Result<IReadOnlyList<JournalEntry>> Recent(string scope) => runner.Recent(scope);
    /// <summary>Admits one command, reports its outcome and always frees the permit.</summary>
    /// <param name="scope">The journal scope for the command.</param>
    /// <param name="command">The mutation to execute.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The outcome, stamped with the scope and operation id.</returns>
    /// <remarks>Elapsed time is measured with <c>TimeProvider.System</c>, the clock the other diagnostic events also
    /// use. The ambient operation correlation is adopted when present (a prepared publication supplies its own id)
    /// and otherwise a new 32-character id is created; the same id becomes the journal entry id and is copied onto
    /// the reported outcome. Admission happens under <see cref="_sync"/>: a refusal reports immediately with certainty
    /// <see cref="EffectCertainty.NotStarted"/> and never raises <see cref="Starting"/> or opens an entry. Both a pause
    /// and a stop refuse with the same "stopping operations" message. The admitted path links the caller token with
    /// <see cref="_shutdown"/> and disposes the link on the way out, even when the runner throws.</remarks>
    private async Task<CommandOutcome> ExecuteAsync(string scope, IStackCommand command, IProgress<string>? progress, CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        using var correlation = OperationCorrelation.Enter(OperationCorrelation.Current ?? Guid.NewGuid().ToString("N"));
        CancellationTokenSource? linked = null; string? refusal = null;
        lock (_sync)
        {
            if (_stopping || _paused) refusal = "The workspace is stopping operations.";
            else if (!_gate.Wait(0)) refusal = "An operation is already running.";
            else _active = linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        }
        if (refusal is not null) return Report(CommandOutcome.Refused(refusal) with { Scope = scope });
        using (linked)
        {
            try { Starting?.Invoke(); return Report((await runner.RunAsync(scope, command, progress, linked!.Token)) with { Scope = scope }, TimeProvider.System.GetElapsedTime(started).TotalMilliseconds); }
            finally { lock (_sync) { _active = null; _gate.Release(); } }
        }
    }

    /// <summary>Stamps the operation id, records the diagnostic event and raises <see cref="Reported"/>.</summary>
    /// <param name="outcome">The outcome about to be returned to the caller.</param>
    /// <param name="durationMs">Wall-clock duration, or zero for a refusal that ran nothing.</param>
    /// <returns>The same outcome, after stamping and reporting.</returns>
    /// <remarks>The diagnostic stage is the status name; the problem code is recorded but never the message, keeping
    /// the exported metadata log free of user text.</remarks>
    private CommandOutcome Report(CommandOutcome outcome, double durationMs = 0)
    {
        outcome = outcome with { OperationId = OperationCorrelation.Current };
        diagnostics?.Record(new(outcome.OperationId ?? "", outcome.Status.ToString(), outcome.Problem?.Code, durationMs));
        Reported?.Invoke(outcome);
        return outcome;
    }

    /// <summary>Temporarily refuses new commands and cancels the running one.</summary>
    /// <param name="timeout">Budget shared by the cancellation acknowledgement and the wait for the permit.</param>
    /// <returns>True when no command holds the permit any more; false when the budget expired first.</returns>
    /// <remarks>Used while the shell decides whether a close is allowed: a successful pause makes the workspace
    /// quiescent so drafts and settings can be persisted. The paused flag is set before waiting, so a false result
    /// still refuses new commands until <see cref="Resume"/>; the running command is cancelled, but the shutdown
    /// signal is not used, so the process can continue after a cancelled close.</remarks>
    public Task<bool> PauseAsync(TimeSpan timeout) => QuiesceAsync(timeout, false);
    /// <summary>Allows commands again after a pause, unless shutdown has already begun.</summary>
    /// <remarks>Does nothing when <see cref="StopAsync"/> has run, which is what makes stopping permanent. It only
    /// clears the refusal flag; the command cancelled by the pause has already received its cancellation.</remarks>
    public void Resume() { lock (_sync) { if (!_stopping) _paused = false; } }
    /// <summary>Permanently refuses new commands and cancels the running one through the shutdown signal.</summary>
    /// <param name="timeout">Budget shared by the cancellation acknowledgement and the wait for the permit.</param>
    /// <returns>True when the bus is quiescent; false when the budget expired first.</returns>
    /// <remarks>Irreversible for this bus instance: the flag stays set and the shutdown source stays cancelled. The
    /// shutdown path gives it five seconds; a false result becomes a "still stopping, journal checked on restart"
    /// warning and persistence continues without the guarantee. After a true result no command is between its first
    /// and last effect, so workspace and journal state can be persisted safely.</remarks>
    public Task<bool> StopAsync(TimeSpan timeout) => QuiesceAsync(timeout, true);
    /// <summary>Sets the refusal state, cancels what must stop, then waits for the permit to be free.</summary>
    /// <param name="timeout">Total budget shared by the cancellation wait and the drain wait.</param>
    /// <param name="permanent">True for stop: cancel the shared shutdown source and keep refusals on. False for pause:
    /// cancel only the current command.</param>
    /// <returns>True when the permit was observed free within the budget; false otherwise.</returns>
    /// <remarks>The two phases share one budget: after the cancellation task is awaited, the remaining time is
    /// recomputed and used to acquire the permit. Acquiring and immediately releasing it proves no command is still
    /// between its first and last effect; the refusal flags set under the lock keep a new one out. A command that
    /// ignores cancellation makes the wait time out, and a cancelled command still runs its own compensation before
    /// releasing the permit, so a slow undo also consumes this budget and yields false. That is why the caller treats
    /// a false result as a warning rather than blocking shutdown.</remarks>
    private async Task<bool> QuiesceAsync(TimeSpan timeout, bool permanent)
    {
        var started = TimeProvider.System.GetTimestamp(); Task cancellation;
        lock (_sync)
        {
            _paused = true; if (permanent) _stopping = true;
            cancellation = permanent ? _shutdown.CancelAsync() : _active?.CancelAsync() ?? Task.CompletedTask;
        }
        try { await cancellation.WaitAsync(timeout); } catch (TimeoutException) { return false; }
        var remaining = timeout - TimeProvider.System.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero || !await _gate.WaitAsync(remaining)) return false;
        _gate.Release(); return true;
    }
}
