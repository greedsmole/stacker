namespace Stacker.Core;

/// <summary>Produces the file lists for the requested layer comparisons in one pass.</summary>
/// <remarks>Only compares: it reads merge bases and file metadata and leaves patches to a later per-file request, so a stack with
/// many layers shows a file list before any large blob or patch is loaded. Every comparison is pinned to the SHAs captured in the
/// request, so a branch that moves while the comparison runs cannot produce a mixed result.</remarks>
public sealed class DiffService(IGitRepositoryReader git)
{
    /// <summary>Builds Layer, Cumulative, FullStack, or independent MultiLayer file lists.</summary>
    /// <remarks>All requested comparisons use SHAs captured in the request so a moving branch cannot mix snapshots. The mode
    /// decides the positions and the parent side: <c>FullStack</c> ignores <c>Positions</c> and compares the base with the topmost
    /// layer; <c>Layer</c> and <c>Cumulative</c> sort and de-duplicate <c>Positions</c> but keep only the highest, because one
    /// modes describe a single comparison, so extra positions are redundant; <c>MultiLayer</c> keeps every distinct position and compares each layer with its
    /// own parent, which is the base for layer zero. <c>Cumulative</c> and <c>FullStack</c> always compare against the stack base,
    /// which is what makes "through layer N" a single base-to-N diff. A position outside the current layer count, a ref missing
    /// from the snapshot, or a merge base that is absent or ambiguous is rejected instead of returning a plausible but wrong file
    /// list: zero merge bases means unrelated histories, and more than one means a criss-cross history where either answer would be
    /// arbitrary. When the merge base is not the parent tip, the result carries a warning that this is a merge-base diff and not a
    /// merge preview. Results are returned in ascending layer order.</remarks>
    /// <param name="request">The snapshot, definition, mode and zero-based positions to compare.</param>
    /// <param name="ct">Cancels between layers; no ref or file is changed by this method.</param>
    /// <returns>One result per requested comparison; empty when the stack has no layers, or when a <c>Layer</c>, <c>Cumulative</c> or
    /// <c>MultiLayer</c> request lists no positions.</returns>
    /// <exception cref="StackerException">A position no longer exists, a ref is missing from the snapshot, or the merge base is
    /// absent or ambiguous. The message names the layer or ref that failed and is written for the user.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested between layers.</exception>
    public async Task<IReadOnlyList<DiffResult>> CompareAsync(DiffRequest request, CancellationToken ct = default)
    {
        var stack = request.Stack;
        if (stack.Branches.Count == 0)
            return [];
        var positions = request.Mode == DiffMode.FullStack ? [stack.Branches.Count - 1] : request.Positions.Distinct().Order().ToArray();
        if (request.Mode != DiffMode.MultiLayer && positions.Length > 1)
            positions = [positions[^1]];
        var refs = request.Repository.Refs.ToDictionary(r => r.Name, r => r.Sha, StringComparer.Ordinal);
        var results = new List<DiffResult>();
        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();
            if (position < 0 || position >= stack.Branches.Count)
                throw new StackerException("The selected layer no longer exists. Refresh the stack.");
            var branch = stack.Branches[position];
            var parent = request.Mode is DiffMode.Cumulative or DiffMode.FullStack || position == 0 ? stack.Base : stack.Branches[position - 1];
            if (!refs.TryGetValue(parent, out var parentSha))
                throw new StackerException($"Missing ref: {parent}");
            if (!refs.TryGetValue(branch, out var headSha))
                throw new StackerException($"Missing ref: {branch}");
            var bases = (await git.MergeBasesAsync(request.Repository.Root, parentSha, headSha, ct)).OrThrow();
            if (bases.Count == 0)
                throw new StackerException($"{branch}: no common ancestor with {parent}.");
            if (bases.Count > 1)
                throw new StackerException($"{branch}: multiple merge-bases; this history is ambiguous.");
            var warning = bases[0] == parentSha ? null : "Parent is not an ancestor. Showing changes from merge-base; this is not a merge preview.";
            var files = (await git.FilesAsync(request.Repository.Root, bases[0], headSha, ct)).OrThrow();
            results.Add(new(branch, parent, bases[0], headSha, warning, files));
        }
        return results;
    }
}
