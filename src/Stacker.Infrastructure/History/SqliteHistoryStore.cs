using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore : IHistoryStore, IReviewDraftRepository
{
    private readonly string _path;
    internal string Root => Path.GetDirectoryName(_path)!;
    public SqliteHistoryStore(string root)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "review-history.db");
    }
    internal static SqliteConnection CreateConnection(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling = false,
        DefaultTimeout = 5,
        ForeignKeys = true
    }.ToString());
    internal SqliteConnection Open()
    {
        var c = CreateConnection(_path);
        try
        {
            c.Open();
            Initialize(c);
            return c;
        }
        catch { c.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (key, value) in values)
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private static string Table(string kind) => kind switch { "node" => "nodes", "version" => "versions", "graph" => "graphs", "checkpoint" => "checkpoints", "concern" => "concerns", "draft" => "drafts", _ => throw new ArgumentException("Unknown history kind") };
    private static T? Read<T>(SqliteConnection c, SqliteTransaction? tx, string kind, string id)
    {
        using var query = Command(c, tx, $"""
            SELECT json
              FROM {Table(kind)}
             WHERE id=$i
            """, ("$i", id));
        return query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) : default;
    }
    private static void Put<T>(SqliteConnection c, SqliteTransaction? tx, string kind, string id, T value)
    {
        var fields = new List<(string, object?)> { ("id", id), ("json", JsonSerializer.Serialize(value)) };
        switch (value)
        {
            case ChangeNode node:
                fields.Add(("entity_id", node.Id));
                fields.Add(("scope", node.Scope));
                break;
            case ReviewCheckpoint mark:
                fields.Add(("owner", mark.ChangeId));
                fields.Add(("version_id", mark.VersionId));
                break;
            case ReviewConcern concern:
                fields.Add(("owner", concern.ChangeId));
                break;
        }
        var columns = string.Join(',', fields.Select(f => f.Item1));
        var values = string.Join(',', fields.Select((_, i) => "$p" + i));
        var updates = string.Join(',', fields.Skip(1).Select(f => $"{f.Item1}=excluded.{f.Item1}"));
        using var query = Command(c, tx, $"""
              INSERT INTO {Table(kind)}({columns})
                   VALUES ({values})
              ON CONFLICT (id)
            DO UPDATE SET {updates},
                          revision=revision+1
            """,
            fields.Select((f, i) => ("$p" + i, f.Item2)).ToArray());
        query.ExecuteNonQuery();
    }
    private Task<Result<T>> Run<T>(Func<SqliteConnection, T> action, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        return Boundary.Capture(() => { using var c = Open(); return action(c); }, ProblemCodes.StoreWrite);
    }, ct);
    private static string Key(string scope, string reference, long? pr) => scope + "|" + (pr is null ? reference : "pr:" + pr);
    public Task<Result<ChangeNode?>> FindNodeAsync(string scope, string reference, long? pr = null, CancellationToken ct = default) => Run(c => Read<ChangeNode>(c, null, "node", Key(scope, reference, pr)), ct);
    public Task<Result<ChangeNode>> ResolveNodeAsync(string scope, string reference, string title, long? pr = null, CancellationToken ct = default) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        var key = Key(scope, reference, pr);
        var old = Read<ChangeNode>(c, tx, "node", key);
        var node = old is null ? new ChangeNode(Guid.NewGuid().ToString("N"), scope, title, reference, pr) : old with
        {
            Title = title,
            Reference = reference
        };
        Put(c, tx, "node", key, node);
        tx.Commit();
        return node;
    }, ct);
    private static ChangeVersion SaveVersion(SqliteConnection c, SqliteTransaction tx, ChangeVersion input)
    {
        // Keep v1 identifiers stable through migration.
        var id = ApplicationStore.Key(input.ChangeId + "|" + input.BaseSha + "|" + input.HeadSha);
        var old = Read<ChangeVersion>(c, tx, "version", id);
        var version = old ?? input with
        {
            Id = id,
            MergeBase = null
        };
        using (var insert = Command(c, tx, """
            INSERT OR IGNORE INTO versions(
              id,
              json,
              owner,
              base_sha,
              head_sha,
              first_seen
            )
                           VALUES (
              $i,
              $j,
              $o,
              $b,
              $h,
              $t
            )
            """,
            ("$i", id), ("$j", JsonSerializer.Serialize(version)), ("$o", input.ChangeId), ("$b", input.BaseSha), ("$h", input.HeadSha), ("$t", input.ObservedAt.ToString("O"))))
            insert.ExecuteNonQuery();
        if (input.MergeBase is not null)
        {
            using var lookup = Command(c, tx, """
                SELECT merge_base
                  FROM materializations
                 WHERE version_id=$i
                """, ("$i", id));
            if (lookup.ExecuteScalar() is string existing && existing != input.MergeBase)
                throw new StackerException("An immutable version has conflicting merge-base evidence.");
            using var evidence = Command(c, tx, """
                  INSERT INTO materializations(
                  version_id,
                  merge_base
                )
                       VALUES (
                  $i,
                  $b
                )
                  ON CONFLICT (version_id)
                DO UPDATE SET merge_base=excluded.merge_base
                """, ("$i", id), ("$b", input.MergeBase));
            evidence.ExecuteNonQuery();
        }
        using (var latest = Command(c, tx, """
              SELECT version_id
                FROM observations
               WHERE owner=$o
            ORDER BY sequence DESC
               LIMIT 1
            """, ("$o", input.ChangeId)))
        {
            if (latest.ExecuteScalar() as string != id)
            {
                using var seen = Command(c, tx, """
                    INSERT INTO observations(
                      version_id,
                      owner,
                      seen_at
                    )
                         VALUES (
                      $i,
                      $o,
                      $t
                    )
                    """, ("$i", id), ("$o", input.ChangeId), ("$t", input.ObservedAt.ToString("O")));
                seen.ExecuteNonQuery();
            }
        }
        using var basis = Command(c, tx, """
            SELECT merge_base
              FROM materializations
             WHERE version_id=$i
            """, ("$i", id));
        return version with
        {
            MergeBase = basis.ExecuteScalar() as string
        };
    }
    public Task<Result<ChangeVersion>> ObserveAsync(ChangeVersion version, CancellationToken ct = default) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        var saved = SaveVersion(c, tx, version);
        tx.Commit();
        return saved;
    }, ct);
    private static IReadOnlyList<T> All<T>(SqliteConnection c, string kind, string? ownerField = null, string? owner = null)
    {
        var column = ownerField switch
        {
            "ChangeId" => "owner",
            "Scope" => "scope",
            null => null,
            _ => throw new ArgumentException("Unknown owner")
        };
        var table = Table(kind);
        var sql = column is null
            ? $"SELECT json FROM {table} ORDER BY rowid"
            : $"SELECT json FROM {table} WHERE {column}=$o ORDER BY rowid";
        using var query = Command(c, null, sql, ("$o", owner));
        using var reader = query.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
        return result;
    }
    public Task<Result<IReadOnlyList<ChangeVersion>>> VersionsAsync(string changeId, CancellationToken ct = default) => Run<IReadOnlyList<ChangeVersion>>(c =>
    {
        var result = new List<ChangeVersion>();
        foreach (var related in Related(c, changeId))
        {
            using var query = Command(c, null, """
                   SELECT v.json,
                          m.merge_base
                     FROM versions v
                LEFT JOIN materializations m
                       ON m.version_id=v.id
                    WHERE v.owner=$o
                 ORDER BY v.first_seen
                """, ("$o", related));
            using var reader = query.ExecuteReader();
            while (reader.Read())
                result.Add(JsonSerializer.Deserialize<ChangeVersion>(reader.GetString(0))! with
                {
                    ChangeId = changeId,
                    MergeBase = reader.IsDBNull(1) ? null : reader.GetString(1)
                });
        }
        return result.OrderBy(v => v.ObservedAt).ToArray();
    }, ct);
    public Task<Result<IReadOnlyList<ReviewCheckpoint>>> CheckpointsAsync(string changeId, CancellationToken ct = default) => Run<IReadOnlyList<ReviewCheckpoint>>(c => RelatedRecords<ReviewCheckpoint>(c, "checkpoint", changeId).GroupBy(r => r.Path).Select(g => g.OrderByDescending(r => r.ReviewedAt).First() with { ChangeId = changeId }).ToArray(), ct);
    public Task<Result<Unit>> SetCheckpointAsync(ReviewCheckpoint mark, bool remove, CancellationToken ct = default) => Run(c =>
    {
        if (remove)
        {
            foreach (var related in Related(c, mark.ChangeId))
            {
                using var cmd = Command(c, null, """
                    DELETE FROM checkpoints
                          WHERE id=$i
                    """, ("$i", related + "|" + mark.Path));
                cmd.ExecuteNonQuery();
            }
        }
        else
        {
            mark = mark with
            {
                ChangeId = Canonical(c, mark.ChangeId)
            };
            Put(c, null, "checkpoint", mark.ChangeId + "|" + mark.Path, mark);
        }
        return Unit.Value;
    }, ct);
}
