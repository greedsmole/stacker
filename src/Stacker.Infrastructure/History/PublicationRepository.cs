using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Infrastructure.Framework;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    private static void SaveOperation(SqliteConnection c,SqliteTransaction tx,string key,PublicationOperation operation)
    {
        using(var previous=Command(c,tx,"SELECT draft_key,payload FROM publication_operations WHERE id=$i",("$i",operation.Id)))
        using(var row=previous.ExecuteReader())
        {
            if(row.Read())
            {
                var original=JsonSerializer.Deserialize<PublicationOperation>(row.GetString(1))!;
                if(row.GetString(0)!=key || original with{Phase=operation.Phase}!=operation)throw new StackerException("Publication identity or immutable payload changed. Original operation was preserved.");
            }
        }
        using(var entry=Command(c,tx,"INSERT INTO publication_operations(id,draft_key,payload,phase) VALUES($i,$k,$p,$s) ON CONFLICT(id) DO UPDATE SET phase=CASE WHEN phase=2 THEN 2 ELSE excluded.phase END",
            ("$i",operation.Id),("$k",key),("$p",JsonSerializer.Serialize(operation)),("$s",(int)operation.Phase)))entry.ExecuteNonQuery();
        if(operation.Phase is not(PublicationPhase.Confirmed or PublicationPhase.Rejected))return;
        var scope="github:"+operation.ContextIdentity;
        using var lookup=Command(c,tx,"SELECT json FROM remote_journals WHERE scope=$s",("$s",scope));
        if(lookup.ExecuteScalar() is not string json)return;
        var journal=JsonSerializer.Deserialize<JournalLog>(json)!;
        var index=journal.Entries.FindIndex(e=>e.Id==operation.Id);if(index<0)return;
        var updated=journal.Entries[index] with{Phase=operation.Phase==PublicationPhase.Confirmed?Core.Framework.JournalPhase.Applied:Core.Framework.JournalPhase.Dismissed};
        journal.Entries[index]=updated;
        using(var update=Command(c,tx,"UPDATE remote_journals SET json=$j WHERE scope=$s",("$j",JsonSerializer.Serialize(journal)),("$s",scope)))update.ExecuteNonQuery();
        using(var update=Command(c,tx,"UPDATE publication_operations SET execution=$j WHERE id=$i",("$j",JsonSerializer.Serialize(updated)),("$i",operation.Id)))update.ExecuteNonQuery();
    }
}
