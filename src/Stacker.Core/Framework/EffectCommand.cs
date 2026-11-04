namespace Stacker.Core.Framework;

/// <summary>A typed effect boundary: adapters return a value/problem; only the runner closes the outcome.</summary>
public abstract class EffectCommand<T>(string kind, string target,
    Func<CommandContext, CancellationToken, Task<Result<T>>> effect) : IStackCommand
{
    public string Kind => kind;
    public Result<T>? Value { get; private set; }
    public virtual string Describe() => kind;
    public virtual Task<CommandPlan> PlanAsync(CancellationToken ct = default) =>
        Task.FromResult(CommandPlan.Ready([JournalStep.Planned("effect", kind, target)]));

    protected virtual bool AdapterJournalsSteps => false;
    public async Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        if (!AdapterJournalsSteps)
            (await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0] with { State = StepState.Started }, ct)).OrThrow();
        Value = await effect(context, ct);
        if (Value.IsFailed)
        {
            if(!AdapterJournalsSteps)(await context.Journal.StepAsync(context.Entry,context.Entry.Steps[0] with
                {State=Value.Problem!.Certainty==EffectCertainty.Unknown?StepState.Unknown:StepState.Failed},ct)).OrThrow();
            return CommandOutcome.Failed(Value.Problem!);
        }
        if (!AdapterJournalsSteps)
            (await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0].Finished(null,
                Value.Value is string sha && CommitSha.Parse(sha).IsOk ? sha : null, StepState.Done), ct)).OrThrow();
        if (Value.Value is RestackOutcome outcome)
            return outcome.Conflicted ? CommandOutcome.Attention(outcome.Message)
                : outcome.RolledBack ? CommandOutcome.Restored(outcome.Message)
                : outcome.Status == RestackStatus.Refused ? outcome.Journal is { NeedsAttention: true }
                    ? CommandOutcome.Attention(outcome.Message) : CommandOutcome.Refused(outcome.Message)
                : CommandOutcome.Done(outcome.Message);
        return CommandOutcome.Done(Describe());
    }
}
