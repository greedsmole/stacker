using System.Text.Json;
using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    internal void MigrateScope(string oldScope, string newScope)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var select = Command(c, tx, """
            SELECT id,
                   json
              FROM nodes
             WHERE scope=$s
            """, ("$s", oldScope));
        var nodes = new List<(string Key, ChangeNode Node)>();
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                nodes.Add((reader.GetString(0), JsonSerializer.Deserialize<ChangeNode>(reader.GetString(1))!));
        foreach (var (key, node) in nodes)
        {
            var moved = node with
            {
                Scope = newScope
            };
            var existing = Read<ChangeNode>(c, tx, "node", NodeKey(moved));
            if (existing is not null && existing.Id != node.Id)
            {
                using var alias = Command(c, tx, """
                      INSERT INTO change_aliases(
                      alias_id,
                      canonical_id
                    )
                           VALUES (
                      $a,
                      $c
                    )
                      ON CONFLICT (alias_id)
                    DO UPDATE SET canonical_id=$c
                    """, ("$a", node.Id), ("$c", existing.Id));
                alias.ExecuteNonQuery();
                continue;
            }
            using var update = Command(c, tx, """
                UPDATE nodes
                   SET id=$n,
                       json=$j,
                       scope=$s
                 WHERE id=$o
                """, ("$n", NodeKey(moved)), ("$j", JsonSerializer.Serialize(moved)), ("$s", newScope), ("$o", key));
            update.ExecuteNonQuery();
        }
        using var graphs = Command(c, tx, """
              SELECT id,
                     json
                FROM graphs
               WHERE scope=$s
            ORDER BY revision
            """, ("$s", oldScope));
        var saved = new List<(string, GraphSnapshot)>();
        using (var reader = graphs.ExecuteReader())
            while (reader.Read())
                saved.Add((reader.GetString(0), JsonSerializer.Deserialize<GraphSnapshot>(reader.GetString(1))!));
        var revision = Latest(c, tx, newScope)?.Revision ?? 0;
        foreach (var (id, graph) in saved)
        {
            var moved = graph with
            {
                Scope = newScope,
                Revision = ++revision,
                Nodes = graph.Nodes.Select(n => n with { Scope = newScope }).ToArray()
            };
            using var update = Command(c, tx, """
                UPDATE graphs
                   SET scope=$s,
                       revision=$r,
                       json=$j
                 WHERE id=$i
                """, ("$s", newScope), ("$r", revision), ("$j", JsonSerializer.Serialize(moved)), ("$i", id));
            update.ExecuteNonQuery();
        }
        using (var catalog = Command(c, tx, """
            INSERT OR IGNORE INTO object_catalog(
              scope,
              availability
            )
                           SELECT $n,
                                  availability
                             FROM object_catalog
                            WHERE scope=$o
            """, ("$n", newScope), ("$o", oldScope)))
            catalog.ExecuteNonQuery();
        tx.Commit();

    }
    internal string BoundScope(string localScope, string reference, string? activeAccount)
    {
        if (activeAccount is null)
            return localScope;
        using var c = Open();
        using var cmd = Command(c, null, """
            SELECT remote_scope
              FROM change_bindings
             WHERE local_scope=$s
               AND local_ref=$f
               AND remote_scope=$r
            """, ("$s", localScope), ("$f", reference), ("$r", activeAccount));
        return cmd.ExecuteScalar() as string ?? localScope;
    }
}
