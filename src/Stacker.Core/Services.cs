namespace Stacker.Core;

public static class StackValidation
{
    public static void Validate(IReadOnlyList<StackDefinition> stacks)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stack in stacks)
        {
            if (string.IsNullOrWhiteSpace(stack.Id) || !ids.Add(stack.Id))
                throw new StackerException("Each stack must have a unique, non-empty ID.");
            if (string.IsNullOrWhiteSpace(stack.Name)) throw new StackerException("A stack needs a name.");
            if (!ValidRef(stack.Base)) throw new StackerException("Select a full base ref.");
            var refs = new HashSet<string>(StringComparer.Ordinal) { stack.Base };
            foreach (var branch in stack.Branches)
                if (!ValidRef(branch) || !refs.Add(branch))
                    throw new StackerException("Layers must be unique full refs and cannot contain the base.");
        }
    }
    private static bool ValidRef(string? value) => value is not null &&
        (value.StartsWith("refs/heads/", StringComparison.Ordinal) || value.StartsWith("refs/remotes/", StringComparison.Ordinal)) &&
        !value.Any(char.IsControl) && !value.Contains(' ');
}

public sealed class StackService(IGitRepositoryReader git)
{
    public async Task<IReadOnlyList<StackLayerSnapshot>> SnapshotAsync(RepositorySnapshot repository, StackDefinition stack, CancellationToken ct = default)
    {
        var refs = repository.Refs.ToDictionary(r => r.Name, r => r.Sha, StringComparer.Ordinal);
        var layers = new List<StackLayerSnapshot>();
        for (var i = 0; i < stack.Branches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var parent = i == 0 ? stack.Base : stack.Branches[i - 1];
            refs.TryGetValue(parent, out var parentSha);
            refs.TryGetValue(stack.Branches[i], out var head);
            var ahead = 0; var behind = 0; string? warning = null;
            if (head is null || parentSha is null) warning = $"Missing ref: {(head is null ? stack.Branches[i] : parent)}";
            else
            {
                (ahead, behind) = await git.CountsAsync(repository.Root, parentSha, head, ct);
                if (behind > 0) warning = "Parent is not an ancestor. Diff uses merge-base, not a merge preview.";
            }
            layers.Add(new(i, stack.Branches[i], parent, head, parentSha, ahead, behind, warning));
        }
        return layers;
    }
}

public sealed class DiffService(IGitRepositoryReader git)
{
    public async Task<IReadOnlyList<DiffResult>> CompareAsync(DiffRequest request, CancellationToken ct = default)
    {
        var stack = request.Stack;
        if (stack.Branches.Count == 0) return [];
        var positions = request.Mode == DiffMode.FullStack ? [stack.Branches.Count - 1] : request.Positions.Distinct().Order().ToArray();
        if (request.Mode != DiffMode.MultiLayer && positions.Length > 1) positions = [positions[^1]];
        var refs = request.Repository.Refs.ToDictionary(r => r.Name, r => r.Sha, StringComparer.Ordinal);
        var results = new List<DiffResult>();
        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();
            if (position < 0 || position >= stack.Branches.Count) throw new StackerException("The selected layer no longer exists. Refresh the stack.");
            var branch = stack.Branches[position];
            var parent = request.Mode is DiffMode.Cumulative or DiffMode.FullStack || position == 0 ? stack.Base : stack.Branches[position - 1];
            if (!refs.TryGetValue(parent, out var parentSha)) throw new StackerException($"Missing ref: {parent}");
            if (!refs.TryGetValue(branch, out var headSha)) throw new StackerException($"Missing ref: {branch}");
            var bases = await git.MergeBasesAsync(request.Repository.Root, parentSha, headSha, ct);
            if (bases.Count == 0) throw new StackerException($"{branch}: no common ancestor with {parent}.");
            if (bases.Count > 1) throw new StackerException($"{branch}: multiple merge-bases; this history is ambiguous.");
            var warning = bases[0] == parentSha ? null : "Parent is not an ancestor. Showing changes from merge-base; this is not a merge preview.";
            var files = await git.FilesAsync(request.Repository.Root, bases[0], headSha, ct);
            results.Add(new(branch, parent, bases[0], headSha, warning, files));
        }
        return results;
    }
}
