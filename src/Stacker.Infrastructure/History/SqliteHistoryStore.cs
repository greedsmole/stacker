using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

/// <summary>Transactional history and revision-checked drafts. Connections are short lived; no process-local lock is required.</summary>
public sealed partial class SqliteHistoryStore : IHistoryStore
{
    private readonly string _path;
    public SqliteHistoryStore(string root) { Directory.CreateDirectory(root); _path = Path.Combine(root, "review-history.db"); }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt64(version.ExecuteScalar()) > 1) { connection.Dispose(); throw new StackerException("History was created by a newer Stacker version. Database preserved."); }
        }
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=1; CREATE TABLE IF NOT EXISTS documents(kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 1, PRIMARY KEY(kind,id));";
        command.ExecuteNonQuery();
        using var indexes = connection.CreateCommand();
        indexes.CommandText = "CREATE INDEX IF NOT EXISTS versions_owner ON documents(kind,json_extract(json,'$.ChangeId')); CREATE INDEX IF NOT EXISTS graphs_scope ON documents(kind,json_extract(json,'$.Scope'));";
        indexes.ExecuteNonQuery(); return connection;
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static T? Read<T>(SqliteConnection c, SqliteTransaction? tx, string kind, string id)
    {
        using var command = Command(c, tx, "SELECT json FROM documents WHERE kind=$k AND id=$i", ("$k", kind), ("$i", id));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) : default;
    }
    private static void Put<T>(SqliteConnection c, SqliteTransaction? tx, string kind, string id, T value)
    {
        using var command = Command(c, tx, "INSERT INTO documents(kind,id,json) VALUES($k,$i,$j) ON CONFLICT(kind,id) DO UPDATE SET json=$j,revision=revision+1",
            ("$k", kind), ("$i", id), ("$j", JsonSerializer.Serialize(value))); command.ExecuteNonQuery();
    }
    private Task<Result<T>> Run<T>(Func<SqliteConnection, T> action, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        return Boundary.Capture(() => { using var connection = Open(); return action(connection); }, ProblemCodes.StoreWrite);
    }, ct);
    public Task<Result<ChangeNode>> ResolveNodeAsync(string scope, string reference, string title, long? pr = null, CancellationToken ct = default) => Run(c =>
    {
        using var tx = c.BeginTransaction(); var key = scope + "|" + (pr is null ? reference : "pr:" + pr);
        var old = Read<ChangeNode>(c, tx, "node", key);
        var node = old is null ? new ChangeNode(Guid.NewGuid().ToString("N"), scope, title, reference, pr) : old with { Title = title, Reference = reference };
        Put(c, tx, "node", key, node); tx.Commit(); return node;
    }, ct);
    public Task<Result<ChangeVersion>> ObserveAsync(ChangeVersion input, CancellationToken ct = default) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        var id = ApplicationStore.Key(input.ChangeId + "|" + input.BaseSha + "|" + input.HeadSha);
        var old = Read<ChangeVersion>(c, tx, "version", id);
        var version = old is null ? input with { Id = id } : old with { MergeBase = input.MergeBase ?? old.MergeBase };
        if (old?.MergeBase is not null && input.MergeBase is not null && old.MergeBase != input.MergeBase)
            throw new StackerException("An immutable version has conflicting merge-base evidence.");
        Put(c, tx, "version", id, version); tx.Commit(); return version;
    }, ct);
    private static IReadOnlyList<T> All<T>(SqliteConnection c, string kind, string? ownerField = null, string? owner = null)
    {
        var clause = ownerField switch { "ChangeId" => " AND json_extract(json,'$.ChangeId')=$o", "Scope" => " AND json_extract(json,'$.Scope')=$o", null => "", _ => throw new ArgumentException("Unknown history owner") };
        using var command = Command(c, null, "SELECT json FROM documents WHERE kind=$k" + clause + " ORDER BY rowid", ("$k", kind));
        if (ownerField is not null) command.Parameters.AddWithValue("$o", owner);
        using var reader = command.ExecuteReader(); var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!); return result;
    }
    public Task<Result<IReadOnlyList<ChangeVersion>>> VersionsAsync(string changeId, CancellationToken ct = default) =>
        Run<IReadOnlyList<ChangeVersion>>(c => All<ChangeVersion>(c, "version", "ChangeId", changeId).OrderBy(v => v.ObservedAt).ToArray(), ct);
    public Task<Result<Unit>> SaveGraphAsync(GraphSnapshot graph, CancellationToken ct = default) => Run(c =>
    {
        var previous = All<GraphSnapshot>(c, "graph", "Scope", graph.Scope).LastOrDefault();
        if (previous is null || JsonSerializer.Serialize(previous with { ObservedAt = graph.ObservedAt }) != JsonSerializer.Serialize(graph))
            Put(c, null, "graph", Guid.NewGuid().ToString("N"), graph);
        return Unit.Value;
    }, ct);
    public Task<Result<IReadOnlyList<GraphSnapshot>>> GraphsAsync(string scope, CancellationToken ct = default) =>
        Run<IReadOnlyList<GraphSnapshot>>(c => All<GraphSnapshot>(c, "graph", "Scope", scope).ToArray(), ct);
    public Task<Result<IReadOnlyList<ReviewCheckpoint>>> CheckpointsAsync(string changeId, CancellationToken ct = default) =>
        Run<IReadOnlyList<ReviewCheckpoint>>(c => All<ReviewCheckpoint>(c, "checkpoint", "ChangeId", changeId).ToArray(), ct);
    public Task<Result<Unit>> SetCheckpointAsync(ReviewCheckpoint checkpoint, bool remove, CancellationToken ct = default) => Run(c =>
    {
        var key = checkpoint.ChangeId + "|" + checkpoint.Path;
        if (remove) { using var cmd = Command(c, null, "DELETE FROM documents WHERE kind='checkpoint' AND id=$i", ("$i", key)); cmd.ExecuteNonQuery(); }
        else Put(c, null, "checkpoint", key, checkpoint);
        return Unit.Value;
    }, ct);
    public Task<Result<(T? Value, long Revision)>> ReadDraftAsync<T>(string id, string? legacyPath, CancellationToken ct) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        using var query = Command(c, tx, "SELECT json,revision FROM documents WHERE kind='draft' AND id=$i", ("$i", id));
        T? value; long revision;
        using (var reader = query.ExecuteReader())
        {
            if (reader.Read()) return (JsonSerializer.Deserialize<T>(reader.GetString(0)), reader.GetInt64(1));
        }
        if (legacyPath is not null && File.Exists(legacyPath))
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(legacyPath)) ?? throw new StackerException("Unreadable legacy draft was preserved.");
            Put(c, tx, "draft", id, value); revision = 1;
        }
        else { value = default; revision = 0; }
        tx.Commit(); return (value, revision);
    }, ct);
    public Task<Result<long>> SaveDraftAsync<T>(string id, T value, long expectedRevision, CancellationToken ct) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        using var command = expectedRevision == 0
            ? Command(c, tx, "INSERT OR IGNORE INTO documents(kind,id,json) VALUES('draft',$i,$j)", ("$i", id), ("$j", JsonSerializer.Serialize(value)))
            : Command(c, tx, "UPDATE documents SET json=$j,revision=revision+1 WHERE kind='draft' AND id=$i AND revision=$r", ("$i", id), ("$j", JsonSerializer.Serialize(value)), ("$r", expectedRevision));
        if (command.ExecuteNonQuery() != 1) throw new StackerException(Problem.Of(ProblemCodes.StoreStaleRevision, "Draft changed in another session. Your text is kept in this window; copy it before reloading."));
        tx.Commit(); return expectedRevision + 1;
    }, ct);
}
