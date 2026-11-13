namespace Stacker.Core;

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
            if (matches.Length == 0)
            {
                steps.Add(new(position, branch, parent, PullRequestAction.Create, null, null, null));
                continue;
            }
            var pr = matches[0];
            steps.Add(string.Equals(pr.BaseRef, parentName, StringComparison.Ordinal)
                ? new(position, branch, parent, PullRequestAction.Unchanged, pr.Number, pr.BaseRef, null)
                : new(position, branch, parent, PullRequestAction.UpdateBase, pr.Number, pr.BaseRef, null));
        }
        return new(stack.Id, stack.Name, steps, blockers.Distinct().ToList());
    }
}
