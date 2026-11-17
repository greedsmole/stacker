using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Publishes stack branches with a force-with-lease against the remote commits captured before the restack.</summary>
/// <remarks>The <c>wholeStack</c> flag decides the branch set when the plan is built: every non-blocked local layer, including layers that were already up to date, versus only the layers this restack needs to rewrite. Each step carries the remote tip from the plan, which the effect uses as the lease value; a remote that moved after the preview therefore makes the push fail instead of overwriting someone else's work. The adapter journals each branch from started to done, so the base class must not add a single wrapper step for a multi-branch push.</remarks>
/// <param name="plan">Approved plan whose captured remote tips are the lease values.</param>
/// <param name="wholeStack">Whether to publish every local layer or only the rewritten ones.</param>
/// <param name="effect">Adapter that verifies the remote snapshot and pushes each branch.</param>
public sealed class PushCommand(RestackPlan plan, bool wholeStack, Func<CommandContext, CancellationToken, Task<Result<IReadOnlyList<string>>>> effect)
    : EffectCommand<IReadOnlyList<string>>("local.push", plan.StackId, effect)
{
    /// <summary>Whether the adapter writes the journal steps itself instead of letting the base class write one wrapper step.</summary>
    /// <remarks>The push adapter journals every branch around its actual remote write, including the lease value, so a partial push remains visible in the entry.</remarks>
    protected override bool AdapterJournalsSteps => true;
    /// <summary>Returns one push step per branch the chosen mode will publish, using the plan's captured remote tip as the old value.</summary>
    /// <remarks>Whole-stack mode selects every non-blocked local layer, so an out-of-date remote branch is moved too; normal mode selects only the layers this restack needs to rewrite. The plan is always runnable because origin presence, an empty branch set and remote drift are re-checked by the effect at execution time rather than at planning time. Cancellation is not observed because the projection completes synchronously.</remarks>
    /// <param name="ct">Unused; the projection is synchronous.</param>
    public override Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready(
        (wholeStack ? plan.StackPushSteps : plan.PushSteps).Select(step =>
            new JournalStep(step.Branch, "push", step.Branch, step.RemoteTip, null, StepState.Planned)).ToArray()));
}
