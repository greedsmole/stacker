namespace Stacker.Core;

/// <summary>What publishing one layer must do to its pull request.</summary>
public enum PullRequestAction
{
    /// <summary>No open pull request exists for the layer's head branch, so one must be created into the parent.</summary>
    Create,
    /// <summary>An open pull request exists but its base is not the layer's parent, so it must be retargeted.</summary>
    UpdateBase,
    /// <summary>The pull request already has the right base, or the layer cannot be published from here.</summary>
    Unchanged
}

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

/// <summary>Every pull request a stack needs, computed from a server snapshot before anything is written.</summary>
/// <remarks>
/// The plan is a checked proposal: the preview shows it, then publication re-derives it from a fresh snapshot and refuses
/// to proceed unless both the stack and the ordered steps still match. That is why the context and repository root are
/// recorded here — a plan carried across a repository switch or an account change must not be executed, even if its steps
/// still look right. Checking also refuses a plan with blockers or with nothing to do, so the publish action never sends a
/// no-op request that would only fail on GitHub.
/// </remarks>
/// <param name="StackId">Identifier of the stack the plan was derived from, so it can be matched to the live stack.</param>
/// <param name="StackName">Display name of that stack, shown in the confirmation.</param>
/// <param name="Steps">One step per layer, in stack order.</param>
/// <param name="Blockers">Reasons the plan cannot be published; nonempty means every step is left unchanged.</param>
public sealed record PullRequestPublicationPlan(string StackId, string StackName, IReadOnlyList<PullRequestStep> Steps, IReadOnlyList<string> Blockers)
{
    /// <summary>GitHub context identity at check time, set by the caller; publication refuses a plan from another context.</summary>
    public string? ContextIdentity { get; init; }
    /// <summary>Local repository root at check time, set by the caller; publication refuses a plan from another checkout.</summary>
    public string? RepositoryRoot { get; init; }
    /// <summary>True only when there are no blockers and at least one step has an effect.</summary>
    public bool CanPublish => Blockers.Count == 0 && PendingCount > 0;
    /// <summary>Number of layers that need a pull request created or retargeted.</summary>
    public int PendingCount => Steps.Count(step => step.Action != PullRequestAction.Unchanged);
}

/// <summary>Matches stack layers to open pull requests by exact repository and branch identity, never by title or history.</summary>
/// <remarks>Titles, commit messages and timing are not evidence that two pull requests form a stack, so they are not used
/// as matching keys: a wrong match would retarget someone else's pull request.</remarks>
public static class PullRequestPlanner
{
    /// <summary>Reports the pull request each layer needs and refuses ambiguous or non-local layers instead of guessing.</summary>
    /// <remarks>
    /// Flow: each layer is paired with its parent (the stack base for the first layer, the previous layer otherwise) and the
    /// branch is shortened for comparison. A layer that is not a local branch is blocked first, because only a local layer
    /// can own a pull request published from this checkout; a remote-tracking layer is recorded as unchanged so positions do
    /// not shift. Open pull requests are then matched by head repository id and exact head branch name — the repository id
    /// rejects a fork's same-named branch, and the exact comparison rejects a branch that merely looks similar. More than one
    /// match is blocked rather than resolved: adopting one of them would guess which pull request the user meant, and a create
    /// for the same branch pair would be refused by GitHub. No match means create. One match is kept when its base already
    /// equals the parent's branch name, and retargeted otherwise. Duplicate blocker text is collapsed at the end, and the
    /// method has no side effects.
    /// </remarks>
    /// <param name="stack">Stack definition whose branches are the layers, in order.</param>
    /// <param name="openPullRequests">Open pull requests from one page-complete snapshot.</param>
    /// <param name="repositoryId">Numeric id of the opened repository, used to reject pull requests from a fork.</param>
    /// <returns>A plan with one step per layer; blockers explain every layer that cannot be published.</returns>
    public static PullRequestPublicationPlan Plan(StackDefinition stack, IReadOnlyList<PullRequest> openPullRequests, long repositoryId)
    {
        var blockers = new List<string>();
        var steps = new List<PullRequestStep>();
        for (var position = 0; position < stack.Branches.Count; position++)
        {
            var branch = stack.Branches[position];
            var parent = position == 0 ? stack.Base : stack.Branches[position - 1];
            var name = StackRefs.Short(branch);
            // Only a local branch can own a pull request; the validated type refuses anything else.
            if (Framework.StackBranchRef.Parse(branch) is not { IsOk: true } parsedBranch || parsedBranch.Value.RequireLocal() is { IsFailed: true })
            {
                blockers.Add($"{name} is not a local branch, so its pull request cannot be published from here.");
                steps.Add(new(position, branch, parent, PullRequestAction.Unchanged, null, null, "Not a local branch"));
                continue;
            }
            var matches = openPullRequests
                .Where(pr => pr.HeadRepositoryId == repositoryId && string.Equals(pr.HeadRef, name, StringComparison.Ordinal)).ToArray();
            if (matches.Length > 1)
            {
                blockers.Add($"{name} has {matches.Length} open pull requests. Close the duplicates before publishing this stack.");
                steps.Add(new(position, branch, parent, PullRequestAction.Unchanged, null, null, "Multiple open pull requests"));
                continue;
            }
            // GitHub knows branches by name, so a remote-tracking parent becomes "main", never "origin/main".
            var parentName = Framework.StackBranchRef.Parse(parent) is { IsOk: true } parsedParent ? parsedParent.Value.Name : StackRefs.Short(parent);
            if (matches.Length == 0) { steps.Add(new(position, branch, parent, PullRequestAction.Create, null, null, null)); continue; }
            var pr = matches[0];
            steps.Add(string.Equals(pr.BaseRef, parentName, StringComparison.Ordinal)
                ? new(position, branch, parent, PullRequestAction.Unchanged, pr.Number, pr.BaseRef, null)
                : new(position, branch, parent, PullRequestAction.UpdateBase, pr.Number, pr.BaseRef, null));
        }
        return new(stack.Id, stack.Name, steps, blockers.Distinct().ToList());
    }
}
