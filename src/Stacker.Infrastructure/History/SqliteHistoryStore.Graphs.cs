using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    private static GraphSnapshot? Latest(SqliteConnection c, SqliteTransaction? tx, string scope)
    {
        using var query = Command(c, tx, """
              SELECT json,
                     revision
                FROM graphs
               WHERE scope=$s
            ORDER BY revision DESC
               LIMIT 1
            """, ("$s", scope));
        using var reader = query.ExecuteReader();
        return reader.Read() ? JsonSerializer.Deserialize<GraphSnapshot>(reader.GetString(0))! with
        {
            Revision = reader.GetInt64(1)
        } : null;
    }
    private static GraphSnapshot SaveGraph(SqliteConnection c, SqliteTransaction tx, GraphSnapshot graph, long? expected)
    {
        var prior = Latest(c, tx, graph.Scope);
        var revision = prior?.Revision ?? 0;
        if (expected is not null && revision != expected)
            throw new StackerException(Problem.Of(ProblemCodes.StoreStaleRevision, "Workspace changed. Reload before applying this change."));
        if (prior is not null && JsonSerializer.Serialize(prior with
        {
            ObservedAt = graph.ObservedAt,
            Revision = 0
        }) == JsonSerializer.Serialize(graph with
        {
            Revision = 0
        }))
            return prior;
        var saved = graph with
        {
            Revision = revision + 1
        };
        var id = Guid.NewGuid().ToString("N");
        using (var insert = Command(c, tx, """
            INSERT INTO graphs(
              id,
              json,
              scope,
              revision
            )
                 VALUES (
              $i,
              $j,
              $s,
              $r
            )
            """, ("$i", id), ("$j", JsonSerializer.Serialize(saved)), ("$s", graph.Scope), ("$r", saved.Revision)))
            insert.ExecuteNonQuery();
        foreach (var node in graph.Nodes)
        {
            using var member = Command(c, tx, """
                INSERT INTO graph_members(
                  graph_id,
                  node_id,
                  version_id
                )
                     VALUES (
                  $g,
                  $n,
                  $v
                )
                """, ("$g", id), ("$n", node.Id), ("$v", graph.Versions.GetValueOrDefault(node.Id)));
            member.ExecuteNonQuery();
        }
        return saved;
    }
    public Task<Result<GraphSnapshot?>> LatestGraphAsync(string scope, CancellationToken ct = default) => Run(c => Latest(c, null, scope), ct);
    public Task<Result<GraphSnapshot>> ApplyObservationBatchAsync(HistoryObservationBatch batch, CancellationToken ct = default) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        foreach (var node in batch.Graph.Nodes)
            Put(c, tx, "node", NodeKey(node), node);
        foreach (var version in batch.Versions)
            SaveVersion(c, tx, version);
        var saved = SaveGraph(c, tx, batch.Graph, batch.ExpectedRevision);
        tx.Commit();
        return saved;
    }, ct);
    public Task<Result<Unit>> SaveGraphAsync(GraphSnapshot graph, CancellationToken ct = default) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        SaveGraph(c, tx, graph, graph.Revision > 0 ? graph.Revision : null);
        tx.Commit();
        return Unit.Value;
    }, ct);
    public Task<Result<IReadOnlyList<GraphSnapshot>>> GraphsAsync(string scope, CancellationToken ct = default) => Run<IReadOnlyList<GraphSnapshot>>(c => All<GraphSnapshot>(c, "graph", "Scope", scope), ct);
}
