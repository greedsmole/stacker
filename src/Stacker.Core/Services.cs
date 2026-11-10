namespace Stacker.Core;

/// <summary>Enforces definition invariants before a stack is saved or compared.</summary>
/// <remarks>The editing dialog and the configuration store both call <see cref="Validate"/>, so a definition the workspace could not
/// display or compare never reaches disk. Validation is deliberately shape-only: it cannot know which refs exist in the
/// repository that will later be opened, and a stack may legitimately be saved before a branch is fetched. That is why a
/// missing ref is reported per layer by <see cref="StackService.SnapshotAsync"/> instead of rejecting the definition.</remarks>
public static class StackValidation
{
    /// <summary>Rejects duplicate IDs, invalid refs, repeated layers, and using the base as a layer.</summary>
    /// <remarks>Every guard prevents a failure that would otherwise surface much later as an empty comparison or a silently
    /// replaced entry: an empty or repeated stack ID collides in the workspace key and in the UI's lookup by ID, so one stack's saved
    /// view would stand in for another's; an empty name leaves the stack unlabeled in the workspace; a base that is not a full <c>refs/heads/</c> or
    /// <c>refs/remotes/</c> ref - or one containing a space or control character - can resolve to an object the user did not mean
    /// and is unsafe to pass as one Git argument; a layer that repeats another layer, or equals the base, would be compared with
    /// itself, producing an empty diff and a restack that tries to rebase a branch onto its own tip. Remote-tracking layers are
    /// accepted here because a stack can be inspected before its branches are local; <see cref="IGitBranchWriter"/> refuses to
    /// rewrite them. Order inside <c>Branches</c> is not validated: it is the layer order by definition, and a wrong order shows
    /// up as a suspicious ahead/behind count rather than as an error. IDs and refs are compared ordinally, so values that differ
    /// only by case are treated as distinct definitions.</remarks>
    /// <param name="stacks">Every definition that would be written; the editor passes one, a store save passes the whole
    /// document.</param>
    /// <exception cref="StackerException">The first violated invariant, with a message written for the authoring dialog.</exception>
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
    /// <summary>Accepts only a full local or remote-tracking ref that is safe as one Git argument.</summary>
    /// <remarks>The prefix decides whether the ref can ever be rewritten; the character check keeps a value from breaking out of
    /// a single argument if it is handed to Git unchanged. Existence is not checked here, because resolving a ref is a repository
    /// read and this type must stay independent of any repository. Tags, <c>refs/stash</c> and other namespaces are rejected on
    /// purpose: a layer is always a branch that can be compared and, when local, rebased.</remarks>
    /// <param name="value">The candidate ref; <see langword="null"/> is rejected.</param>
    /// <returns><see langword="true"/> for a <c>refs/heads/name</c> or <c>refs/remotes/name</c> value without spaces or control
    /// characters.</returns>
    private static bool ValidRef(string? value) => value is not null &&
        (value.StartsWith("refs/heads/", StringComparison.Ordinal) || value.StartsWith("refs/remotes/", StringComparison.Ordinal)) &&
        !value.Any(char.IsControl) && !value.Contains(' ');
}

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
            var ahead = 0; var behind = 0; string? warning = null;
            if (head is null || parentSha is null) warning = $"Missing ref: {(head is null ? stack.Branches[i] : parent)}";
            else
            {
                (ahead, behind) = (await git.CountsAsync(repository.Root, parentSha, head, ct)).OrThrow();
                if (behind > 0) warning = "Parent is not an ancestor. Diff uses merge-base, not a merge preview.";
            }
            layers.Add(new(i, stack.Branches[i], parent, head, parentSha, ahead, behind, warning));
        }
        return layers;
    }
}

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
            var bases = (await git.MergeBasesAsync(request.Repository.Root, parentSha, headSha, ct)).OrThrow();
            if (bases.Count == 0) throw new StackerException($"{branch}: no common ancestor with {parent}.");
            if (bases.Count > 1) throw new StackerException($"{branch}: multiple merge-bases; this history is ambiguous.");
            var warning = bases[0] == parentSha ? null : "Parent is not an ancestor. Showing changes from merge-base; this is not a merge preview.";
            var files = (await git.FilesAsync(request.Repository.Root, bases[0], headSha, ct)).OrThrow();
            results.Add(new(branch, parent, bases[0], headSha, warning, files));
        }
        return results;
    }
}
