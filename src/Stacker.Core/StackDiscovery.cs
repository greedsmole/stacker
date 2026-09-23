namespace Stacker.Core;

public static class StackDiscovery
{
    public static DiscoveryResult Discover(IReadOnlyList<PullRequest> prs, IReadOnlyCollection<string> boundaries)
    {
        var warnings = new List<string>();
        var parent = new Dictionary<long, PullRequest>();
        var blocked = new HashSet<long>();
        foreach (var pr in prs)
        {
            if (boundaries.Contains(pr.BaseRef, StringComparer.Ordinal)) continue;
            var candidates = prs.Where(p => p.Number != pr.Number && p.HeadRepositoryId == pr.BaseRepositoryId && p.HeadRef == pr.BaseRef).ToArray();
            if (candidates.Length == 1) parent[pr.Number] = candidates[0];
            else if (candidates.Length > 1) { blocked.Add(pr.Number); warnings.Add($"#{pr.Number}: multiple possible parents. Link this stack manually."); }
        }
        foreach (var pr in prs)
        {
            var seen = new HashSet<long>(); var current = pr;
            while (parent.TryGetValue(current.Number, out var next))
            {
                if (!seen.Add(current.Number)) { foreach (var id in seen) blocked.Add(id); warnings.Add($"#{pr.Number}: cyclic PR dependencies."); break; }
                current = next;
            }
        }
        foreach (var key in parent.Keys.ToArray()) if (blocked.Contains(key) || blocked.Contains(parent[key].Number)) parent.Remove(key);
        var parents = parent.Values.Select(p => p.Number).ToHashSet();
        var paths = new List<List<PullRequest>>();
        foreach (var leaf in prs.Where(p => !parents.Contains(p.Number) && !blocked.Contains(p.Number)))
        {
            var path = new List<PullRequest> { leaf }; var current = leaf;
            while (parent.TryGetValue(current.Number, out var next)) { path.Add(next); current = next; }
            path.Reverse(); if (path.Count >= 2) paths.Add(path);
        }
        var occurrences = paths.SelectMany(p => p).GroupBy(p => p.Number).ToDictionary(g => g.Key, g => g.Count());
        var stacks = paths.Select(p => new DiscoveredStack($"github-{p[^1].Number}", p[^1].Title, p, p.Any(pr => occurrences[pr.Number] > 1))).ToArray();
        var used = stacks.SelectMany(s => s.Layers).Select(p => p.Number).ToHashSet();
        return new(stacks, prs.Where(p => !used.Contains(p.Number)).ToArray(), warnings.Distinct().ToArray());
    }
}
