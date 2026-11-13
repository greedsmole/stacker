using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Restack command that plans one journal step per rewritten layer, preceded by a step recording the branch to return to.</summary>
/// <remarks>The plan is projected from an already-built <see cref="RestackPlan"/>, so this type validates nothing about Git state itself. Repository-level blockers become a <see cref="CommandPlan"/> that cannot run, which makes the runner refuse before opening a journal entry; otherwise each layer that needs a rebase and is not blocked becomes a <c>rebase</c> step whose old value is the layer's pre-move tip. The first <c>original</c> step carries the whole plan on <see cref="JournalStep.RestackPlan"/>, which is what lets resume and undo rebuild the intended sequence from the entry alone. Step indexes are assigned after filtering, matching the order the adapter writes and resumes. The adapter journals its own steps, so the base class must not write a single wrapper step for a run that can stop on one of several layers.</remarks>
/// <param name="plan">Approved plan; its blockers decide whether the command may run.</param>
/// <param name="effect">Adapter that performs the rebases and writes the journal steps.</param>
public sealed class RestackCommand(RestackPlan plan,
    Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("restack", plan.StackId, effect)
{
    /// <summary>Whether the adapter writes the journal steps itself instead of letting the base class write one wrapper step.</summary>
    /// <remarks>The restack adapter records each layer around the actual rebase, including a started step that a conflict leaves open, so a single synthetic step from the base class would describe the wrong effect.</remarks>
    protected override bool AdapterJournalsSteps => true;
    /// <summary>Refuses the command when the plan has blockers, otherwise returns the rebase steps with the original-branch step first.</summary>
    /// <remarks>The returned steps are a snapshot of this command's plan and are written to the journal before execution. Steps are filtered to layers that need a rebase and are not blocked, which is also the exact set the adapter later executes; the index in each id is the filtered position, not the stack position. Cancellation is not observed because the projection completes synchronously.</remarks>
    /// <param name="ct">Unused; the projection is synchronous.</param>
    public override Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(plan.Blockers.Count > 0
        ? CommandPlan.Blocked(plan.Blockers.ToArray())
        : CommandPlan.Ready(plan.Steps.Where(step => step.NeedsRebase && !step.Blocked)
            .Select((step, index) => new JournalStep($"layer:{index}", "rebase", step.Branch, step.BranchTip, null, StepState.Planned))
            .Prepend(new JournalStep("original", "original", plan.OriginalBranch, plan.OriginalBranch, null, StepState.Planned)
            {
                RestackPlan = plan
            }).ToArray()));
}
