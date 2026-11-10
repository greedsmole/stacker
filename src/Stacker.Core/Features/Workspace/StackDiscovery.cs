namespace Stacker.Core;

/// <summary>Groups open pull requests into stacks using exact repository and branch relationships.</summary>
/// <remarks>This is the evidence-only fallback for a snapshot that has no stored change graph. Parent edges come
/// from the head and base refs the server reported, never from titles or dates, and the projection deliberately
/// matches <see cref="GraphDiscovery"/>: the same stack ids, names and shared-prefix flag, so the navigation tree keeps
/// its shape when a stored graph becomes available.
/// <para>The caller supplies the configured boundary branches, or the repository default branch when none are
/// configured, and surfaces any warning as a message instead of silently dropping pull requests.</para></remarks>
public static class StackDiscovery
{
    /// <summary>Finds PR parent edges, cuts edges at configured boundary branches, and reports ambiguous or cyclic paths.</summary>
    /// <param name="prs">Snapshot of open pull requests. An empty list yields no stacks, no others and no warnings.</param>
    /// <param name="boundaries">Base branch names that may not be crossed. A pull request whose base is one of these is
    /// the bottom of a stack and never gets a parent. The comparison is ordinal, so the names must match the reported
    /// base refs exactly; no prefix or case normalization is applied.</param>
    /// <returns>Complete stacks of two or more layers in base-first order, the pull requests that belong to none, and
    /// the ambiguity and cycle warnings, deduplicated.</returns>
    /// <remarks>Parent detection is an exact match: the parent is the one open pull request whose head repository id
    /// equals this pull request's base repository id and whose head ref equals its base ref. Ids rather than names keep
    /// a fork's same-named branch from linking to the upstream's stack. Titles, timestamps, and commit similarity are
    /// deliberately ignored: they are not reliable evidence of stacking. Zero candidates means the stack starts here;
    /// more than one means the base branch is ambiguous, and that pull request is blocked with a warning asking for a
    /// manual link, because guessing a parent would silently reorder someone's work. A base that matches a boundary
    /// skips the scan entirely, which is how a service branch is kept from becoming a layer.
    /// <para>Cycles are found by walking parent links from every pull request while remembering the numbers already
    /// visited, so mutually referencing pull requests terminate instead of looping. Every pull request on a revisited
    /// chain is blocked and each starting point adds a warning; the message names that pull request, so warnings from
    /// different starting points are distinct, and the final list is deduplicated. Blocked pull requests and the pull
    /// requests naming a blocked parent have their edge removed, so a path never runs through ambiguous or cyclic
    /// evidence.</para>
    /// <para>Each remaining leaf, meaning a pull request that nothing names as a parent, is walked down to its bottom
    /// and reversed into base-first order. A path shorter than two layers is a single pull request and is reported in
    /// <see cref="DiscoveryResult.Other"/> rather than as a one-layer stack, as is a pull request whose parent closed
    /// and disappeared from the snapshot. Stacks sharing a lower layer are not merged: every leaf keeps its own path
    /// and the shared layers are flagged through <see cref="DiscoveredStack.SharedPrefix"/>. The stack id is built from
    /// the top pull request's number and its title becomes the name, so a stack keeps its identity while its lower
    /// layers change.</para>
    /// <para>The parent search is quadratic in the snapshot size because every pull request whose base is not a
    /// boundary scans the whole list, which is why the boundary skip is also a cost saving.</para></remarks>
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
