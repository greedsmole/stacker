using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
namespace Stacker.Infrastructure;

/// <summary>Remote execution evidence shares the database and operation identity with publication payloads.</summary>
public sealed class SqliteJournalState(SqliteHistoryStore database, IJournal? legacy = null) : IStateStore<JournalLog>
{
    public Result<string> Quarantine(string identity) => Result<string>.Fail(ProblemCodes.StateInvalid, "Critical remote journal was preserved. Restore a backup or inspect recovery.");
    public Result<JournalLog?> Read(string identity) => Boundary.Capture(() =>
    {
        using var c = database.Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT json FROM remote_journals WHERE scope=$s"; command.Parameters.AddWithValue("$s", identity);
        if (command.ExecuteScalar() is string json) return FinalizePublications(c, null, JsonSerializer.Deserialize<JournalLog>(json)!);
        if (legacy is null) return null;
        var old = legacy.Recent(identity, int.MaxValue).OrThrow();
        return old.Count == 0 ? null : new JournalLog { Entries = old.Reverse().ToList() };
    }, ProblemCodes.StoreRead);
    public Task<Result<Unit>> WriteAsync(string identity, JournalLog value, CancellationToken ct = default) => Boundary.CaptureAsync(async () =>
    {
        using var c = database.Open(); using var tx = c.BeginTransaction(); using var command = c.CreateCommand(); command.Transaction = tx;
        value = FinalizePublications(c, tx, value);
        command.CommandText = "INSERT INTO remote_journals(scope,json) VALUES($s,$j) ON CONFLICT(scope) DO UPDATE SET json=$j";
        command.Parameters.AddWithValue("$s", identity); command.Parameters.AddWithValue("$j", JsonSerializer.Serialize(value)); await command.ExecuteNonQueryAsync(ct);
        foreach (var entry in value.Entries)
        {
            using var update = c.CreateCommand(); update.Transaction = tx;
            update.CommandText = "UPDATE publication_operations SET execution=$e WHERE id=$i";
            update.Parameters.AddWithValue("$i", entry.Id); update.Parameters.AddWithValue("$e", JsonSerializer.Serialize(entry)); await update.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }, ProblemCodes.StoreWrite);
    private static JournalLog FinalizePublications(SqliteConnection c, SqliteTransaction? tx, JournalLog journal)
    {
        for (var i = 0; i < journal.Entries.Count; i++)
        {
            using var query = c.CreateCommand(); query.Transaction = tx; query.CommandText = "SELECT phase FROM publication_operations WHERE id=$i"; query.Parameters.AddWithValue("$i", journal.Entries[i].Id);
            if (query.ExecuteScalar() is not long phase) continue;
            journal.Entries[i] = SqliteHistoryStore.ReconcilePublication(journal.Entries[i], (PublicationPhase)phase);
        }
        return journal;
    }
}
public sealed class RoutingJournal(IJournal local, SqliteHistoryStore database, IClock clock) : IJournal
{
    private readonly IJournal _remote = new JsonJournal(new SqliteJournalState(database, local), clock);
    private IJournal For(string scope) => scope.StartsWith("github:", StringComparison.Ordinal) ? _remote : local;
    public Task<Result<JournalEntry>> OpenAsync(string repository, string kind, IReadOnlyList<JournalStep> planned, CancellationToken ct = default) => For(repository).OpenAsync(repository, kind, planned, ct);
    public Task<Result<JournalEntry>> StepAsync(JournalEntry entry, JournalStep step, CancellationToken ct = default) => For(entry.Repository).StepAsync(entry, step, ct);
    public Task<Result<JournalEntry>> CloseAsync(JournalEntry entry, JournalPhase phase, string? stashRef = null, string? pendingStepId = null, string? message = null, CancellationToken ct = default) => For(entry.Repository).CloseAsync(entry, phase, stashRef, pendingStepId, message, ct);
    public Task<Result<JournalEntry>> SetStashAsync(JournalEntry entry, string? stashRef, CancellationToken ct = default) => For(entry.Repository).SetStashAsync(entry, stashRef, ct);
    public Task<Result<JournalEntry?>> ActiveAsync(string repository, CancellationToken ct = default) => For(repository).ActiveAsync(repository, ct);
    public Task<Result<JournalEntry?>> UndoableAsync(string repository, CancellationToken ct = default) => For(repository).UndoableAsync(repository, ct);
    public Task<Result<JournalEntry?>> ByIdAsync(string repository, string id, CancellationToken ct = default) => For(repository).ByIdAsync(repository, id, ct);
    public Result<IReadOnlyList<JournalEntry>> Recent(string repository, int count = 20) => For(repository).Recent(repository, count);
    public Task<Result<IReadOnlyList<SavedStash>>> StashesAsync(string repository, CancellationToken ct = default) => For(repository).StashesAsync(repository, ct);
}
