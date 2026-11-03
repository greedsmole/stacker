using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    public Task<Result<IReadOnlyList<ReviewConcern>>> ConcernsAsync(string changeId, CancellationToken ct = default) =>
        Run<IReadOnlyList<ReviewConcern>>(c => All<ReviewConcern>(c, "concern", "ChangeId", changeId), ct);
    public Task<Result<Unit>> SetConcernAsync(ReviewConcern concern, bool remove, CancellationToken ct = default) => Run(c =>
    {
        var key = concern.ChangeId + "|" + concern.Path;
        if (remove) { using var cmd = Command(c, null, "DELETE FROM documents WHERE kind='concern' AND id=$i", ("$i", key)); cmd.ExecuteNonQuery(); }
        else Put(c, null, "concern", key, concern);
        return Unit.Value;
    }, ct);
    private static string NodeKey(ChangeNode node) => node.Scope + "|" + (node.PullRequest is null ? node.Reference : "pr:" + node.PullRequest);
    public Task<Result<Unit>> SaveNodeAsync(ChangeNode node, CancellationToken ct = default) => Run(c =>
    { Put(c, null, "node", NodeKey(node), node); return Unit.Value; }, ct);
    public Task<Result<Unit>> RebindLocalNodeAsync(ChangeNode node, string reference, CancellationToken ct = default) => Run(c =>
    {
        if (node.PullRequest is not null || !node.Scope.StartsWith("local:", StringComparison.Ordinal) ||
            !reference.StartsWith("refs/", StringComparison.Ordinal) || reference.Any(char.IsControl))
            throw new StackerException("Select a local full ref for the existing change.");
        using var tx = c.BeginTransaction();
        var rebound = node with { Reference = reference, State = "Open" };
        var existing = Read<ChangeNode>(c, tx, "node", NodeKey(rebound));
        if (existing is not null && existing.Id != node.Id) throw new StackerException("That ref already has its own history. Histories cannot be merged implicitly.");
        using var command = Command(c, tx, "DELETE FROM documents WHERE kind='node' AND id=$i", ("$i", NodeKey(node)));
        command.ExecuteNonQuery(); Put(c, tx, "node", NodeKey(rebound), rebound); tx.Commit(); return Unit.Value;
    }, ct);
}
