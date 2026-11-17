namespace Stacker.Core;

public sealed record CSharpGraphComparison(IReadOnlyList<CSharpTypeChange> Types, IReadOnlyList<CSharpRelationshipChange> Relationships)
{
    public static CSharpGraphComparison Compare(CSharpGraph? basis, CSharpGraph head)
    {
        var comparable = basis?.IsComplete == true && head.IsComplete;
        var edges = CompareRelationships(basis, head, comparable);
        var changedSources = edges.Where(edge => edge.Change is CSharpChange.Added or CSharpChange.Removed)
            .Select(edge => (edge.Relationship.SourceType, edge.Relationship.SourceMember)).ToHashSet();
        var changedTypes = changedSources.Select(source => source.SourceType).ToHashSet(StringComparer.Ordinal);

        var before = Index(basis?.Types ?? [], type => type.Id);
        var after = Index(head.Types, type => type.Id);
        var types = before.Keys.Union(after.Keys, StringComparer.Ordinal).Select(id =>
        {
            var oldType = before.GetValueOrDefault(id);
            var newType = after.GetValueOrDefault(id);
            var oldMembers = Index(oldType?.Members ?? [], member => member.Id);
            var newMembers = Index(newType?.Members ?? [], member => member.Id);
            var members = oldMembers.Keys.Union(newMembers.Keys, StringComparer.Ordinal).Select(memberId =>
            {
                var oldMember = oldMembers.GetValueOrDefault(memberId);
                var newMember = newMembers.GetValueOrDefault(memberId);
                var status = Status(oldMember, newMember, oldMember?.DeclarationHash != newMember?.DeclarationHash || oldMember?.Signature != newMember?.Signature
                    || changedSources.Contains((id, memberId)), basis, comparable);
                return new CSharpMemberChange(oldMember, newMember, status);
            }).OrderBy(member => member.Member.Signature, StringComparer.Ordinal).ToArray();

            var changed = oldType?.DeclarationHash != newType?.DeclarationHash
                || members.Any(member => member.Change is CSharpChange.Added or CSharpChange.Removed or CSharpChange.Modified)
                || changedTypes.Contains(id);
            return new CSharpTypeChange(oldType, newType, Status(oldType, newType, changed, basis, comparable), members);
        }).OrderBy(type => type.Type.Project, StringComparer.Ordinal)
            .ThenBy(type => type.Type.Namespace, StringComparer.Ordinal).ThenBy(type => type.Type.Name, StringComparer.Ordinal).ToArray();
        return new(types, edges);
    }

    private static CSharpRelationshipChange[] CompareRelationships(CSharpGraph? basis, CSharpGraph head, bool comparable)
    {
        // A signature is a label. Source and target declarations identify the relationship across revisions.
        var before = Index(basis?.Relationships ?? [], EdgeKey);
        var after = Index(head.Relationships, EdgeKey);
        return before.Keys.Union(after.Keys).Select(key =>
        {
            var oldEdge = before.GetValueOrDefault(key);
            var newEdge = after.GetValueOrDefault(key);
            return new CSharpRelationshipChange((newEdge ?? oldEdge)!, Status(oldEdge, newEdge, false, basis, comparable));
        }).ToArray();
    }

    private static (string, string?, string?, string?, CSharpRelationshipKind) EdgeKey(CSharpRelationship edge) =>
        (edge.SourceType, edge.SourceMember, edge.TargetType, edge.TargetMember ?? (edge.TargetType is null ? edge.TargetSignature : null), edge.Kind);

    private static Dictionary<TKey, T> Index<T, TKey>(IEnumerable<T> items, Func<T, TKey> key) where TKey : notnull =>
        items.DistinctBy(key).ToDictionary(key);

    private static CSharpChange Status<T>(T? before, T? after, bool modified, CSharpGraph? basis, bool comparable) where T : class
    {
        if (basis is null) return CSharpChange.Unchanged;
        if (before is null || after is null)
            return comparable ? (before is null ? CSharpChange.Added : CSharpChange.Removed) : CSharpChange.Unknown;
        return modified ? CSharpChange.Modified : CSharpChange.Unchanged;
    }
}
