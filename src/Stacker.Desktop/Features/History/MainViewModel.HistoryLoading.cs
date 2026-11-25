using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private async Task<HistoryWorkspaceViewModel> CreateHistoryAsync(CancellationToken ct)
    {
        if (_runtime.HistoryStore is not { } store || _runtime.HistoryObjects is not { } objects ||
            _runtime.HistoryGraph is not { } graphs || _runtime.HistoryComparisons is not { } comparisons ||
            _runtime.HistoryProgress is not { } progress)
            throw new StackerException("Review history is unavailable in this session.");
        if (_runtime._activeGroup is not { } group || _runtime._repository is not { } repo)
            throw new StackerException("Select a stack or pull request first.");
        if (HasPendingChanges)
            throw new StackerException("Apply Update changes before opening review history.");
        if (group.Layers.Count == 0)
            throw new StackerException("The stack has no layers.");

        var repository = repo.Identity ?? throw new StackerException("Repository identity is unavailable.");
        var sections = Sections.Where(section => section.Root == repo.Root).ToArray();
        var demo = IsDemo;
        var remote = group.IsRemote ? _runtime._remoteSnapshot ?? throw new StackerException("Refresh to load this stack.") : null;
        var graph = remote is not null
            ? _runtime._historyRemoteGraph is { } displayed && displayed.Scope == remote.Context.Identity ? displayed
                : await graphs.ObserveRemoteAsync(remote, GitHubPreferences.BoundaryBranches ?? [], ct)
            : (await _runtime.ObserveLocalHistoryAsync(repo, _runtime._document ?? new([], null), ct,
                group.UsesGhStack ? group.Definition : null))!;
        ct.ThrowIfCancellationRequested();

        var prs = new Dictionary<string, PullRequest>();
        var path = new List<(ChangeNode Node, ChangeVersion Version)>();
        for (var i = 0; i < group.Layers.Count; i++)
        {
            var layer = group.Layers[i].Snapshot;
            var pr = remote is null ? null : group.PullRequests[i];
            var node = graph.Nodes.SingleOrDefault(n => pr is null ? n.Reference == layer.Branch : n.PullRequest == pr.Number)
                ?? throw new StackerException("Refresh the stack to load its history.");
            var basis = pr?.BaseSha ?? layer.ParentSha ?? "";
            var head = pr?.HeadSha ?? layer.HeadSha ?? "";
            var id = StableIdentity.For(node.Id + "|" + basis + "|" + head);
            path.Add((node, new(id, node.Id, basis, head, null, graph.ObservedAt)));
            if (pr is not null)
                prs[node.Id] = pr;
        }

        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materialized = new Dictionary<string, ChangeVersion>(StringComparer.Ordinal);
        return new HistoryWorkspaceViewModel(store, objects, _runtime._git, comparisons, progress,
            graph, repository, path, remote is null ? null : ReadDiscussionAsync, _runtime._highlighter, Settings.Theme)
        {
            WrapCode = Settings.WrapCode,
            MaterializeVersion = MaterializeAsync,
            ExistingFiles = version => sections.FirstOrDefault(section => section.Result.BaseSha == version.MergeBase &&
                section.Result.HeadSha == version.HeadSha)?.Result.Files,
            RetainLocalLayers = async token =>
            {
                var commits = path.SelectMany(item => new[] { item.Version.BaseSha, item.Version.HeadSha })
                    .Where(sha => sha.Length > 0 && !retained.Contains(sha)).Distinct().ToArray();
                if (commits.Length > 0)
                    retained.UnionWith((await objects.CaptureAvailableAsync(repository, graph.Scope, commits, token)).OrThrow());
                foreach (var (_, version) in path)
                {
                    token.ThrowIfCancellationRequested();
                    if (!materialized.ContainsKey(version.Id) && retained.Contains(version.BaseSha) && retained.Contains(version.HeadSha))
                    {
                        // Persist the ancestor of a retained layer before a later rewrite makes it historical.
                        try { await MaterializeAsync(version, token); }
                        catch (StackerException) { /* Report this layer's error on selection. */ }
                    }
                }
            },
            GraphEdited = () =>
            {
                if (remote is not null && _runtime._remoteSnapshot?.Context.Identity == remote.Context.Identity)
                    _runtime._historyRemoteGraph = null;
                else
                    _runtime.InvalidateLocalHistory();
            }
        };

        async Task<ChangeVersion> MaterializeAsync(ChangeVersion version, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (materialized.TryGetValue(version.Id, out var cached))
                return cached;
            if (version.BaseSha.Length == 0 || version.HeadSha.Length == 0)
                throw new StackerException("A layer ref is missing. Refresh its definition and load the layer again.");
            var commits = new[] { version.BaseSha, version.HeadSha }.Distinct().ToArray();
            if (!commits.All(retained.Contains))
            {
                if (remote is not null && !demo && prs.TryGetValue(version.ChangeId, out var pr))
                {
                    retained.UnionWith((await objects.CaptureAvailableAsync(repository, graph.Scope, commits, token)).OrThrow());
                    if (!commits.All(retained.Contains))
                    {
                        var provider = _runtime._prObjects ?? throw new StackerException("PR object preparation is unavailable.");
                        (await provider.EnsureAsync(repository, remote.Context, [pr], token)).OrThrow();
                    }
                }
                if (!commits.All(retained.Contains))
                {
                    (await objects.CaptureAsync(repository, graph.Scope, commits, token)).OrThrow();
                    retained.UnionWith(commits);
                }
            }
            var observed = (await store.VersionsAsync(version.ChangeId, token)).OrThrow()
                .FirstOrDefault(prior => prior.Id == version.Id && prior.MergeBase is not null);
            if (observed is not null)
                return materialized[version.Id] = observed;
            var basis = sections.FirstOrDefault(section => section.Result.HeadSha == version.HeadSha &&
                section.Result.BaseSha == version.BaseSha)?.Result.BaseSha;
            if (basis is null)
            {
                var bases = (await _runtime._git.MergeBasesAsync(repository.Root, version.BaseSha, version.HeadSha, token)).OrThrow();
                basis = bases.Count == 1 ? bases[0] : null;
            }
            var saved = (await store.ObserveAsync(version with { MergeBase = basis }, token)).OrThrow();
            return saved.MergeBase is not null ? materialized[version.Id] = saved :
                throw new StackerException("This layer has no unique merge base. Its recorded version is available in history.");
        }

        async Task<ReviewDiscussion?> ReadDiscussionAsync(ChangeNode node, ChangeVersion version, CancellationToken token)
        {
            var pr = prs[node.Id] with { BaseSha = version.BaseSha, HeadSha = version.HeadSha };
            return demo ? DemoReviewData.Discussion(pr) : _runtime._github is null ? null :
                (await _runtime._github.DiscussionAsync(remote!.Context, pr, token)).OrThrow();
        }
    }
}
