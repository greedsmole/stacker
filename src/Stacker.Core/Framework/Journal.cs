using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>How far one journaled effect got.</summary>
/// <remarks>The journal records an effect before it runs and its outcome as soon as that outcome is known,
/// because the stored entry is the only evidence recovery has after a crash. <see cref="StepState.Done"/>,
/// <see cref="StepState.Started"/> and <see cref="StepState.Unknown"/> may all describe something that took effect;
/// <see cref="StepState.Planned"/> and <see cref="StepState.Failed"/> prove that nothing moved. The numeric values are
/// part of the stored format (the journal is serialized JSON with the default enum encoding), so members must not be
/// reordered or renumbered without a schema migration. Single-effect commands let <c>EffectCommand</c> advance these
/// states; multi-step adapters (restack, push) record each step themselves.</remarks>
public enum StepState
{
    /// <summary>Written by <see cref="IJournal.OpenAsync"/> from the plan, before any effect runs.</summary>
    Planned,
    /// <summary>Recorded after the effect returned success; the replaced and resulting commits are then known.</summary>
    Done,
    /// <summary>Recorded when the effect failed in a way that proves nothing changed, so it is safe to abandon.</summary>
    Failed,
    /// <summary>Recorded immediately before an effect whose completion cannot be observed atomically, so a
    /// process death between the effect and its record still leaves evidence that something may have happened.</summary>
    Started,
    /// <summary>Recorded when the effect was dispatched but its result was lost, so it may or may not have
    /// happened; recovery inspects instead of retrying.</summary>
    Unknown
}

/// <summary>One effect inside a command: a branch move, a stash, a commit, a remote write.</summary>
/// <remarks>The pair <paramref name="OldSha"/> and <paramref name="NewSha"/> is the recovery evidence: the old
/// commit is what an undo restores, and the new commit proves the ref has not moved since. Steps are immutable;
/// <see cref="Finished"/> produces the completed copy that <see cref="IJournal.StepAsync"/> stores.</remarks>
/// <param name="Id">Stable identifier inside the entry. Updates replace the step with the same id, so it must not
/// change between planning and completion.</param>
/// <param name="Kind">Effect category used for filtering and display: <c>rebase</c>, <c>push</c>, <c>original</c>,
/// or the command kind for a single-effect command. Recovery only restores <c>rebase</c> steps.</param>
/// <param name="Target">The ref or remote identifier the effect changes; shown to the user in an outcome's
/// effects list.</param>
/// <param name="OldSha">The commit to restore on undo, when known; <see langword="null"/> when the effect does not
/// replace a ref. Recovery also compares the current ref against it to avoid discarding newer work.</param>
/// <param name="NewSha">The commit the effect produced, when known; undo refuses to move a ref that no longer
/// points here.</param>
/// <param name="State">Last observed state; see <see cref="StepState"/> for who records each transition.</param>
public sealed record JournalStep(string Id, string Kind, string Target, string? OldSha, string? NewSha, StepState State)
{
    /// <summary>The approved restack plan, attached to the synthetic <c>original</c> step; the journal lifts it onto
    /// the entry so Continue can resume the remaining layers after a restart without replanning against a changed
    /// repository.</summary>
    public RestackPlan? RestackPlan { get; init; }
    /// <summary>Creates a step in <see cref="StepState.Planned"/> with no observed commits, which is the only state
    /// a plan may hand to the journal.</summary>
    /// <param name="id">Stable identifier inside the entry.</param>
    /// <param name="kind">Effect category, such as <c>rebase</c> or <c>push</c>.</param>
    /// <param name="target">The ref or remote identifier the effect will change.</param>
    /// <returns>A planned step with both commit fields empty.</returns>
    public static JournalStep Planned(string id, string kind, string target) => new(id, kind, target, null, null, StepState.Planned);
    /// <summary>Returns a copy carrying the observed commits and terminal state; the planned id, kind and target
    /// are preserved so recovery can match the step to the effect it describes.</summary>
    /// <param name="oldSha">The commit the effect replaced, or <see langword="null"/> when none applies.</param>
    /// <param name="newSha">The commit the effect produced, or <see langword="null"/> when unknown.</param>
    /// <param name="state">The state to record, normally <see cref="StepState.Done"/> or
    /// <see cref="StepState.Failed"/>.</param>
    /// <returns>A completed copy of this step.</returns>
    public JournalStep Finished(string? oldSha, string? newSha, StepState state) => this with { OldSha = oldSha, NewSha = newSha, State = state };
}

/// <summary>How a command ended; <see cref="JournalPhase.Running"/> means it never closed and needs recovery.</summary>
/// <remarks><see cref="JournalPhase.Running"/> is written by <see cref="IJournal.OpenAsync"/> and every other phase
/// by <see cref="IJournal.CloseAsync"/>. The command runner closes normal outcomes, recovery closes after restoring
/// or dismissing, and the remote journal maps a confirmed publication to <see cref="JournalPhase.Applied"/>. Only
/// <see cref="JournalPhase.Running"/> and <see cref="JournalPhase.NeedsUser"/> require the user's attention;
/// <see cref="JournalPhase.Running"/>, <see cref="JournalPhase.Applied"/> and
/// <see cref="JournalPhase.NeedsUser"/> are the phases that still offer an undo. The numeric values are part of the
/// stored format, like <see cref="StepState"/>, so the order is fixed.</remarks>
public enum JournalPhase
{
    /// <summary>Opened by <see cref="IJournal.OpenAsync"/> and not yet closed; an effect may be in flight. A later
    /// start that reads this phase must treat the command as interrupted.</summary>
    Running,
    /// <summary>Every planned effect completed and the command closed normally; a confirmed remote publication is
    /// mapped here as well. The entry can still be undone.</summary>
    Applied,
    /// <summary>The command stopped for a decision it cannot make itself: a conflict, a parent that moved outside
    /// the operation, or an effect whose outcome is unknown. Only an explicit resume, undo or dismiss changes it.</summary>
    NeedsUser,
    /// <summary>The recorded effects were reversed, either by the command's compensation in the runner or by
    /// recovery restoring every branch after a failed restack. Nothing further is offered.</summary>
    RolledBack,
    /// <summary>The user explicitly undid an earlier applied restack, restoring the commits recorded before it.
    /// Kept distinct from <see cref="JournalPhase.RolledBack"/>, which is automatic recovery rather than a deliberate
    /// undo.</summary>
    Undone,
    /// <summary>The record was closed without reversing anything: a command that failed before moving a ref, an
    /// explicit dismiss of effects that cannot be proven, or a publication GitHub did not confirm.</summary>
    Dismissed
}

/// <summary>What the user chose for a journal entry that still needs attention.</summary>
/// <remarks>The three answers a recovery prompt can offer: continue the plan, reverse what is recorded, or accept
/// the effects and close the record. The enum names the choices; each caller maps them to recovery operations.</remarks>
public enum RecoveryChoice
{
    /// <summary>Continue the remaining planned effects from the durable entry.</summary>
    Resume,
    /// <summary>Reverse the recorded effects and close the entry, either as rolled back or as undone.</summary>
    Undo,
    /// <summary>Close the entry without changing any ref; the effects stay as they are.</summary>
    Dismiss
}

/// <summary>Write-ahead record of one command; the only thing that makes an effect recoverable.</summary>
/// <remarks>One entry owns every step of a command and its terminal phase. It is persisted before the first
/// effect and rewritten after each step, so the stored copy is at least as strong as the command's own knowledge.
/// Records are immutable: an update produces a new instance with a later <see cref="UpdatedAt"/>, and the store
/// matches it by <see cref="Id"/>. A damaged or unsupported entry blocks recovery instead of being replaced with
/// an empty history.</remarks>
/// <param name="SchemaVersion">Shape of this entry; the JSON journal accepts 2 through
/// <see cref="CurrentSchema"/> and refuses anything else, so a newer entry is never silently truncated.</param>
/// <param name="Id">Stable operation identifier shared with diagnostics and, for remote work, with the persisted
/// publication payload.</param>
/// <param name="Repository">Scope key the entry belongs to (a local repository scope or a GitHub context identity);
/// it is also the partition used to find the entry again.</param>
/// <param name="Kind">Command kind that produced the entry, such as <c>restack</c>, <c>local.push</c> or
/// <c>github.comment</c>; used for filtering, recovery priority and the summary.</param>
/// <param name="StartedAt">Clock time when the entry was opened; also shown for saved stashes.</param>
/// <param name="UpdatedAt">Clock time of the last persisted change.</param>
/// <param name="Phase">How the command ended; see <see cref="JournalPhase"/>.</param>
/// <param name="Steps">Planned effects in execution order; an update replaces the step with the matching id.</param>
/// <param name="StashRef">Commit id of a stash attached to this entry, kept separate so undo and stash restore do
/// not compete.</param>
/// <param name="PendingStepId">Id of the step the command paused on, normally a conflicting rebase; used to select
/// the layer to resume or reverse.</param>
/// <param name="Message">Last user-facing message; <see cref="Summary"/> derives a line when it is
/// <see langword="null"/>.</param>
public sealed record JournalEntry(int SchemaVersion, string Id, string Repository, string Kind,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, JournalPhase Phase, IReadOnlyList<JournalStep> Steps,
    string? StashRef, string? PendingStepId, string? Message)
{
    /// <summary>The entry schema this build writes. The JSON journal also reads schema 2 and preserves the stored
    /// version when it updates an older entry.</summary>
    public const int CurrentSchema = 3;
    /// <summary>The approved restack plan, lifted from the synthetic <c>original</c> step when the entry is opened;
    /// it lets Continue resume the remaining layers without replanning against a changed repository.</summary>
    public RestackPlan? RestackPlan { get; init; }
    /// <summary>An entry the user must deal with: still running, or stopped for a decision.</summary>
    public bool NeedsAttention => Phase is JournalPhase.Running or JournalPhase.NeedsUser;
    /// <summary>Whether the effects recorded here can still be reversed.</summary>
    /// <remarks>Rolled-back, undone and dismissed entries are final; an applied entry stays undoable because a
    /// deliberate undo is how the user reverses a restack that finished successfully.</remarks>
    public bool CanUndo => Phase is JournalPhase.Running or JournalPhase.Applied or JournalPhase.NeedsUser;
    /// <summary>Completed effects that recorded the commit they replaced, which an undo can use as its candidate
    /// list.</summary>
    /// <remarks>Every completed step that recorded a previous commit, including remote pushes. Callers that rewrite
    /// refs narrow it further: the undo path keeps only <c>rebase</c> steps whose new commit is also known.</remarks>
    public IReadOnlyList<JournalStep> Moved => Steps.Where(step => step.State == StepState.Done && step.OldSha is not null).ToArray();
    /// <summary>
    /// Every intended effect whose original commit is known, including one whose record never landed.
    /// Recovery reverses these, which is what makes a crash between an effect and its record recoverable.
    /// </summary>
    /// <remarks>Includes <see cref="StepState.Done"/>, <see cref="StepState.Started"/> and
    /// <see cref="StepState.Unknown"/> because all three may have changed a ref. Only <c>rebase</c> steps qualify:
    /// the synthetic <c>original</c> step stores a branch name in the commit field, and remote writes cannot be
    /// restored automatically. Recovery still refuses a move unless the current ref equals the recorded new
    /// commit.</remarks>
    public IReadOnlyList<JournalStep> Restorable => Steps.Where(step => step.OldSha is not null && step.Kind == "rebase"
        && step.State is StepState.Done or StepState.Started or StepState.Unknown).ToArray();
    /// <summary>The step identified by <see cref="PendingStepId"/>, or <see langword="null"/> when the entry has no
    /// pending step or the id no longer matches one.</summary>
    public JournalStep? Pending => PendingStepId is null ? null : Steps.FirstOrDefault(step => step.Id == PendingStepId);
    /// <summary>One line for the operations list: the stored message, otherwise the command kind plus the number of
    /// moved effects when any moved.</summary>
    public string Summary => Message ?? (Moved.Count == 0 ? Kind : $"{Kind} · {Moved.Count} effect(s)");
}

/// <summary>A stash commit recorded by the journal so it can be listed and restored by its owning entry.</summary>
/// <param name="Sha">Full commit id of the stash; the restore path finds it in the stash list by this id rather
/// than by a positional index.</param>
/// <param name="EntryId">Id of the journal entry that owns the stash, so unrelated restacks never overwrite it.</param>
/// <param name="CreatedAt">When the owning entry started; used for display.</param>
/// <param name="Description">The owning entry's kind, shown as the stash description.</param>
public sealed record SavedStash(string Sha, string EntryId, DateTimeOffset CreatedAt, string Description)
{
    /// <summary>A display label combining the created time, the description and a seven-character commit prefix;
    /// it is shown to the user and never parsed.</summary>
    /// <remarks>The seven characters are the conventional Git abbreviation and exist only to keep the label short.
    /// The stored <see cref="Sha"/> is always complete, and the prefix falls back to the whole value when the id is
    /// shorter than seven characters.</remarks>
    public string Label => $"{CreatedAt:g} · {Description} · {Sha[..Math.Min(7, Sha.Length)]}";
}

/// <summary>Stores journal entries; implementations must write before the effect they describe.</summary>
/// <remarks>Every call returns a <see cref="Result{T}"/>; each write is durable when it succeeds:
/// <see cref="OpenAsync"/> returns only after the running entry is stored, and the update methods return only after
/// the change is stored, so the caller may begin the effect once a call succeeds. Updates are matched by entry id, and
/// implementations serialize writes. A damaged journal is preserved and reported, never replaced with an empty one. The
/// JSON journal keeps roughly 40 resolved entries plus every entry that still needs attention or holds a
/// stash.</remarks>
public interface IJournal
{
    /// <summary>Writes the entry in the <see cref="JournalPhase.Running"/> phase, before any effect runs.</summary>
    /// <remarks>A blocked plan must never reach this method; the runner returns a refusal before opening an entry.
    /// One unfinished <c>restack</c> per scope is enforced by the journal adapter.</remarks>
    /// <param name="repository">Scope key the entry belongs to.</param>
    /// <param name="kind">Command kind, copied to <see cref="JournalEntry.Kind"/>.</param>
    /// <param name="planned">Steps produced by the read-only plan, copied to
    /// <see cref="JournalEntry.Steps"/>.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>The stored entry in the running phase.</returns>
    Task<Result<JournalEntry>> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default);
    /// <summary>Records one finished effect; called as soon as the effect is known to have happened.</summary>
    /// <remarks>The step with the same id is replaced and the entry's update time advances. Call this before
    /// starting the next effect so a crash loses at most the in-flight step.</remarks>
    /// <param name="entry">The entry that owns the step.</param>
    /// <param name="step">The step copy carrying its new state and commits.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>The refreshed entry.</returns>
    Task<Result<JournalEntry>> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default);
    /// <summary>Closes the entry with its outcome and any pending decision.</summary>
    /// <remarks>A <see langword="null"/> stash reference keeps a stash already attached to the entry;
    /// <paramref name="pendingStepId"/> and <paramref name="message"/> are written as given, so a null message
    /// clears the stored one.</remarks>
    /// <param name="entry">The entry to close.</param>
    /// <param name="phase">The terminal (or needs-user) phase to record.</param>
    /// <param name="stashRef">A stash to attach, or <see langword="null"/> to keep the current one.</param>
    /// <param name="pendingStepId">The step a resume or undo should start from, or <see langword="null"/>.</param>
    /// <param name="message">User-facing outcome text, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>The closed entry.</returns>
    Task<Result<JournalEntry>> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default);
    /// <summary>Sets or clears the stash this entry is holding, without closing it.</summary>
    /// <remarks>Attaching a stash to the entry instead of opening a competing entry keeps undo and stash restore
    /// together; clearing it also removes the stash from the saved list.</remarks>
    /// <param name="entry">The entry that holds the stash.</param>
    /// <param name="stashRef">The stash commit to attach, or <see langword="null"/> to clear it.</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <returns>The updated entry.</returns>
    Task<Result<JournalEntry>> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default);
    /// <summary>The entry recovery should offer first: an unfinished restack, otherwise the newest entry that still
    /// needs attention, if any.</summary>
    /// <remarks>An unfinished <c>restack</c> is offered before any other kind; otherwise the newest entry that still
    /// needs attention is returned. When no entry needs attention, a saved stash is surfaced through its owning
    /// entry.</remarks>
    /// <param name="repository">Scope key to search.</param>
    /// <param name="ct">Cancellation for the read.</param>
    /// <returns>The entry to attend to, or <see langword="null"/> when nothing is outstanding.</returns>
    Task<Result<JournalEntry?>> ActiveAsync(string repository, CancellationToken ct = default);
    /// <summary>The newest restack entry that can still be undone and moved at least one branch, if any.</summary>
    /// <remarks>Only restack entries in a phase that still allows an undo are offered; an entry that moved nothing,
    /// was refused, was dismissed or was already restored is excluded.</remarks>
    /// <param name="repository">Scope key to search.</param>
    /// <param name="ct">Cancellation for the read.</param>
    /// <returns>The undoable entry, or <see langword="null"/> when there is none.</returns>
    Task<Result<JournalEntry?>> UndoableAsync(string repository, CancellationToken ct = default);
    /// <summary>Finds an entry by its operation id.</summary>
    /// <remarks>The runner re-reads the durable entry after execution so the reported effects come from stored
    /// steps rather than from the in-memory copy.</remarks>
    /// <param name="repository">Scope key the entry belongs to.</param>
    /// <param name="id">The operation id to find.</param>
    /// <param name="ct">Cancellation for the read.</param>
    /// <returns>The matching entry, or <see langword="null"/> when it is absent.</returns>
    Task<Result<JournalEntry?>> ByIdAsync(string repository, string id, CancellationToken ct = default);
    /// <summary>Lists saved stashes so the user can choose one to restore.</summary>
    /// <remarks>The default implementation scans every stored entry and projects those that hold a stash, which
    /// lets a simple store support the list; the JSON journal overrides it with its stash index. Results are newest
    /// first.</remarks>
    /// <param name="repository">Scope key to search.</param>
    /// <param name="ct">Cancellation for the read.</param>
    /// <returns>Saved stashes, newest first; empty when there are none.</returns>
    Task<Result<IReadOnlyList<SavedStash>>> StashesAsync(string repository, CancellationToken ct = default)
    {
        var entries = Recent(repository, int.MaxValue);
        return Task.FromResult(entries.Map<IReadOnlyList<SavedStash>>(items => items.Where(e => e.StashRef is not null)
            .Select(e => new SavedStash(e.StashRef!, e.Id, e.StartedAt, e.Kind)).ToArray()));
    }
    /// <summary>The newest entries for this scope, newest first.</summary>
    /// <remarks>The default count matches the operations list. The stored history is bounded: the JSON journal
    /// retains about 40 entries that no longer need attention, while entries that do, or that hold a stash, are
    /// kept.</remarks>
    /// <param name="repository">Scope key to search.</param>
    /// <param name="count">Maximum entries to return; defaults to 20.</param>
    /// <returns>The newest entries, newest first, or a problem when the journal cannot be read.</returns>
    Result<IReadOnlyList<JournalEntry>> Recent(string repository, int count = 20);
}

/// <summary>Reads time so commands and tests share one clock.</summary>
public interface IClock
{
    /// <summary>The current instant recorded as an entry's start and update time; the production clock reports UTC
    /// and tests substitute a fixed value so stored timestamps are deterministic.</summary>
    DateTimeOffset Now { get; }
}

/// <summary>What a command decided before running: the blockers and the effects it would cause.</summary>
/// <remarks><see cref="IStackCommand.PlanAsync"/> produces this without changing any state. A plan that cannot run
/// makes the runner return a refusal and never opens a journal entry, so a blocked action leaves no recovery
/// record; a runnable plan's effects are journaled before execution begins.</remarks>
/// <param name="CanRun">True when every precondition holds; false makes the runner refuse without any effect.</param>
/// <param name="Blockers">User-facing reasons the command cannot run; joined into the refusal message.</param>
/// <param name="Effects">The effects that will be journaled, in execution order.</param>
public sealed record CommandPlan(bool CanRun, IReadOnlyList<string> Blockers, IReadOnlyList<JournalStep> Effects)
{
    /// <summary>A plan that must not run, carrying the reasons shown to the user.</summary>
    /// <param name="blockers">One or more user-facing reasons.</param>
    /// <returns>A plan with <see cref="CanRun"/> false and no effects.</returns>
    public static CommandPlan Blocked(params string[] blockers) => new(false, blockers, []);
    /// <summary>A runnable plan whose effects will be journaled before execution.</summary>
    /// <param name="effects">The planned effects, in execution order.</param>
    /// <returns>A plan with <see cref="CanRun"/> true.</returns>
    public static CommandPlan Ready(IReadOnlyList<JournalStep> effects) => new(true, [], effects);
    /// <summary>A runnable plan with no planned steps, for commands whose effects are decided while executing.</summary>
    /// <returns>A plan with <see cref="CanRun"/> true and an empty effect list.</returns>
    public static CommandPlan Ready() => new(true, [], []);
}

/// <summary>The single final status of a command, chosen so the UI never has to reconcile two contradictory
/// flags.</summary>
/// <remarks><c>CommandRunner</c> maps a successful outcome to <see cref="CommandStatus.Applied"/>, a needs-user
/// outcome to <see cref="CommandStatus.NeedsUser"/>, a rolled-back one to
/// <see cref="CommandStatus.RolledBack"/>, and any other non-success to
/// <see cref="JournalPhase.Dismissed"/> in the journal. <see cref="CommandStatus.Refused"/> is returned before an
/// entry exists; <see cref="CommandStatus.Cancelled"/> only after cancellation;
/// <see cref="CommandStatus.PartiallyApplied"/> when some effects landed and could not be reversed.</remarks>
public enum CommandStatus
{
    /// <summary>Every effect the command intended completed.</summary>
    Applied,
    /// <summary>A precondition blocked the command; nothing ran and no journal entry was opened.</summary>
    Refused,
    /// <summary>An effect failed. The runner closes the entry as dismissed when the certainty proves that nothing
    /// happened; otherwise it reports the partial or needs-user result instead of a plain failure.</summary>
    Failed,
    /// <summary>The caller or shutdown cancelled the command; the runner attempted compensation with a short
    /// cleanup budget and reports whatever it could restore.</summary>
    Cancelled,
    /// <summary>Execution cannot proceed, or cannot prove the outcome, without a user decision. The journal entry
    /// stays in the needs-user phase.</summary>
    NeedsUser,
    /// <summary>Some effects landed and were not reversed, for example a multi-branch push that stopped midway; the
    /// message lists them.</summary>
    PartiallyApplied,
    /// <summary>The recorded effects were reversed after failure or cancellation.</summary>
    RolledBack
}

/// <summary>A single, non-contradictory status with the known effects and optional warnings.</summary>
/// <remarks>The status, message and problem are chosen together; <see cref="Ok"/>, <see cref="NeedsUser"/> and
/// <see cref="RolledBack"/> derive from the status so callers never interpret two flags. <see cref="Certainty"/>
/// defaults to <see cref="EffectCertainty.Unknown"/> and the factories set only what they can prove, which is how
/// "safe to retry" is decided. The runner fills <see cref="Effects"/> from durable steps; the command bus adds
/// <see cref="OperationId"/> and <see cref="Scope"/>.</remarks>
/// <param name="Status">The single final status.</param>
/// <param name="Message">Display-ready text for the status line and operations list.</param>
/// <param name="Problem">The catalogued failure when one exists; <see langword="null"/> for a success or a plain
/// refusal.</param>
public sealed record CommandOutcome(CommandStatus Status, string Message, Problem? Problem)
{
    /// <summary>Correlation id assigned by the command bus from the ambient operation; the same id names the
    /// journal entry and the diagnostic record.</summary>
    public string? OperationId { get; init; }
    /// <summary>Repository scope the operation belongs to (a local scope or a GitHub context identity), used to
    /// group the operations list.</summary>
    public string? Scope { get; init; }
    /// <summary>What is known about effects that were attempted; defaults to
    /// <see cref="EffectCertainty.Unknown"/> because a stop without proof must not be presented as safe to
    /// retry.</summary>
    public EffectCertainty Certainty { get; init; } = EffectCertainty.Unknown;
    /// <summary>True only for <see cref="CommandStatus.Applied"/>.</summary>
    public bool Ok => Status == CommandStatus.Applied;
    /// <summary>True for <see cref="CommandStatus.NeedsUser"/> and <see cref="CommandStatus.PartiallyApplied"/>;
    /// both require the user's attention.</summary>
    public bool NeedsUser => Status is CommandStatus.NeedsUser or CommandStatus.PartiallyApplied;
    /// <summary>True when the status is <see cref="CommandStatus.RolledBack"/>.</summary>
    public bool RolledBack => Status == CommandStatus.RolledBack;
    /// <summary>Human-readable descriptions of effects that completed, filled by the runner from durable steps;
    /// empty for a refusal.</summary>
    public IReadOnlyList<string> Effects { get; init; } = [];
    /// <summary>Whether an interrupted operation already has a different journal entry for recovery.</summary>
    internal bool HasSeparateRecoveryEntry { get; init; }
    /// <summary>A successful outcome; certainty is confirmed because the command returned after its effects.</summary>
    /// <param name="message">Display-ready success text.</param>
    /// <returns>An applied outcome with no problem.</returns>
    public static CommandOutcome Done(string message) => new(CommandStatus.Applied, message, null) { Certainty = EffectCertainty.Confirmed };
    /// <summary>Stops for a decision; the journal entry becomes needs-user. Certainty stays at the default
    /// <see cref="EffectCertainty.Unknown"/> unless the caller overrides it.</summary>
    /// <param name="message">Display-ready text explaining what the user must decide.</param>
    /// <returns>A needs-user outcome.</returns>
    public static CommandOutcome Attention(string message) => new(CommandStatus.NeedsUser, message, null);
    /// <summary>A precondition refusal; certainty is not-started because nothing ran.</summary>
    /// <param name="message">The blockers, joined for display.</param>
    /// <returns>A refused outcome with no problem.</returns>
    public static CommandOutcome Refused(string message) => new(CommandStatus.Refused, message, null) { Certainty = EffectCertainty.NotStarted };
    internal static CommandOutcome CancelledBeforeDispatch() => new(CommandStatus.Cancelled, "The action was cancelled before execution.", null)
        { Certainty = EffectCertainty.NotStarted };
    /// <summary>A failure carrying its problem; certainty is copied from the problem so callers can tell a
    /// definite refusal from an unknown remote result.</summary>
    /// <param name="problem">The catalogued failure.</param>
    /// <returns>A failed outcome whose message is the problem message.</returns>
    public static CommandOutcome Failed(Problem problem) => new(CommandStatus.Failed, problem.Message, problem) { Certainty = problem.Certainty };
    /// <summary>A rollback with an optional underlying problem. The status is rolled back; certainty is left at
    /// the default unless the caller overrides it.</summary>
    /// <param name="message">Display-ready text describing what was restored.</param>
    /// <param name="problem">The failure that caused the rollback, when one exists.</param>
    /// <returns>A rolled-back outcome.</returns>
    public static CommandOutcome Restored(string message, Problem? problem = null) => new(CommandStatus.RolledBack, message, problem);
}

/// <summary>Everything a command may use while it runs; effects go through the journal.</summary>
/// <remarks>The runner creates one context per command after opening the entry. <see cref="Entry"/> is the durable
/// record and <see cref="Journal"/> is the supported way to advance it; <see cref="Progress"/> receives short
/// status lines (for example, which layer is being rebased), not errors.</remarks>
/// <param name="Entry">The running journal entry opened for this command.</param>
/// <param name="Journal">The journal the command records its steps through.</param>
/// <param name="Progress">Optional sink for short progress text.</param>
public sealed record CommandContext(JournalEntry Entry, IJournal Journal, IProgress<string>? Progress);

/// <summary>A user-visible mutation; the only code allowed to change git, disk or GitHub state.</summary>
/// <remarks>Feature code runs a command through the command bus, which permits one foreground mutation at a time,
/// so a command may assume it is the only writer. Planning is read-only; the runner opens the journal only after a
/// runnable plan, and converts an escaping exception into a failure and, when possible, a compensation.</remarks>
public interface IStackCommand
{
    /// <summary>Stable command kind, recorded as the journal entry's kind and used to filter operations.</summary>
    string Kind { get; }
    /// <summary>One line for the UI describing what the command will do; must not change state.</summary>
    /// <returns>The display text.</returns>
    string Describe();
    /// <summary>Read-only preconditions; a plan that cannot run never opens a journal entry.</summary>
    /// <param name="ct">Cancellation for the checks.</param>
    /// <returns>The plan, including any blockers and the effects to journal.</returns>
    Task<CommandPlan> PlanAsync(CancellationToken ct = default);
    /// <summary>Runs the effects and reports what actually happened.</summary>
    /// <remarks>The command records each effect through <paramref name="context"/>'s journal and returns a status
    /// that matches its durable steps. The runner performs the final close and any compensation; an adapter may close
    /// the entry earlier when it stops to ask the user for a decision.</remarks>
    /// <param name="context">The running entry, journal and progress sink.</param>
    /// <param name="ct">Cancellation for the effects.</param>
    /// <returns>The outcome of this execution.</returns>
    Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default);
}

/// <summary>A command whose effects can be reversed from its journal entry alone.</summary>
/// <remarks>The runner calls <see cref="UndoAsync"/> after a failure or cancellation, using a fresh 30-second cleanup
/// budget instead of the cancelled token. An implementation must verify that refs still hold the recorded commits
/// before moving them; a rollback that cannot be proven leaves the entry in the needs-user phase rather than
/// guessing. Returning rolled-back or success lets the runner close the entry as rolled back.</remarks>
public interface IUndoableCommand : IStackCommand
{
    /// <summary>Reverses the recorded effects and reports whether restoration was proven.</summary>
    /// <param name="entry">The durable entry that describes what was done.</param>
    /// <param name="ct">Cancellation for the cleanup, bounded by the runner's cleanup budget.</param>
    /// <returns>A rolled-back or successful outcome when restoration is proven; any other outcome makes the runner
    /// leave the entry for the user.</returns>
    Task<CommandOutcome> UndoAsync(JournalEntry entry, CancellationToken ct = default);
}
