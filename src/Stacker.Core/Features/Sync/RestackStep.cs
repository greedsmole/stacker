namespace Stacker.Core;

/// <summary>One layer's planned rewrite; every SHA is captured before anything moves.</summary>
/// <remarks>OldParentTip and BranchTip are read from the snapshot, so the preview, the journal and the push lease all describe the same commits. RemoteTip is the remote-tracking commit at plan time and becomes the force-with-lease value. A blocked step may have null tips. NeedsRebase is true when the parent moved or when any layer below is rewritten; such a step is never dropped, even with no commits of its own, because it must still follow its rewritten parent.</remarks>
/// <param name="Position">Zero-based position in the stack; user-facing labels use this plus one.</param>
/// <param name="Branch">Full ref of the layer.</param>
/// <param name="ParentRef">Full ref of its parent: the previous layer, or the stack base for the first.</param>
/// <param name="OldParentTip">Parent commit the plan was built against, and the default rebase base; null when that ref was missing.</param>
/// <param name="BranchTip">Layer commit at plan time, used as the journal's old value; null when the ref was missing.</param>
/// <param name="RemoteTip">Remote-tracking commit of the same branch, used as the push lease; null when the plan captured none, for example no remote-tracking ref or a blocked layer.</param>
/// <param name="CommitCount">Own commits the rebase would replay; zero means the layer only follows its parent.</param>
/// <param name="NeedsRebase">Whether the layer must move because its parent moved or a layer below is being rewritten.</param>
/// <param name="Blocked">Whether this layer cannot be rewritten because of its own state or a blocked layer below.</param>
/// <param name="Note">Optional sentence shown instead of the parent and tip detail; when several per-layer conditions apply, every blocker is kept but the last note assigned wins.</param>
public sealed record RestackStep(int Position, string Branch, string ParentRef, string? OldParentTip, string? BranchTip,
    string? RemoteTip, int CommitCount, bool NeedsRebase, bool Blocked, string? Note)
{
    /// <summary>Commit to use as the rebase upstream so only the layer's own commits replay.</summary>
    /// <remarks>The planner stores the fork point from preflight when it is known, otherwise the parent tip; the effect passes it as the base of <c>rebase --onto</c>. A blocked step is never executed, so an executable step always carries a value.</remarks>
    public string? ReplayBase
    {
        get; init;
    }
    /// <summary>Short layer name for labels and messages.</summary>
    public string BranchName => StackRefs.Short(Branch);
    /// <summary>Short parent name for labels and messages.</summary>
    public string ParentName => StackRefs.Short(ParentRef);
    /// <summary>Whether the layer is a local branch, the only ref namespace Stacker may rewrite; a remote-tracking layer is blocked instead.</summary>
    public bool IsLocalBranch => Branch.StartsWith("refs/heads/", StringComparison.Ordinal);
    /// <summary>Preview title: the one-based layer number and the short branch name.</summary>
    public string Title => $"Layer {Position + 1} · {BranchName}";
    /// <summary>What the preview promises before the user approves anything.</summary>
    /// <remarks>Blocked wins over Rebase, which wins over Up to date. A layer that only follows a rewritten parent shows "Rebase 0 commit(s)" because it must still be replayed; the count is a prediction, and the effect reports commits that were already present and dropped.</remarks>
    public string Status => Blocked ? "Blocked" : NeedsRebase ? $"Rebase {CommitCount} commit(s) onto {ParentName}" : "Up to date";
    /// <summary>Secondary preview line: the step's note when present, otherwise the parent name and the short tips.</summary>
    /// <remarks><see cref="RestackStep.Short"/> caps each id at seven characters, so full ids never widen the row; a null tip would render as "missing" instead of a blank.</remarks>
    public string Detail => Note ?? $"parent {ParentName} · {Short(OldParentTip)} → {Short(BranchTip)}";
    /// <summary>Formats a commit id for display: null becomes "missing", otherwise at most seven characters.</summary>
    /// <remarks>Uses <c>Math.Min(7, sha.Length)</c>, so an id shorter than seven characters is not padded and slicing cannot throw.</remarks>
    /// <param name="sha">Commit id, or null when the ref was missing.</param>
    /// <returns>The display form.</returns>
    private static string Short(string? sha) => sha is null ? "missing" : sha[..Math.Min(7, sha.Length)];
}
