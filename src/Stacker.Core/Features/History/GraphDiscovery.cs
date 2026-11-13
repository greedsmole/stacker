namespace Stacker.Core;

/// <summary>Projects the active part of the stored graph into navigation paths; closed nodes remain in graph history.</summary>
/// <remarks>Unlike <see cref="StackDiscovery"/>, which re-derives parent edges from the live pull request set on every refresh,
/// this projects the edges that were already accepted into a graph revision — including manual ones. Only nodes whose pull
/// request is still in the open snapshot participate, so a merged or closed parent disappears from navigation while the stored
/// revision keeps it for the historical view. A child with several parents is ambiguous and gets no parent at all: it becomes
/// the root of a shorter path, so the chain above the ambiguity is never guessed and only the part below it stays navigable.</remarks>
public static class GraphDiscovery
{
    /// <summary>Builds stacks, paths and leftover changes from one graph revision and one open snapshot.</summary>
    /// <remarks>Edges are filtered to nodes that survive the open-snapshot test, then only children with exactly one incoming
    /// edge get a parent; the rest keep no parent and are therefore roots. Every node that is never a parent is treated as a leaf,
    /// walked up to its root with a visited set that stops a stored cycle, and reversed into base-to-tip order. Paths with a
    /// single layer are discarded because one pull request is not a stack, and their changes reappear in the leftover list.
    /// Occurrences are counted across all kept paths: a pull request seen more than once means two stacks share a foundation,
    /// which is what <see cref="DiscoveredStack.SharedPrefix"/> reports. A stack's identity is built from its topmost pull request
    /// number and its name from that pull request's title, so re-observing the same shape keeps the same navigation keys.</remarks>
    /// <param name="snapshot">Current open pull requests and their titles; supplies both the filter and the displayed objects.</param>
    /// <param name="graph">Stored revision whose nodes, edges and warnings are projected.</param>
    /// <returns>Discovered paths, the snapshot changes not used by any path, and the revision's warnings passed through unchanged.</returns>
    public static DiscoveryResult Discover(GitHubSnapshot snapshot, GraphSnapshot graph)
    {
        var active = snapshot.PullRequests.ToDictionary(p => p.Number);
        var nodes = graph.Nodes.Where(n => n.PullRequest is { } number && active.ContainsKey(number)).ToDictionary(n => n.Id);
        var edges = graph.Edges.Where(e => nodes.ContainsKey(e.Parent) && nodes.ContainsKey(e.Child)).ToArray();
        var parents = edges.GroupBy(e => e.Child).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Parent);
        var nonLeaves = parents.Values.ToHashSet();
        var paths = new List<IReadOnlyList<PullRequest>>();
        foreach (var leaf in nodes.Keys.Where(n => !nonLeaves.Contains(n)))
        {
            var seen = new HashSet<string>();
            var path = new List<PullRequest>();
            var current = leaf;
            while (seen.Add(current))
            {
                path.Add(active[nodes[current].PullRequest!.Value]);
                if (!parents.TryGetValue(current, out var parent))
                    break;
                current = parent;
            }
            path.Reverse();
            if (path.Count > 1)
                paths.Add(path);
        }
        var occurrences = paths.SelectMany(p => p).GroupBy(p => p.Number).ToDictionary(g => g.Key, g => g.Count());
        var stacks = paths.Select(p => new DiscoveredStack("github-" + p[^1].Number, p[^1].Title, p, p.Any(pr => occurrences[pr.Number] > 1))).ToArray();
        var used = stacks.SelectMany(s => s.Layers).Select(p => p.Number).ToHashSet();
        return new(stacks, snapshot.PullRequests.Where(p => !used.Contains(p.Number)).ToArray(), graph.Warnings);
    }
}
