using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    private static void SaveOperation(SqliteConnection c, SqliteTransaction tx, string key, PublicationOperation operation)
    {
        if (!Enum.IsDefined(operation.Phase))
            throw new StackerException(Problem.Of(ProblemCodes.StateInvalid, "Unknown publication phase."));
        using (var previous = Command(c, tx, """
            SELECT draft_key,
                   payload,
                   phase
              FROM publication_operations
             WHERE id=$i
            """, ("$i", operation.Id)))
        using (var row = previous.ExecuteReader())
        {
            if (row.Read())
            {
                var original = JsonSerializer.Deserialize<PublicationOperation>(row.GetString(1))!;
                if (row.GetString(0) != key || original with
                {
                    Phase = operation.Phase
                } != operation)
                    throw new StackerException("Publication identity or immutable payload changed. Original operation was preserved.");
                var current = (PublicationPhase)row.GetInt32(2);
                if (!ReviewSessionService.CanTransition(current, operation.Phase))
                    throw new StackerException(Problem.Of(ProblemCodes.StateInvalid, $"Cannot record publication as {operation.Phase} from {current}."));
            }
        }
        using var entry = Command(c, tx, """
              INSERT INTO publication_operations(
              id,
              draft_key,
              payload,
              phase
            )
                   VALUES (
              $i,
              $k,
              $p,
              $s
            )
              ON CONFLICT (id)
            DO UPDATE SET phase=excluded.phase
                RETURNING phase
            """,
            ("$i", operation.Id), ("$k", key), ("$p", JsonSerializer.Serialize(operation)), ("$s", (int)operation.Phase));
        var savedPhase = (PublicationPhase)(long)entry.ExecuteScalar()!;
        if (savedPhase is not (PublicationPhase.Confirmed or PublicationPhase.Rejected))
            return;
        var scope = "github:" + operation.ContextIdentity;
        using var lookup = Command(c, tx, """
            SELECT json
              FROM remote_journals
             WHERE scope=$s
            """, ("$s", scope));
        if (lookup.ExecuteScalar() is not string json)
            return;
        var journal = JsonSerializer.Deserialize<JournalLog>(json)!;
        var index = journal.Entries.FindIndex(e => e.Id == operation.Id);
        if (index < 0)
            return;
        var updated = ReconcilePublication(journal.Entries[index], savedPhase);
        journal.Entries[index] = updated;
        using (var update = Command(c, tx, """
            UPDATE remote_journals
               SET json=$j
             WHERE scope=$s
            """, ("$j", JsonSerializer.Serialize(journal)), ("$s", scope)))
            update.ExecuteNonQuery();
        using (var update = Command(c, tx, """
            UPDATE publication_operations
               SET execution=$j
             WHERE id=$i
            """, ("$j", JsonSerializer.Serialize(updated)), ("$i", operation.Id)))
            update.ExecuteNonQuery();
    }

    /// <summary>Projects durable publication evidence onto its matching journal entry.</summary>
    internal static JournalEntry ReconcilePublication(JournalEntry entry, PublicationPhase phase) => (phase, entry.Phase) switch
    {
        (_, JournalPhase.Running or JournalPhase.NeedsUser or JournalPhase.Dismissed)
            when phase == PublicationPhase.Confirmed || phase == PublicationPhase.Rejected && ReviewSessionService.HasAcknowledgedExecution(entry) =>
            entry with
            {
                Phase = JournalPhase.Applied
            },
        (PublicationPhase.Rejected, JournalPhase.Running or JournalPhase.NeedsUser) =>
            entry with
            {
                Phase = JournalPhase.Dismissed
            },
        _ => entry
    };
}
