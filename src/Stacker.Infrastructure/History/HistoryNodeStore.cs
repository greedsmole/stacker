using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    public Task<Result<IReadOnlyList<ReviewConcern>>> ConcernsAsync(string changeId, CancellationToken ct = default) =>
        Run<IReadOnlyList<ReviewConcern>>(c => RelatedRecords<ReviewConcern>(c,"concern",changeId).GroupBy(r=>r.Path).Select(g=>new ReviewConcern(changeId,g.Key,string.Join("; ",g.Select(r=>r.Text).Distinct()))).ToArray(), ct);
    public Task<Result<Unit>> SetConcernAsync(ReviewConcern concern, bool remove, CancellationToken ct = default) => Run(c =>
    {
        var key = concern.ChangeId + "|" + concern.Path;
        if (remove) { foreach(var related in Related(c,concern.ChangeId)){using var cmd=Command(c,null,"DELETE FROM concerns WHERE id=$i",("$i",related+"|"+concern.Path));cmd.ExecuteNonQuery();} }
        else {concern=concern with {ChangeId=Canonical(c,concern.ChangeId)};Put(c,null,"concern",concern.ChangeId+"|"+concern.Path,concern);}
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
        using var command = Command(c, tx, "UPDATE nodes SET id=$n,json=$j,revision=revision+1 WHERE id=$i AND entity_id=$e", ("$n", NodeKey(rebound)),("$j",System.Text.Json.JsonSerializer.Serialize(rebound)),("$i",NodeKey(node)),("$e",node.Id));
        if(command.ExecuteNonQuery()!=1)throw new StackerException("This change was renamed in another session. Reload its history.");
        tx.Commit(); return Unit.Value;
    }, ct);
}
