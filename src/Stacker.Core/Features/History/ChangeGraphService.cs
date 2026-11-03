using Stacker.Core.Framework;
namespace Stacker.Core;

public sealed class ChangeGraphService(IHistoryStore store, IHistoryRemoteReader? remote = null)
{
    public async Task<GraphSnapshot> ObserveLocalAsync(RepositorySnapshot repo, IReadOnlyList<StackDefinition> stacks, CancellationToken ct)
    {
        var scope = "local:" + repo.Root;
        var currentVersions = new Dictionary<string, string>();
        var nodes = new Dictionary<string, ChangeNode>(); var edges = new List<DependencyEdge>();
        foreach (var stack in stacks)
        {
            ChangeNode? previous = null;
            foreach (var reference in stack.Branches)
            {
                var node = (await store.ResolveNodeAsync(scope, reference, reference.Replace("refs/heads/", ""), ct: ct)).OrThrow();
                nodes[node.Id] = repo.Refs.Any(r => r.Name == reference) ? node : node with { State = "Missing" };
                if (previous is not null) edges.Add(new(previous.Id, node.Id, "local-definition"));
                var head = repo.Refs.FirstOrDefault(r => r.Name == reference)?.Sha;
                var parentRef = previous?.Reference ?? stack.Base;
                var basis = repo.Refs.FirstOrDefault(r => r.Name == parentRef)?.Sha;
                if (head is not null && basis is not null) currentVersions[node.Id] = (await store.ObserveAsync(new("", node.Id, basis, head, null, DateTimeOffset.UtcNow), ct)).OrThrow().Id;
                previous = node;
            }
        }
        return await SaveAsync(scope, nodes.Values.ToArray(), edges, ct, currentVersions: currentVersions);
    }
    public async Task<GraphSnapshot> ObserveRemoteAsync(GitHubSnapshot snapshot, IReadOnlyCollection<string> boundaries, CancellationToken ct)
    {
        var scope = snapshot.Context.Identity;
        var currentVersions = new Dictionary<string, string>();
        var nodes = new Dictionary<long, ChangeNode>(); var edges = new List<DependencyEdge>(); var warnings = new List<string>();
        foreach (var pr in snapshot.PullRequests)
        {
            nodes[pr.Number] = (await store.ResolveNodeAsync(scope, pr.HeadRef, pr.Title, pr.Number, ct)).OrThrow() with { State = pr.State };
            (await store.SaveNodeAsync(nodes[pr.Number], ct)).OrThrow();
            currentVersions[nodes[pr.Number].Id] = (await store.ObserveAsync(new("", nodes[pr.Number].Id, pr.BaseSha, pr.HeadSha, null, snapshot.UpdatedAt), ct)).OrThrow().Id;
        }
        foreach (var pr in snapshot.PullRequests.Where(p => !boundaries.Contains(p.BaseRef)))
        {
            var candidates = snapshot.PullRequests.Where(p => p.Number != pr.Number && p.HeadRepositoryId == pr.BaseRepositoryId && p.HeadRef == pr.BaseRef).ToArray();
            if (candidates.Length == 1) edges.Add(new(nodes[candidates[0].Number].Id, nodes[pr.Number].Id, "github"));
            else if (candidates.Length > 1) warnings.Add($"#{pr.Number}: ambiguous parent");
        }
        var prior = (await store.GraphsAsync(scope, ct)).OrThrow().LastOrDefault();
        if (prior is not null) foreach (var node in prior.Nodes.Where(n => n.PullRequest is not null && !nodes.ContainsKey(n.PullRequest.Value)))
        {
            var state = node.State;
            if (state is not ("Merged" or "Closed"))
            {
                var read = remote is null ? null : await remote.HistoricalPullRequestAsync(snapshot.Context, node.PullRequest!.Value, ct);
                state = read?.IsOk == true ? read.Value!.State : "Unknown";
            }
            var retained = node with { State = state };
            nodes[node.PullRequest!.Value] = retained; (await store.SaveNodeAsync(retained, ct)).OrThrow();
        }
        return await SaveAsync(scope, nodes.Values.ToArray(), edges, ct, warnings, currentVersions);
    }
    private async Task<GraphSnapshot> SaveAsync(string scope, IReadOnlyList<ChangeNode> nodes, IEnumerable<DependencyEdge> input,
        CancellationToken ct, List<string>? warnings = null, Dictionary<string, string>? currentVersions = null)
    {
        warnings ??= []; var edges = new List<DependencyEdge>(); var blocked = new HashSet<string>();
        var prior = (await store.GraphsAsync(scope, ct)).OrThrow().LastOrDefault();
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
        {
            var latest = (await store.VersionsAsync(node.Id, ct)).OrThrow().LastOrDefault();
            if (latest is not null && !versions.ContainsKey(node.Id)) versions[node.Id] = latest.Id;
        }
        var graph = new GraphSnapshot(scope, DateTimeOffset.UtcNow, nodes, edges, warnings) { Versions = versions };
        (await store.SaveGraphAsync(graph, ct)).OrThrow(); return graph;
    }
    public static HashSet<string> Descendants(string node, IEnumerable<DependencyEdge> edges)
    {
        var lookup = edges.ToLookup(e => e.Parent); var result = new HashSet<string>(); var queue = new Queue<string>(); queue.Enqueue(node);
        while (queue.TryDequeue(out var current)) foreach (var edge in lookup[current]) if (result.Add(edge.Child)) queue.Enqueue(edge.Child);
        return result;
    }
}
