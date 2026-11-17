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
            if (string.IsNullOrWhiteSpace(stack.Name))
                throw new StackerException("A stack needs a name.");
            if (!ValidRef(stack.Base))
                throw new StackerException("Select a full base ref.");
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
