namespace Stacker.Core;

/// <summary>Projects the active part of the stored graph into navigation paths; closed nodes remain in graph history.</summary>
public static class GraphDiscovery
{
    public static DiscoveryResult Discover(GitHubSnapshot snapshot, GraphSnapshot graph)
    {
        var active = snapshot.PullRequests.ToDictionary(p => p.Number);
        var nodes = graph.Nodes.Where(n => n.PullRequest is { } number && active.ContainsKey(number)).ToDictionary(n => n.Id);
        var edges = graph.Edges.Where(e => nodes.ContainsKey(e.Parent) && nodes.ContainsKey(e.Child)).ToArray();
        var parents = edges.GroupBy(e => e.Child).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Parent);
        var nonLeaves = parents.Values.ToHashSet(); var paths = new List<IReadOnlyList<PullRequest>>();
        foreach (var leaf in nodes.Keys.Where(n => !nonLeaves.Contains(n)))
        {
            var seen = new HashSet<string>(); var path = new List<PullRequest>(); var current = leaf;
            while (seen.Add(current))
            {
                path.Add(active[nodes[current].PullRequest!.Value]);
                if (!parents.TryGetValue(current, out var parent)) break;
                current = parent;
            }
            path.Reverse(); if (path.Count > 1) paths.Add(path);
        }
        var occurrences = paths.SelectMany(p => p).GroupBy(p => p.Number).ToDictionary(g => g.Key, g => g.Count());
        var stacks = paths.Select(p => new DiscoveredStack("github-" + p[^1].Number, p[^1].Title, p, p.Any(pr => occurrences[pr.Number] > 1))).ToArray();
        var used = stacks.SelectMany(s => s.Layers).Select(p => p.Number).ToHashSet();
        return new(stacks, snapshot.PullRequests.Where(p => !used.Contains(p.Number)).ToArray(), graph.Warnings);
    }
}
