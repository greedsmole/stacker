// Purpose: Persists the write-ahead journal so any interrupted effect can be resumed, undone or dismissed.
using Stacker.Core.Framework;
namespace Stacker.Infrastructure.Framework;

/// <summary>Journal entries for every repository, bounded and ordered by time.</summary>
public sealed class JournalLog
{
    public List<JournalEntry> Entries { get; set; } = [];
    public List<JournalStash> Stashes { get; set; } = [];
}

public sealed record JournalStash(string Sha, string EntryId);

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
                clock.Now, clock.Now, JournalPhase.Running, planned, null, null, null)
                { RestackPlan = planned.FirstOrDefault(step => step.RestackPlan is not null)?.RestackPlan };
            await MutateAsync(repository, log =>
            {
                if (kind == "restack" && log.Entries.Any(existing => existing.Kind == "restack" && existing.NeedsAttention))
                    throw new Stacker.Core.StackerException("Restore, resume or dismiss the interrupted restack before starting another.");
                log.Entries.Add(entry);
                foreach (var old in log.Entries.Where(existing => !existing.NeedsAttention && existing.StashRef is null
                    && !log.Stashes.Any(stash => stash.EntryId == existing.Id))
                    .Take(Math.Max(0, log.Entries.Count - Retained)).ToArray()) log.Entries.Remove(old);
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
            var current = Find(entry);
            var updated = current with
            {
                UpdatedAt = clock.Now,
                Steps = current.Steps.Select(existing => existing.Id == step.Id ? step : existing).ToArray()
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
            var current = Find(entry);
            var updated = current with { Phase = phase, UpdatedAt = clock.Now, StashRef = stashRef ?? current.StashRef, PendingStepId = pendingStepId, Message = message };
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
            var updated = Find(entry) with { StashRef = stashRef, UpdatedAt = clock.Now };
            await MutateAsync(entry.Repository, log =>
            {
                if (stashRef is null && entry.StashRef is { } cleared)
                {
                    log.Stashes.RemoveAll(stash => stash.Sha == cleared);
                    for (var i = 0; i < log.Entries.Count; i++)
                        if (log.Entries[i].StashRef == cleared) log.Entries[i] = log.Entries[i] with { StashRef = null };
                }
                else Replace(log, updated);
            }, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Anything the user must still deal with: a decision, an unfinished run, or a stash to restore.</summary>
    public Task<JournalEntry?> ActiveAsync(string repository, CancellationToken ct = default)
    {
        var log = Load(repository);
        var active = log.Entries.LastOrDefault(entry => entry.NeedsAttention && entry.Kind == "restack")
            ?? log.Entries.LastOrDefault(entry => entry.NeedsAttention && entry.Kind != "stash");
        if (active is not null) return Task.FromResult<JournalEntry?>(active);
        var stash = log.Stashes.LastOrDefault();
        var owner = stash is null ? null : log.Entries.FirstOrDefault(e => e.Id == stash.EntryId);
        return Task.FromResult(owner is null ? null : owner with { StashRef = stash!.Sha });
    }

    /// <summary>Only an entry that actually moved something can be undone.</summary>
    public Task<JournalEntry?> UndoableAsync(string repository, CancellationToken ct = default) =>
        Task.FromResult(Read(repository).LastOrDefault(entry => entry.Kind == "restack" && entry.CanUndo && entry.Moved.Count > 0));

    public Task<JournalEntry?> ByIdAsync(string repository, string id, CancellationToken ct = default) =>
        Task.FromResult(Read(repository).LastOrDefault(entry => entry.Id == id));

    public IReadOnlyList<JournalEntry> Recent(string repository, int count = 20) =>
        Read(repository).TakeLast(count).Reverse().ToArray();

    public Task<Result<IReadOnlyList<SavedStash>>> StashesAsync(string repository, CancellationToken ct = default) =>
        Boundary.CaptureAsync<IReadOnlyList<SavedStash>>(() =>
        {
            var log = Load(repository);
            IReadOnlyList<SavedStash> items = log.Stashes.AsEnumerable().Reverse().Select(stash =>
            {
                var owner = log.Entries.First(entry => entry.Id == stash.EntryId);
                return new SavedStash(stash.Sha, owner.Id, owner.StartedAt, owner.Kind);
            }).ToArray();
            return Task.FromResult(items);
        }, ProblemCodes.StateInvalid);

    private IReadOnlyList<JournalEntry> Read(string repository)
    {
        return Load(repository).Entries.Where(entry => entry.Repository == repository).OrderBy(entry => entry.StartedAt).ToArray();
    }

    private JournalLog Load(string repository)
    {
        var log = store.Read(repository).OrThrow() ?? new JournalLog();
        if (log.Entries is null || log.Stashes is null || log.Entries.Any(entry => entry is null
            || entry.SchemaVersion is < 2 or > JournalEntry.CurrentSchema || entry.Steps is null
            || !Enum.IsDefined(entry.Phase) || entry.Steps.Any(step => step is null || !Enum.IsDefined(step.State)))
            || log.Stashes.Any(stash => stash is null || !log.Entries.Any(entry => entry.Id == stash.EntryId)))
            throw new Stacker.Core.StackerException(Problem.Of(ProblemCodes.StateInvalid,
                "The operation journal has an unsupported or invalid structure. Preserve it and inspect recovery before making changes."));
        foreach (var entry in log.Entries.Where(e => e.StashRef is not null))
            if (!log.Stashes.Any(stash => stash.Sha == entry.StashRef)) log.Stashes.Add(new(entry.StashRef!, entry.Id));
        return log;
    }

    private JournalEntry Find(JournalEntry entry) => Read(entry.Repository).LastOrDefault(e => e.Id == entry.Id)
        ?? throw new Stacker.Core.StackerException("The operation journal entry is missing. No further effects are allowed.");

    private async Task ReplaceAsync(JournalEntry entry, CancellationToken ct) =>
        await MutateAsync(entry.Repository, log => Replace(log, entry), ct);

    private static void Replace(JournalLog log, JournalEntry entry)
    {
        var index = log.Entries.FindIndex(existing => existing.Id == entry.Id);
        if (index >= 0) log.Entries[index] = entry; else log.Entries.Add(entry);
        if (entry.StashRef is { } sha && !log.Stashes.Any(stash => stash.Sha == sha)) log.Stashes.Add(new(sha, entry.Id));
    }

    private async Task MutateAsync(string repository, Action<JournalLog> mutate, CancellationToken ct)
    {
        var log = Load(repository);
        mutate(log);
        (await store.WriteAsync(repository, log, ct)).OrThrow();
    }

    Task<Result<JournalEntry>> IJournal.OpenAsync(string repository,string kind,IReadOnlyList<JournalStep> planned,CancellationToken ct) => Boundary.CaptureAsync(() => OpenAsync(repository, kind, planned, ct), ProblemCodes.StoreWrite);

    Task<Result<JournalEntry>> IJournal.StepAsync(JournalEntry entry,JournalStep step,CancellationToken ct) => Boundary.CaptureAsync(() => StepAsync(entry, step, ct), ProblemCodes.StoreWrite);

    Task<Result<JournalEntry>> IJournal.CloseAsync(JournalEntry entry,JournalPhase phase,string? stashRef ,string? pendingStepId ,string? message ,CancellationToken ct) => Boundary.CaptureAsync(() => CloseAsync(entry, phase, stashRef, pendingStepId, message, ct), ProblemCodes.StoreWrite);

    Task<Result<JournalEntry>> IJournal.SetStashAsync(JournalEntry entry,string? stashRef,CancellationToken ct) => Boundary.CaptureAsync(() => SetStashAsync(entry, stashRef, ct), ProblemCodes.StoreWrite);

    Task<Result<JournalEntry?>> IJournal.ActiveAsync(string repository,CancellationToken ct) => Boundary.CaptureAsync(() => ActiveAsync(repository, ct), ProblemCodes.StoreWrite);

    Task<Result<JournalEntry?>> IJournal.UndoableAsync(string repository,CancellationToken ct) => Boundary.CaptureAsync(() => UndoableAsync(repository, ct), ProblemCodes.StoreWrite);

    Task<Result<JournalEntry?>> IJournal.ByIdAsync(string repository,string id,CancellationToken ct) => Boundary.CaptureAsync(() => ByIdAsync(repository, id, ct), ProblemCodes.StoreWrite);

    Result<IReadOnlyList<JournalEntry>> IJournal.Recent(string repository,int count ) => Boundary.Capture(() => Recent(repository, count), ProblemCodes.StoreWrite);
}
