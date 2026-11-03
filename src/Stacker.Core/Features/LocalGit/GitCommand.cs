using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

public sealed class RestackCommand(RestackPlan plan,
    Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("restack", plan.StackId, effect)
{
    protected override bool AdapterJournalsSteps => true;
    public override Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(plan.Blockers.Count > 0
        ? CommandPlan.Blocked(plan.Blockers.ToArray())
        : CommandPlan.Ready(plan.Steps.Where(step => step.NeedsRebase && !step.Blocked)
            .Select((step, index) => new JournalStep($"layer:{index}", "rebase", step.Branch, step.BranchTip, null, StepState.Planned))
            .Prepend(new JournalStep("original", "original", plan.OriginalBranch, plan.OriginalBranch, null, StepState.Planned)
                { RestackPlan = plan }).ToArray()));
}

public sealed class CommitCommand(CommitMessage message, Func<CommandContext, CancellationToken, Task<Result<string>>> effect)
    : EffectCommand<string>("local.commit", message.Value, effect);
public sealed class SwitchCommand(LocalBranch branch, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("local.switch", branch.Ref, effect);
public sealed class LayerCommand(LocalBranch branch, Func<CommandContext, CancellationToken, Task<Result<string>>> effect)
    : EffectCommand<string>("local.layer", branch.Ref, effect);
public sealed class StageCommand(IReadOnlyList<string> paths, Func<CommandContext, CancellationToken, Task<Result<IReadOnlyList<string>>>> effect)
    : EffectCommand<IReadOnlyList<string>>("local.stage", string.Join("\n", paths), effect);
public sealed class StashCommand(string message, Func<CommandContext, CancellationToken, Task<Result<string?>>> effect)
    : EffectCommand<string?>("local.stash", message, effect);
public sealed class RestoreStashCommand(Func<CommandContext, CancellationToken, Task<Result<WorkingTreeState>>> effect)
    : EffectCommand<WorkingTreeState>("local.restore-stash", "stash", effect);
public sealed class PushCommand(RestackPlan plan, bool wholeStack, Func<CommandContext, CancellationToken, Task<Result<IReadOnlyList<string>>>> effect)
    : EffectCommand<IReadOnlyList<string>>("local.push", plan.StackId, effect)
{
    protected override bool AdapterJournalsSteps => true;
    public override Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready(
        (wholeStack ? plan.StackPushSteps : plan.PushSteps).Select(step =>
            new JournalStep(step.Branch, "push", step.Branch, step.RemoteTip, null, StepState.Planned)).ToArray()));
}
public sealed class UndoCommand(Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("local.undo", "restack", effect);
public sealed class RecoverCommand(Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("local.recover", "restack", effect);
public sealed class ResolveConflictCommand(string action, Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("local.resolve", action, effect);
public sealed class DismissCommand(Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("local.dismiss", "recovery", effect);
