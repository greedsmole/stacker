using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceRuntime
{
    internal IWorkspaceSession Session { get; } = new WorkspaceSession();
    internal static string ComparisonKey(RepositorySnapshot repo, StackDefinition stack, DiffMode mode, int[] positions)
    {
        var relevant = stack.Branches.Prepend(stack.Base).ToHashSet();
        return StableIdentity.For(new[] { repo.Scope, stack.Id, mode.ToString(), string.Join(',', positions), stack.Branches.Count.ToString() }.Concat(stack.Branches.Prepend(stack.Base)).Concat(repo.Refs.Where(r => relevant.Contains(r.Name)).OrderBy(r => r.Name).SelectMany(r => new[] { r.Name, r.Sha })).ToArray());
    }
    internal static StackGroupViewModel RemoteGroup(string id, string name, IReadOnlyList<PullRequest> prs, bool shared)
    {
        var definition = new StackDefinition(id, name, RemoteRefs.BaseRef(prs[0]), prs.Select(RemoteRefs.HeadRef).ToArray());
        var layers = prs.Select((p, i) => new StackLayerSnapshot(i, "refs/heads/" + p.HeadRef, "refs/heads/" + p.BaseRef, p.HeadSha, p.BaseSha, 0, 0, null));
        return new(definition, layers, prs, shared);
    }
    internal static bool SameActions(PullRequestPublicationPlan approved, PullRequestPublicationPlan current) =>
            approved.StackId == current.StackId && approved.Steps.SequenceEqual(current.Steps);
}
