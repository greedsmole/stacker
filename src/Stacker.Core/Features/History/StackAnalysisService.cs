namespace Stacker.Core;

public sealed class StackAnalysisService(IHistoryStore store, IHistoryGit objects, IGitRepositoryReader git)
{
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
