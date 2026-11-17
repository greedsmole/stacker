namespace Stacker.Core;

/// <summary>Read-only facts about one layer, gathered before a plan is built.</summary>
/// <remarks>Behind counts commits the parent has and this layer does not; a nonzero value means the parent is not an ancestor. OwnCommitCount is measured from the replay base, so it is what a rebase would replay rather than every commit on the branch. These values are a snapshot and can go stale before execution; the restack effect re-verifies the plan against the refs.</remarks>
/// <param name="Branch">Full ref of the layer as declared by the stack.</param>
/// <param name="Behind">Commits the parent has and this layer does not; nonzero means the layer must replay onto the parent.</param>
/// <param name="OwnCommitCount">Commits from the replay base to the layer head, i.e. what a rebase would replay; zero means the layer only points at its parent's history.</param>
/// <param name="HasMergeCommits">Whether those own commits include a merge, which a linear replay would flatten.</param>
/// <param name="ReplayBase">Fork point used as the rebase upstream so only own commits replay; null when it could not be established, in which case <c>Blocker</c> explains why.</param>
/// <param name="Blocker">Why this layer cannot be planned safely, already phrased for the user; null when the layer is plannable.</param>
public sealed record SyncLayerState(string Branch, int Behind, int OwnCommitCount, bool HasMergeCommits,
    string? ReplayBase = null, string? Blocker = null);
