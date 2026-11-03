using Stacker.Core.Framework;
namespace Stacker.Core;

public sealed class ChangeGraphService(IHistoryStore store, IHistoryRemoteReader? remote = null)
{
    public async Task<GraphSnapshot> ObserveLocalAsync(RepositorySnapshot repo, IReadOnlyList<StackDefinition> stacks, CancellationToken ct)
    {
        var scope = repo.Scope;
        var currentVersions = new Dictionary<string, string>();
        var observed = new List<ChangeVersion>();
        var nodes = new Dictionary<string, ChangeNode>(); var edges = new List<DependencyEdge>();
        foreach (var stack in stacks)
        {
            ChangeNode? previous = null;
            foreach (var reference in stack.Branches)
            {
                var node = (await store.FindNodeAsync(scope, reference, ct: ct)).OrThrow() ?? new ChangeNode(StableIdentity.For(scope, reference), scope, reference.Replace("refs/heads/", ""), reference);
                nodes[node.Id] = node with {State=repo.Refs.Any(r => r.Name == reference)?"Open":"Missing"};
                if (previous is not null) edges.Add(new(previous.Id, node.Id, "local-definition"));
                var head = repo.Refs.FirstOrDefault(r => r.Name == reference)?.Sha;
                var parentRef = previous?.Reference ?? stack.Base;
                var basis = repo.Refs.FirstOrDefault(r => r.Name == parentRef)?.Sha;
                if (head is not null && basis is not null)
                {
                    var version = new ChangeVersion(StableIdentity.For(node.Id+"|"+basis+"|"+head), node.Id, basis, head, null, DateTimeOffset.UtcNow);
                    observed.Add(version); currentVersions[node.Id] = version.Id;
                }
                previous = node;
            }
        }
        return await SaveAsync(scope, nodes.Values.ToArray(), edges, ct, currentVersions: currentVersions, observed: observed);
    }
    public async Task<GraphSnapshot> ObserveRemoteAsync(GitHubSnapshot snapshot, IReadOnlyCollection<string> boundaries, CancellationToken ct)
    {
        var scope = snapshot.Context.Identity;
        var currentVersions = new Dictionary<string, string>();
        var observed = new List<ChangeVersion>();
        var nodes = new Dictionary<long, ChangeNode>(); var edges = new List<DependencyEdge>(); var warnings = new List<string>();
        foreach (var pr in snapshot.PullRequests)
        {
            nodes[pr.Number] = ((await store.FindNodeAsync(scope, pr.HeadRef, pr.Number, ct)).OrThrow() ?? new ChangeNode(StableIdentity.For(scope, "pr:"+pr.Number), scope, pr.Title, pr.HeadRef, pr.Number)) with { State = pr.State, Title = pr.Title, Reference = pr.HeadRef };
            var version = new ChangeVersion(StableIdentity.For(nodes[pr.Number].Id+"|"+pr.BaseSha+"|"+pr.HeadSha), nodes[pr.Number].Id, pr.BaseSha, pr.HeadSha, null, snapshot.UpdatedAt);
            observed.Add(version); currentVersions[nodes[pr.Number].Id] = version.Id;
        }
        foreach (var pr in snapshot.PullRequests.Where(p => !boundaries.Contains(p.BaseRef)))
        {
            var candidates = snapshot.PullRequests.Where(p => p.Number != pr.Number && p.HeadRepositoryId == pr.BaseRepositoryId && p.HeadRef == pr.BaseRef).ToArray();
            if (candidates.Length == 1) edges.Add(new(nodes[candidates[0].Number].Id, nodes[pr.Number].Id, "github"));
            else if (candidates.Length > 1) warnings.Add($"#{pr.Number}: ambiguous parent");
        }
        var prior = (await store.LatestGraphAsync(scope, ct)).OrThrow();
        if (prior is not null) foreach (var node in prior.Nodes.Where(n => n.PullRequest is not null && !nodes.ContainsKey(n.PullRequest.Value)))
        {
            var state = node.State;
            if (state is not ("Merged" or "Closed"))
            {
                var read = remote is null ? null : await remote.HistoricalPullRequestAsync(snapshot.Context, node.PullRequest!.Value, ct);
                state = read?.IsOk == true ? read.Value!.State : "Unknown";
            }
            var retained = node with { State = state };
            nodes[node.PullRequest!.Value] = retained;
        }
        return await SaveAsync(scope, nodes.Values.ToArray(), edges, ct, warnings, currentVersions, observed);
    }
    private async Task<GraphSnapshot> SaveAsync(string scope, IReadOnlyList<ChangeNode> nodes, IEnumerable<DependencyEdge> input,
        CancellationToken ct, List<string>? warnings = null, Dictionary<string, string>? currentVersions = null, IReadOnlyList<ChangeVersion>? observed = null)
    {
        warnings ??= []; var edges = new List<DependencyEdge>(); var blocked = new HashSet<string>();
        var prior = (await store.LatestGraphAsync(scope, ct)).OrThrow();
        var manual = prior?.Edges.Where(e => e.Source == "explicit" && nodes.Any(n => n.Id == e.Parent) && nodes.Any(n => n.Id == e.Child)).ToArray() ?? [];
        input = manual.Concat(input.Where(e => !manual.Any(m => m.Child == e.Child)));
        foreach (var edge in input.Distinct())
        {
            if (blocked.Contains(edge.Parent) || blocked.Contains(edge.Child)) continue;
            if (edge.Parent == edge.Child || Descendants(edge.Child, edges).Contains(edge.Parent))
                {
                warnings.Add("Cyclic dependency rejected: " + edge.Child);
                blocked.UnionWith(Descendants(edge.Child, edges)); blocked.Add(edge.Child); blocked.Add(edge.Parent);
                edges.RemoveAll(e => blocked.Contains(e.Parent) || blocked.Contains(e.Child));
            }
            else edges.Add(edge);
        }
        var versions = currentVersions ?? new Dictionary<string, string>();
        foreach (var node in nodes)
            if (!versions.ContainsKey(node.Id) && prior?.Versions.TryGetValue(node.Id, out var previous) == true) versions[node.Id] = previous;
        var graph = new GraphSnapshot(scope, DateTimeOffset.UtcNow, nodes, edges, warnings) { Versions = versions };
        return (await store.ApplyObservationBatchAsync(new(graph, observed ?? [], prior?.Revision ?? 0), ct)).OrThrow();
    }
    public static HashSet<string> Descendants(string node, IEnumerable<DependencyEdge> edges)
    {
        var lookup = edges.ToLookup(e => e.Parent); var result = new HashSet<string>(); var queue = new Queue<string>(); queue.Enqueue(node);
        while (queue.TryDequeue(out var current)) foreach (var edge in lookup[current]) if (result.Add(edge.Child)) queue.Enqueue(edge.Child);
        return result;
    }
}
