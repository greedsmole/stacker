using Stacker.Core;

namespace Stacker.Desktop;

internal sealed record DiagramContext(RepositoryIdentity Repository, IReadOnlyList<DiagramRevision> Revisions,
    DiagramRevision Current, DiagramRevision? StackBase, GitHubRepositoryContext? GitHub, DiagramRevision? DisplayedBase)
{
    internal DiagramRevision? BaseFor(DiagramRevision revision, DiagramMode mode) => mode switch
    {
        DiagramMode.Layer => revision.Parent,
        DiagramMode.Displayed => DisplayedBase,
        DiagramMode.Cumulative => StackBase,
        _ => null
    };
}
