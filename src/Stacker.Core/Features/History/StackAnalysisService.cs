namespace Stacker.Core;

/// <summary>Checks every edge of one stored graph revision for stale parents, overlapping paths and textual conflicts.</summary>
/// <remarks>Analysis runs against the versions pinned in the revision, not the newest ones, so polling metadata again cannot
/// change a report the reviewer is already reading. It needs the scope's objects: a read lease is taken up front and the call
/// fails with a user-facing problem when the history was removed or the commits are missing. One edge costs several Git reads,
/// so cancellation is checked between edges rather than only at the start. Findings are attributed to the child of the edge —
/// that is the layer a restack would move — and one edge can produce several findings. Everything here is textual: a clean
/// result does not predict that a restack applies, and a conflict set is a hint rather than the restack outcome. Every edge is
/// expected to reference nodes of the same revision; a dangling edge is an error in the caller, not an analysis finding.</remarks>
public sealed class StackAnalysisService(IHistoryStore store, IHistoryGit objects, IGitRepositoryReader git)
{
    /// <summary>Produces the findings for every edge of a graph revision.</summary>
    /// <remarks>Per edge, the parent and child versions are the ones the revision pins; they are resolved once for all nodes
    /// before the loop starts, so the loop itself performs only Git work. A missing pin or missing merge-base
    /// evidence gives <c>Unavailable</c> with instructions to open both layers, because the objects were never captured. Next
    /// comes the unique-ancestor check: zero or several merge bases give <c>Unavailable</c>. The child is then reported
    /// <c>StaleParent</c> when the merge base of the two heads is not the parent's head, which means the child does not contain
    /// the parent's current tip and is still stacked on older code. Path overlap intersects the two layers' own patch path sets —
    /// old and new sides — and is informational only: touching the same file is not a conflict. A trial merge finally reports
    /// <c>Conflict</c> for the paths that do not merge textually. A <see cref="StackerException"/> from any Git step becomes an
    /// <c>Unavailable</c> finding for that edge, so one unreadable layer does not abort the report.</remarks>
    /// <param name="repository">Local repository that owns the pinned commits; passed explicitly for the lease, the trial merge
    /// and the reads.</param>
    /// <param name="graph">Revision to analyse; its <see cref="GraphSnapshot.Versions"/> pin the inputs.</param>
    /// <param name="ct">Cancellation, checked before each edge because a single edge performs several Git operations.</param>
    /// <returns>The same revision with the findings found on its edges, in edge order.</returns>
    public async Task<StackAnalysisResult> AnalyzeAsync(RepositoryIdentity repository, GraphSnapshot graph, CancellationToken ct = default)
    {
        using var lease = (await objects.AcquireAsync(repository, graph.Scope, ct)).OrThrow(); var root = lease.Root; var issues = new List<StackAnalysisIssue>();
        var versions = new Dictionary<string, ChangeVersion?>();
        foreach (var node in graph.Nodes) versions[node.Id] = (await store.VersionsAsync(node.Id, ct)).OrThrow().FirstOrDefault(v => graph.Versions.GetValueOrDefault(node.Id) == v.Id);
        foreach (var edge in graph.Edges)
        {
            ct.ThrowIfCancellationRequested();
            var parent = versions[edge.Parent]; var child = versions[edge.Child];
            if (parent?.MergeBase is null || child?.MergeBase is null)
            { issues.Add(new(edge.Child, "Unavailable", "Open both layers to capture their objects before analysis.", [])); continue; }
            try
            {
                var bases = (await git.MergeBasesAsync(root, parent.HeadSha, child.HeadSha, ct)).OrThrow();
                if (bases.Count != 1) { issues.Add(new(edge.Child, "Unavailable", "No unique common ancestor.", [])); continue; }
                if (bases[0] != parent.HeadSha)
                    issues.Add(new(edge.Child, "StaleParent", "Layer does not contain the current parent. Descendants may need attention.", []));
                var a = (await git.FilesAsync(root, parent.MergeBase, parent.HeadSha, ct)).OrThrow();
                var b = (await git.FilesAsync(root, child.MergeBase, child.HeadSha, ct)).OrThrow();
                var overlap = a.SelectMany(f => new[] { f.OldPath, f.NewPath }).Intersect(b.SelectMany(f => new[] { f.OldPath, f.NewPath })).ToArray();
                if (overlap.Length > 0) issues.Add(new(edge.Child, "Overlap", "Layers touch common files; this alone is not a conflict.", overlap));
                var conflicts = (await objects.MergeAsync(repository, parent.HeadSha, child.HeadSha, ct)).OrThrow();
                if (conflicts.Count > 0) issues.Add(new(edge.Child, "Conflict", "Trial merge has textual conflicts (not a restack prediction).", conflicts));
            }
            catch (StackerException ex) { issues.Add(new(edge.Child, "Unavailable", ex.Message, [])); }
        }
        return new(graph, issues);
    }
}
