namespace Stacker.Core;

/// <summary>Resolves the configured layers of one stack against a single repository snapshot.</summary>
/// <remarks>Read-only: it never changes a ref, so it can run on every refresh. Layer SHAs come from the supplied snapshot rather
/// than from fresh ref reads, so all layers in one call describe the same moment and the comparison that follows cannot mix two
/// states of a branch that moved during the refresh.</remarks>
public sealed class StackService(IGitRepositoryReader git)
{
    /// <summary>Resolves each configured layer and measures its relation to the immediately preceding ref.</summary>
    /// <remarks>A behind count warns that the displayed merge-base diff is not a prediction of the merge result. One pass runs per
    /// configured branch, in definition order: <c>Position</c> is the zero-based index, the parent is the stack base for the first
    /// layer and the previous branch afterwards, and both SHAs are looked up in the snapshot's <c>Refs</c> instead of being
    /// re-resolved. Each layer therefore costs at most one ahead/behind read and no ref write. A ref missing from the snapshot
    /// leaves its counts at zero and sets the warning; those zeros are placeholders, not evidence that the layer is in sync. When
    /// the parent tip is not an ancestor of the layer, the count of parent-only commits is reported and the warning says the
    /// comparison will start at the merge base, because the eventual merge result depends on those newer parent commits.</remarks>
    /// <param name="repository">The snapshot whose refs and commit IDs are used for every layer.</param>
    /// <param name="stack">The definition to resolve; its <c>Branches</c> order is the layer order.</param>
    /// <param name="ct">Cancels between layers; the method is a sequence of reads with no effects to roll back.</param>
    /// <returns>One snapshot per configured branch, in definition order.</returns>
    /// <exception cref="StackerException">A count read failed; the message is the adapter's problem text.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested between layers.</exception>
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
            var ahead = 0;
            var behind = 0;
            string? warning = null;
            if (head is null || parentSha is null)
                warning = $"Missing ref: {(head is null ? stack.Branches[i] : parent)}";
            else
            {
                (ahead, behind) = (await git.CountsAsync(repository.Root, parentSha, head, ct)).OrThrow();
                if (behind > 0)
                    warning = "Parent is not an ancestor. Diff uses merge-base, not a merge preview.";
            }
            layers.Add(new(i, stack.Branches[i], parent, head, parentSha, ahead, behind, warning));
        }
        return layers;
    }
}
