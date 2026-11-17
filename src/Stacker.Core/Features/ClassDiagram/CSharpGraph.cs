namespace Stacker.Core;

public sealed record CSharpGraph(string Commit, IReadOnlyList<CSharpType> Types, IReadOnlyList<CSharpRelationship> Relationships,
    IReadOnlyList<string> Projects, IReadOnlyList<string> Diagnostics)
{
    public bool IsComplete => Diagnostics.Count == 0;
}
