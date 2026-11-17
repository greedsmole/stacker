using Stacker.Core;

namespace Stacker.Desktop;

internal sealed record DiagramRevision(string Key, string Name, string Commit, DiagramRevision? Parent = null, PullRequest? Pr = null)
{
    public override string ToString() => $"{Name} · {Commit[..8]}";
}
