namespace Stacker.Core;

/// <summary>One layer's pull-request outcome: the branch, the base it needs, and what is missing.</summary>
/// <remarks>
/// A step is a plan line, not a mutation: it holds the numbers and refs that were observed in the snapshot so the preview
/// can show what will happen and the publisher can act without replanning per layer. Steps for blocked layers stay in the
/// list as <see cref="PullRequestAction.Unchanged"/> with a <paramref name="Note"/>, which keeps each position aligned with
/// the stack layer of the same index.
/// </remarks>
/// <param name="Position">Zero-based stack layer index; titles show it one-based for humans.</param>
/// <param name="Branch">Full ref of the layer head, as stored in the stack definition.</param>
/// <param name="ParentRef">Full ref of the layer parent, which is the stack base for the first layer and the previous
/// layer's branch otherwise.</param>
/// <param name="Action">What must happen to this layer's pull request.</param>
/// <param name="Number">Existing open pull request number, or null when none was found.</param>
/// <param name="CurrentBase">Base branch the existing pull request currently points at, or null when it has none.</param>
/// <param name="Note">Why the layer is unchanged or blocked, shown instead of a pull request summary.</param>
public sealed record PullRequestStep(int Position, string Branch, string ParentRef, PullRequestAction Action,
    long? Number, string? CurrentBase, string? Note)
{
    /// <summary>The branch name without its ref prefix, for display and for the GitHub request.</summary>
    public string BranchName => StackRefs.Short(Branch);
    /// <summary>The base branch name GitHub knows, so a remote-tracking parent never leaks "origin/" into a request.</summary>
    /// <remarks>GitHub resolves bases by branch name, not by local ref: sending <c>origin/main</c> would ask for a branch
    /// literally named that and fail, and comparison against the server's <c>main</c> would never match. A parent that does
    /// not parse as a ref falls back to the short form so the preview still shows something instead of throwing.</remarks>
    public string ParentName => Framework.StackBranchRef.Parse(ParentRef) is { IsOk: true } parsed ? parsed.Value.Name : StackRefs.Short(ParentRef);
    /// <summary>Layer caption used in the preview, numbered from one.</summary>
    public string Title => $"Layer {Position + 1} · {BranchName}";
    /// <summary>The one-line promise of what this step does, or why it does nothing.</summary>
    public string Status => Action switch
    {
        PullRequestAction.Create => $"Create a pull request into {ParentName}",
        PullRequestAction.UpdateBase => $"#{Number} · base {CurrentBase} → {ParentName}",
        _ => Note ?? (Number is null ? "No pull request needed" : $"#{Number} · already into {ParentName}")
    };
}
