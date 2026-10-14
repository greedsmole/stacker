// Purpose: Persists the write-ahead journal so any interrupted effect can be resumed, undone or dismissed.
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>Journal entries for every repository, bounded and ordered by time.</summary>
public sealed class JournalLog
{
    public List<JournalEntry> Entries { get; set; } = [];
}

public sealed class JsonJournal(IStateStore<JournalLog> store, IClock clock) : IJournal
{
    private const int Retained = 40;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<JournalEntry> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var entry = new JournalEntry(JournalEntry.CurrentSchema, Guid.NewGuid().ToString("N"), repository, kind,
                clock.Now, clock.Now, JournalPhase.Running, planned, null, null, null);
            await MutateAsync(repository, log =>
            {
                log.Entries.RemoveAll(existing => existing.Repository == repository && (existing.NeedsAttention || existing.CanUndo));
                log.Entries.Add(entry);
                while (log.Entries.Count > Retained) log.Entries.RemoveAt(0);
            }, ct);
            return entry;
        }
        finally { _gate.Release(); }
    }

    public async Task<JournalEntry> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var updated = entry with
            {
                UpdatedAt = clock.Now,
                Steps = entry.Steps.Select(existing => existing.Id == step.Id ? step : existing).ToArray()
            };
            await ReplaceAsync(updated, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<JournalEntry> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var updated = entry with { Phase = phase, UpdatedAt = clock.Now, StashRef = stashRef ?? entry.StashRef, PendingStepId = pendingStepId, Message = message };
            await ReplaceAsync(updated, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<JournalEntry> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var updated = entry with { StashRef = stashRef, UpdatedAt = clock.Now };
            await ReplaceAsync(updated, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Anything the user must still deal with: a decision, an unfinished run, or a stash to restore.</summary>
    public Task<JournalEntry?> ActiveAsync(string repository, CancellationToken ct = default) =>
        Task.FromResult(Read(repository).LastOrDefault(entry => entry.NeedsAttention || entry.StashRef is not null));

    /// <summary>Only an entry that actually moved something can be undone.</summary>
    public Task<JournalEntry?> UndoableAsync(string repository, CancellationToken ct = default) =>
        Task.FromResult(Read(repository).LastOrDefault(entry => entry.CanUndo && entry.Moved.Count > 0));

    public Task<JournalEntry?> ByIdAsync(string repository, string id, CancellationToken ct = default) =>
        Task.FromResult(Read(repository).LastOrDefault(entry => entry.Id == id));

    public IReadOnlyList<JournalEntry> Recent(string repository, int count = 20) =>
        Read(repository).TakeLast(count).Reverse().ToArray();

    private IReadOnlyList<JournalEntry> Read(string repository)
    {
        var log = store.Read(repository).Value;
        return log is null ? [] : log.Entries.Where(entry => entry.Repository == repository).OrderBy(entry => entry.StartedAt).ToArray();
    }

    private async Task ReplaceAsync(JournalEntry entry, CancellationToken ct) =>
        await MutateAsync(entry.Repository, log =>
        {
            var index = log.Entries.FindIndex(existing => existing.Id == entry.Id);
            if (index >= 0) log.Entries[index] = entry; else log.Entries.Add(entry);
        }, ct);

    private async Task MutateAsync(string repository, Action<JournalLog> mutate, CancellationToken ct)
    {
        var log = store.Read(repository).Value ?? new JournalLog();
        mutate(log);
        await store.WriteAsync(repository, log, ct);
    }
}
