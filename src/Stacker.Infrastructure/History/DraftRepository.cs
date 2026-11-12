using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    public Task<Result<(T? Value, long Revision)>> ReadDraftAsync<T>(string id, string? legacyPath, CancellationToken ct) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        using var query = Command(c, tx, "SELECT json,revision FROM drafts WHERE id=$i", ("$i", id));
        T? value = default; long revision = 0; var found = false;
        using (var reader = query.ExecuteReader())
        {
            if (reader.Read()) { value = JsonSerializer.Deserialize<T>(reader.GetString(0)); revision = reader.GetInt64(1); found = true; }
        }
        if (!found && legacyPath is not null && File.Exists(legacyPath))
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(legacyPath)) ?? throw new StackerException("Unreadable legacy draft was preserved.");
            Put(c, tx, "draft", id, value); revision = 1;
            if (value is ReviewDraft imported && imported.Operation is { } operation) SaveOperation(c, tx, id, operation);
        }
        if (value is ReviewDraft draft)
        {
            using var active = Command(c, tx, "SELECT payload,phase,execution FROM publication_operations WHERE draft_key=$k AND phase IN(0,1,3)", ("$k", id));
            using var rows = active.ExecuteReader();
            if (rows.Read())
            {
                var operation = JsonSerializer.Deserialize<PublicationOperation>(rows.GetString(0))! with { Phase = (PublicationPhase)rows.GetInt32(1) };
                draft.Operation = operation;
                if (!rows.IsDBNull(2) && JsonSerializer.Deserialize<JournalEntry>(rows.GetString(2)) is { } execution)
                    ReviewSessionService.ReconcileExecution(draft, execution);
            }
        }
        tx.Commit(); return (value, revision);
    }, ct);

    public Task<Result<long>> SaveDraftAsync<T>(string id, T value, long expectedRevision, CancellationToken ct) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        using var command = expectedRevision == 0
            ? Command(c, tx, "INSERT OR IGNORE INTO drafts(id,json) VALUES($i,$j)", ("$i", id), ("$j", JsonSerializer.Serialize(value)))
            : Command(c, tx, "UPDATE drafts SET json=$j,revision=revision+1 WHERE id=$i AND revision=$r", ("$i", id), ("$j", JsonSerializer.Serialize(value)), ("$r", expectedRevision));
        if (command.ExecuteNonQuery() != 1) throw new StackerException(Problem.Of(ProblemCodes.StoreStaleRevision, "Draft changed in another session. Your text is kept in this window; copy it before reloading."));
        if (value is ReviewDraft draft)
        {
            if (draft.Operation is { } active && draft.PublicationHistory.Any(operation => operation.Id == active.Id))
                throw new StackerException(Problem.Of(ProblemCodes.StateInvalid, "A publication cannot be both active and archived."));
            var unfinished = new HashSet<string>();
            using (var pending = Command(c, tx, "SELECT id FROM publication_operations WHERE draft_key=$k AND phase IN(0,1,3)", ("$k", id)))
            using (var rows = pending.ExecuteReader()) while (rows.Read()) unfinished.Add(rows.GetString(0));
            // A coalesced save may contain a rejected old operation and a new prepared one.
            // Retire the old operation first, in the same transaction as the new payload.
            var operations = draft.PublicationHistory.Where(o => unfinished.Contains(o.Id)).Concat(draft.PublicationHistory.TakeLast(1));
            if (draft.Operation is { } current) operations = operations.Append(current);
            operations = operations.DistinctBy(o => o.Id);
            foreach (var operation in operations)
            {
                SaveOperation(c, tx, id, operation);
            }
        }
        tx.Commit(); return expectedRevision + 1;
    }, ct);
    public async Task<Result<RevisionedDraft?>> ReadDraftAsync(string key, CancellationToken ct = default)
    {
        var read = await ReadDraftAsync<ReviewDraft>(key, Path.Combine(Path.GetDirectoryName(_path)!, "drafts", ApplicationStore.Key(key) + ".json"), ct);
        return read.Map<RevisionedDraft?>(r => r.Value is null ? null : new(r.Value, r.Revision));
    }

    public Task<Result<long>> SaveDraftAsync(string key, ReviewDraft draft, long expectedRevision, CancellationToken ct = default) =>
        SaveDraftAsync<ReviewDraft>(key, draft, expectedRevision, ct);
}
