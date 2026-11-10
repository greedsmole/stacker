using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    internal const int Schema = 3;
    private readonly object _initialization = new();
    private bool _initialized;
    private void Initialize(SqliteConnection connection)
    {
        lock (_initialization)
        {
            if (_initialized) return;
            using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
            var old = Convert.ToInt32(version.ExecuteScalar());
            if (old > Schema) throw new StackerException("History was created by a newer Stacker version. Database preserved.");
            if (old == Schema) { _initialized = true; return; }
            if (old > 0) BackupBeforeMigration(connection);
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS nodes(id TEXT PRIMARY KEY,json TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 1,entity_id TEXT NOT NULL UNIQUE,scope TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS versions(id TEXT PRIMARY KEY,json TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 1,owner TEXT NOT NULL REFERENCES nodes(entity_id),base_sha TEXT NOT NULL,head_sha TEXT NOT NULL,first_seen TEXT NOT NULL,UNIQUE(owner,base_sha,head_sha));
                CREATE TABLE IF NOT EXISTS materializations(version_id TEXT PRIMARY KEY REFERENCES versions(id),merge_base TEXT);
                CREATE TABLE IF NOT EXISTS object_catalog(scope TEXT PRIMARY KEY,availability INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS repository_contents(repository_id TEXT NOT NULL,scope TEXT NOT NULL,availability INTEGER NOT NULL,PRIMARY KEY(repository_id,scope));
                CREATE TABLE IF NOT EXISTS object_imports(repository_id TEXT NOT NULL,source TEXT NOT NULL,PRIMARY KEY(repository_id,source));
                CREATE TABLE IF NOT EXISTS observations(sequence INTEGER PRIMARY KEY AUTOINCREMENT,version_id TEXT NOT NULL REFERENCES versions(id),owner TEXT NOT NULL REFERENCES nodes(entity_id),seen_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS graphs(sequence INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,json TEXT NOT NULL,revision INTEGER NOT NULL,scope TEXT NOT NULL,UNIQUE(scope,revision));
                CREATE TABLE IF NOT EXISTS graph_members(graph_id TEXT NOT NULL REFERENCES graphs(id),node_id TEXT NOT NULL REFERENCES nodes(entity_id),version_id TEXT REFERENCES versions(id),PRIMARY KEY(graph_id,node_id));
                CREATE TABLE IF NOT EXISTS checkpoints(id TEXT PRIMARY KEY,json TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 1,owner TEXT NOT NULL REFERENCES nodes(entity_id),version_id TEXT NOT NULL REFERENCES versions(id));
                CREATE TABLE IF NOT EXISTS concerns(id TEXT PRIMARY KEY,json TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 1,owner TEXT NOT NULL REFERENCES nodes(entity_id));
                CREATE TABLE IF NOT EXISTS drafts(id TEXT PRIMARY KEY,json TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE IF NOT EXISTS publication_operations(id TEXT PRIMARY KEY,draft_key TEXT NOT NULL,payload TEXT NOT NULL,phase INTEGER NOT NULL,execution TEXT);
                CREATE UNIQUE INDEX IF NOT EXISTS publication_active ON publication_operations(draft_key) WHERE phase IN(0,1,3);
                CREATE TABLE IF NOT EXISTS remote_journals(scope TEXT PRIMARY KEY,json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS repositories(id TEXT PRIMARY KEY,common_directory TEXT NOT NULL UNIQUE);
                CREATE TABLE IF NOT EXISTS locations(root TEXT PRIMARY KEY,repository_id TEXT NOT NULL REFERENCES repositories(id),worktree_id TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS scope_aliases(alias TEXT PRIMARY KEY,scope TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS change_bindings(local_scope TEXT NOT NULL,local_ref TEXT NOT NULL,remote_scope TEXT NOT NULL,pr INTEGER NOT NULL,PRIMARY KEY(local_scope,local_ref,remote_scope));
                CREATE TABLE IF NOT EXISTS change_aliases(alias_id TEXT PRIMARY KEY REFERENCES nodes(entity_id),canonical_id TEXT NOT NULL REFERENCES nodes(entity_id));
                CREATE INDEX IF NOT EXISTS versions_owner ON versions(owner,first_seen);
                CREATE INDEX IF NOT EXISTS graphs_latest ON graphs(scope,revision DESC);
                CREATE INDEX IF NOT EXISTS observations_latest ON observations(version_id,sequence DESC);
                CREATE INDEX IF NOT EXISTS observations_owner ON observations(owner,sequence DESC);
                CREATE INDEX IF NOT EXISTS nodes_scope ON nodes(scope);
                CREATE INDEX IF NOT EXISTS checkpoints_owner ON checkpoints(owner);
                CREATE INDEX IF NOT EXISTS concerns_owner ON concerns(owner);
                """;
            command.ExecuteNonQuery();
            if (old == 1) ImportDocuments(connection, tx);
            command.CommandText = "PRAGMA user_version=3"; command.ExecuteNonQuery(); tx.Commit(); _initialized = true;
        }
    }
    private void ImportDocuments(SqliteConnection c, SqliteTransaction tx)
    {
        using var query = Command(c, tx, "SELECT kind,id,json,revision FROM documents ORDER BY CASE kind WHEN 'node' THEN 0 WHEN 'version' THEN 1 WHEN 'graph' THEN 2 ELSE 3 END,rowid");
        var rows = new List<(string Kind, string Id, string Json, long Revision)>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case "node": Put(c, tx, row.Kind, row.Id, JsonSerializer.Deserialize<ChangeNode>(row.Json)!); break;
                case "version": SaveVersion(c, tx, JsonSerializer.Deserialize<ChangeVersion>(row.Json)!); break;
                case "graph": SaveGraph(c, tx, JsonSerializer.Deserialize<GraphSnapshot>(row.Json)!, null); break;
                case "checkpoint": Put(c, tx, row.Kind, row.Id, JsonSerializer.Deserialize<ReviewCheckpoint>(row.Json)!); break;
                case "concern": Put(c, tx, row.Kind, row.Id, JsonSerializer.Deserialize<ReviewConcern>(row.Json)!); break;
                case "draft":
                    using (var insert = Command(c, tx, "INSERT INTO drafts(id,json,revision) VALUES($i,$j,$r)", ("$i", row.Id), ("$j", row.Json), ("$r", row.Revision))) insert.ExecuteNonQuery();
                    break;
                default: throw new StackerException("Unknown history document kind. Original database and migration backup were retained: " + row.Kind);
            }
        }
    }
    private void BackupBeforeMigration(SqliteConnection source)
    {
        var directory = Path.Combine(Path.GetDirectoryName(_path)!, "backups"); Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "history-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssffff") + ".db");
        using var backup = CreateConnection(destination); backup.Open(); source.BackupDatabase(backup);
        foreach (var stale in Directory.GetFiles(directory, "history-*.db").OrderByDescending(Path.GetFileName).Skip(3)) File.Delete(stale);
    }
}
