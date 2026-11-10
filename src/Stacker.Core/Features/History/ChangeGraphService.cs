using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Turns one repository or GitHub observation into a new persisted graph revision with pinned versions.</summary>
/// <remarks>Both entry points build a candidate node and edge set and then hand it to <see cref="SaveAsync"/>; keeping
/// explicit-edge precedence, cycle rejection and version carry-forward in that single place is what stops local and remote
/// observations from disagreeing about the same child. Nodes are looked up before they are created, so identity survives
/// renames and repeated polls. Local observation mirrors the declared stack definitions; remote observation infers parents
/// from pull request refs, records ambiguity as a warning and retains nodes that left the open snapshot. Every call returns
/// the saved revision, and earlier revisions stay readable.</remarks>
public sealed class ChangeGraphService(IHistoryStore store, IHistoryRemoteReader? remote = null)
{
    /// <summary>Observes the declared local stacks and records one graph revision for them.</summary>
    /// <remarks>Each branch of each stack becomes a node; the first layer is parented by the stack's base ref and every later
    /// layer by the branch before it, with source <c>local-definition</c>, so the revision reproduces the declared order
    /// exactly. A node is <c>Open</c> while its ref exists in this snapshot and <c>Missing</c> otherwise. A version is observed
    /// only when both the layer ref and its basis ref resolve to a SHA: the version's base is the parent's current tip, and
    /// merge-base evidence is left empty here because it is captured when the layer is opened for comparison. Definitions
    /// removed from the stack file therefore vanish from the next revision while older revisions still contain them.</remarks>
    /// <param name="repo">Snapshot whose refs supply the head and basis SHAs; its scope owns the history.</param>
    /// <param name="stacks">User-authored definitions that determine the order to record.</param>
    /// <param name="ct">Cancellation for the store reads and the final write.</param>
    /// <returns>The saved revision; warnings can still appear when declared edges are rejected as cyclic.</returns>
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
                nodes[node.Id] = node with { State = repo.Refs.Any(r => r.Name == reference) ? "Open" : "Missing" };
                if (previous is not null) edges.Add(new(previous.Id, node.Id, "local-definition"));
                var head = repo.Refs.FirstOrDefault(r => r.Name == reference)?.Sha;
                var parentRef = previous?.Reference ?? stack.Base;
                var basis = repo.Refs.FirstOrDefault(r => r.Name == parentRef)?.Sha;
                if (head is not null && basis is not null)
                {
                    var version = new ChangeVersion(StableIdentity.For(node.Id + "|" + basis + "|" + head), node.Id, basis, head, null, DateTimeOffset.UtcNow);
                    observed.Add(version); currentVersions[node.Id] = version.Id;
                }
                previous = node;
            }
        }
        return await SaveAsync(scope, nodes.Values.ToArray(), edges, ct, currentVersions: currentVersions, observed: observed);
    }
    /// <summary>Observes a page-complete GitHub snapshot and records one graph revision for it.</summary>
    /// <remarks>Every open pull request becomes a node whose state, title and head ref are refreshed from the snapshot. The
    /// version's base and head are the pull request's SHA pair and its observation time is the snapshot timestamp, so
    /// observing the same snapshot twice yields the same versions. A parent is inferred only when another pull request's head
    /// ref, in the repository this pull request targets, is this pull request's base ref — a head branch that lives in a fork
    /// never matches — and exactly one candidate is required: several candidates produce the warning
    /// <c>#&lt;number&gt;: ambiguous parent</c> and no edge, so the layer becomes a root instead of a guess. Pull requests based
    /// on a boundary branch are roots by definition. Nodes from the prior revision that left the open snapshot are retained:
    /// <c>Merged</c> and <c>Closed</c> are final and kept as they are, anything else is re-read once through
    /// <see cref="IHistoryRemoteReader"/> and becomes <c>Unknown</c> when that read fails, because absence from a list of open
    /// pull requests is not proof of a state.</remarks>
    /// <param name="snapshot">Timestamped pull request snapshot; its context identity is the history scope.</param>
    /// <param name="boundaries">Base branch names, as GitHub reports them without the ref prefix, that terminate a stack.</param>
    /// <param name="ct">Cancellation for the historical read and the final write.</param>
    /// <returns>The saved revision, whose warnings include any ambiguity found while inferring parents.</returns>
    public async Task<GraphSnapshot> ObserveRemoteAsync(GitHubSnapshot snapshot, IReadOnlyCollection<string> boundaries, CancellationToken ct)
    {
        var scope = snapshot.Context.Identity;
        var currentVersions = new Dictionary<string, string>();
        var observed = new List<ChangeVersion>();
        var nodes = new Dictionary<long, ChangeNode>(); var edges = new List<DependencyEdge>(); var warnings = new List<string>();
        foreach (var pr in snapshot.PullRequests)
        {
            nodes[pr.Number] = ((await store.FindNodeAsync(scope, pr.HeadRef, pr.Number, ct)).OrThrow() ?? new ChangeNode(StableIdentity.For(scope, "pr:" + pr.Number), scope, pr.Title, pr.HeadRef, pr.Number)) with { State = pr.State, Title = pr.Title, Reference = pr.HeadRef };
            var version = new ChangeVersion(StableIdentity.For(nodes[pr.Number].Id + "|" + pr.BaseSha + "|" + pr.HeadSha), nodes[pr.Number].Id, pr.BaseSha, pr.HeadSha, null, snapshot.UpdatedAt);
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
    /// <summary>Finalizes a candidate node and edge set into one persisted revision.</summary>
    /// <remarks>The sequence matters. Prior explicit edges whose endpoints still exist are kept first and win over any input
    /// edge for the same child, so a manual relationship is never silently replaced by inference. Duplicate edges are
    /// collapsed, then each remaining edge is accepted unless it is a self-edge or would close a cycle: a rejected edge adds
    /// <c>Cyclic dependency rejected: &lt;child&gt;</c>, blocks the child, the parent and the child's descendants, and drops
    /// edges already accepted that touch a blocked node — a poisoned region is excluded whole instead of half-applied.
    /// Version pins for nodes that received no new version are carried forward from the prior revision, so a metadata-only
    /// observation does not unpin older code. The write is a compare-and-swap against the prior revision, so a concurrent
    /// observation fails instead of overwriting it. Only explicit prior edges survive; inferred edges are recomputed from the
    /// current observation, so a retained node can lose its inferred parent.</remarks>
    /// <param name="scope">History scope the revision belongs to.</param>
    /// <param name="nodes">Candidate nodes, as built by the calling observation.</param>
    /// <param name="input">Candidate edges; a prior explicit edge takes precedence for the same child.</param>
    /// <param name="ct">Cancellation for the store reads and the write.</param>
    /// <param name="warnings">Mutated list that already holds the caller's warnings; cycle warnings are appended to it.</param>
    /// <param name="currentVersions">Node identity to newly observed version identity; nodes absent here keep the prior pin.</param>
    /// <param name="observed">Versions to persist with this revision; empty for a metadata-only observation.</param>
    /// <returns>The saved revision, carrying the store-assigned revision number.</returns>
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
    /// <summary>Collects every change reachable by following child edges from one node.</summary>
    /// <remarks>Used for cycle rejection when a revision is saved and again by the dependency editor before it stores a manual
    /// parent. The node itself is not part of the result unless the supplied edges already contain a cycle; the breadth-first
    /// walk visits each child once, so a cyclic input cannot loop forever.</remarks>
    /// <param name="node">Identity to start from.</param>
    /// <param name="edges">Edges to follow; only their parent-to-child direction is used.</param>
    /// <returns>Identities of all transitive children, empty when the node has none.</returns>
    public static HashSet<string> Descendants(string node, IEnumerable<DependencyEdge> edges)
    {
        var lookup = edges.ToLookup(e => e.Parent); var result = new HashSet<string>(); var queue = new Queue<string>(); queue.Enqueue(node);
        while (queue.TryDequeue(out var current)) foreach (var edge in lookup[current]) if (result.Add(edge.Child)) queue.Enqueue(edge.Child);
        return result;
    }
}
