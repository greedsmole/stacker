using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

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
