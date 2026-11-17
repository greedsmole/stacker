namespace Stacker.Core;

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
    public string? ContextIdentity
    {
        get; init;
    }
    /// <summary>Local repository root at check time, set by the caller; publication refuses a plan from another checkout.</summary>
    public string? RepositoryRoot
    {
        get; init;
    }
    /// <summary>True only when there are no blockers and at least one step has an effect.</summary>
    public bool CanPublish => Blockers.Count == 0 && PendingCount > 0;
    /// <summary>Number of layers that need a pull request created or retargeted.</summary>
    public int PendingCount => Steps.Count(step => step.Action != PullRequestAction.Unchanged);
}
