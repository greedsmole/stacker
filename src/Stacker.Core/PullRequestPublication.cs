namespace Stacker.Core;

/// <summary>What publishing one layer must do to its pull request.</summary>
public enum PullRequestAction { Create, UpdateBase, Unchanged }

/// <summary>One layer's pull-request outcome: the branch, the base it needs, and what is missing.</summary>
public sealed record PullRequestStep(int Position, string Branch, string ParentRef, PullRequestAction Action,
    long? Number, string? CurrentBase, string? Note)
{
    public string BranchName => StackRefs.Short(Branch);
    /// <summary>The base branch name GitHub knows, so a remote-tracking parent never leaks "origin/" into a request.</summary>
    public string ParentName => Framework.StackBranchRef.Parse(ParentRef) is { IsOk: true } parsed ? parsed.Value.Name : StackRefs.Short(ParentRef);
    public string Title => $"Layer {Position + 1} · {BranchName}";
    public string Status => Action switch
    {
        PullRequestAction.Create => $"Create a pull request into {ParentName}",
        PullRequestAction.UpdateBase => $"#{Number} · base {CurrentBase} → {ParentName}",
        _ => Note ?? (Number is null ? "No pull request needed" : $"#{Number} · already into {ParentName}")
    };
}

/// <summary>Every pull request a stack needs, computed from a server snapshot before anything is written.</summary>
public sealed record PullRequestPublicationPlan(string StackId, string StackName, IReadOnlyList<PullRequestStep> Steps, IReadOnlyList<string> Blockers)
{
    public bool CanPublish => Blockers.Count == 0 && PendingCount > 0;
    public int PendingCount => Steps.Count(step => step.Action != PullRequestAction.Unchanged);
}

/// <summary>Matches stack layers to open pull requests by exact repository and branch identity, never by title or history.</summary>
public static class PullRequestPlanner
{
    /// <summary>Reports the pull request each layer needs and refuses ambiguous or non-local layers instead of guessing.</summary>
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
