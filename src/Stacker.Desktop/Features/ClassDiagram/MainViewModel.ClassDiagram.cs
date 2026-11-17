using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    internal ICSharpGraphReader? CSharpGraph { get; }

    internal DiagramContext CreateDiagramContext(DiagramTarget target)
    {
        var repository = Repository?.Identity ?? throw new StackerException("Open a local repository to analyze C#.");
        var group = _runtime._activeGroup;
        var remote = _runtime._remoteSnapshot;
        var section = Sections.FirstOrDefault(view => view.Result.Layer == target.Ref && view.Root == repository.Root);
        var requestedPr = target.PullRequestNumber is long number
            ? remote?.PullRequests.FirstOrDefault(pr => pr.Number == number)
                ?? throw new StackerException($"PR #{number} is unavailable. Refresh GitHub and reopen the diagram.")
            : null;
        if (requestedPr is not null)
            group = RemoteGroups.Concat(OtherGroups).FirstOrDefault(item => item.PullRequests.Any(pr => pr.Number == requestedPr.Number));

        var revisions = new List<DiagramRevision>();
        DiagramRevision? stackBase = null;
        if (group is { IsRemote: true } && (requestedPr is not null || section?.AssociatedPr is not null))
        {
            var first = group.PullRequests[0];
            stackBase = new("base", first.BaseRef, first.BaseSha, Pr: first);
            revisions.Add(stackBase);
            foreach (var pr in group.PullRequests)
                revisions.Add(PrRevision(pr, section?.IsPrSnapshot == true ? section.Result : null));
        }
        else if (requestedPr is not null)
        {
            stackBase = new("base", requestedPr.BaseRef, requestedPr.BaseSha, Pr: requestedPr);
            revisions.Add(stackBase);
            revisions.Add(PrRevision(requestedPr, section?.IsPrSnapshot == true ? section.Result : null));
        }
        else if (group is { IsRemote: false })
        {
            var layers = group.Layers.Select(item => item.Snapshot).OrderBy(layer => layer.Position).ToArray();
            if (layers.FirstOrDefault() is { ParentSha: { } baseCommit } first)
            {
                stackBase = new("base", StackRefs.Short(first.Parent), baseCommit);
                revisions.Add(stackBase);
            }
            foreach (var layer in layers)
            {
                if (layer.HeadSha is not { } head) continue;
                var basis = layer.ParentSha is { } parent ? new DiagramRevision("parent", StackRefs.Short(layer.Parent), parent) : null;
                revisions.Add(new(layer.Branch, StackRefs.Short(layer.Branch), head, basis));
            }
        }

        var currentKey = requestedPr is null ? target.Ref : RemoteRefs.HeadRef(requestedPr);
        var current = revisions.FirstOrDefault(revision => revision.Key == currentKey);
        var diff = section?.Result;
        DiagramRevision? displayedBase = null;
        if (target.DisplayedDiff && diff is not null && (requestedPr is null || section?.IsPrSnapshot == true))
        {
            var isStackRange = requestedPr is null && !section!.IsPrSnapshot && Mode is DiffMode.Cumulative or DiffMode.FullStack;
            var basisName = isStackRange ? stackBase?.Name ?? StackRefs.Short(diff.ParentRef)
                : current?.Parent?.Name ?? StackRefs.Short(diff.ParentRef);
            var basis = new DiagramRevision("comparison-base", basisName, diff.BaseSha, Pr: isStackRange ? stackBase?.Pr : current?.Pr);
            if (isStackRange && current is not null)
                displayedBase = basis;
            var displayed = new DiagramRevision(currentKey, current?.Name ?? StackRefs.Short(diff.Layer), diff.HeadSha,
                displayedBase is null ? basis : current?.Parent, current?.Pr);
            if (current is null)
                revisions.Add(displayed);
            else
                revisions[revisions.IndexOf(current)] = displayed;
            current = displayed;
        }

        if (current is null)
            throw new StackerException("The selected branch or PR is unavailable. Refresh the changes and reopen the diagram.");
        if ((displayedBase ?? current.Parent) is { } alternate && !revisions.Any(revision => revision.Commit == alternate.Commit))
            revisions.Insert(stackBase is null ? 0 : 1, alternate with { Key = "comparison-base", Name = "Comparison base · " + alternate.Name });
        return new(repository, revisions.ToArray(), current, stackBase, remote?.Context, displayedBase);
    }

    internal async Task<DiagramRevision?> PrepareDiagramRangeAsync(DiagramContext context, DiagramRevision head,
        DiagramMode mode, CancellationToken ct)
    {
        var basis = context.BaseFor(head, mode);
        var prs = new[] { basis?.Pr, head.Pr }.OfType<PullRequest>().DistinctBy(pr => pr.Number).ToArray();
        if (prs.Length > 0 && !IsDemo)
        {
            if (_runtime._prObjects is not { } objects || context.GitHub is not { } github)
                throw new StackerException("PR object preparation is unavailable. Refresh GitHub and retry.");
            (await objects.EnsureAsync(context.Repository, github, prs, ct)).OrThrow();
        }
        if (mode != DiagramMode.Layer || basis is null || basis.Key == "comparison-base")
            return basis;

        var bases = (await _runtime._git.MergeBasesAsync(context.Repository.Root, basis.Commit, head.Commit, ct)).OrThrow();
        if (bases.Count != 1)
            throw new StackerException($"Cannot compare {head.Name} with {basis.Name}. Expected one Git merge base.");
        return basis with { Commit = bases[0] };
    }

    private static DiagramRevision PrRevision(PullRequest pr, DiffResult? exact)
    {
        var key = RemoteRefs.HeadRef(pr);
        var basis = exact?.Layer == key
            ? new DiagramRevision("comparison-base", pr.BaseRef, exact.BaseSha, Pr: pr)
            : new DiagramRevision("parent", pr.BaseRef, pr.BaseSha, Pr: pr);
        return new(key, $"#{pr.Number} {pr.HeadRef}", pr.HeadSha, basis, pr);
    }
}
