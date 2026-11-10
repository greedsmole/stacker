namespace Stacker.Core.Framework;

/// <summary>A typed effect boundary: adapters return a value/problem; only the runner closes the outcome.</summary>
/// <param name="kind">The journal kind and default description, such as <c>local.commit</c>.</param>
/// <param name="target">The journal target the effect names, such as a branch ref, pull request id or repository root.</param>
/// <param name="effect">The adapter callback that performs the effect and returns its typed result.</param>
/// <typeparam name="T">The value the effect produces on success.</typeparam>
/// <remarks>This base class removes the repeated boilerplate around one adapter call. For the default single-step
/// plan it records that step's Started and final state, keeps the typed <see cref="Result{T}"/> for the facade that
/// called the bus, and translates the result into the status vocabulary the runner understands. It never closes the
/// journal entry and never decides recovery; that stays with <see cref="CommandRunner"/>. A command whose adapter
/// records several effects itself overrides <see cref="PlanAsync"/> and sets
/// <see cref="AdapterJournalsSteps"/>.</remarks>
public abstract class EffectCommand<T>(string kind, string target,
    Func<CommandContext, CancellationToken, Task<Result<T>>> effect) : IStackCommand
{
    /// <summary>The command kind recorded in the journal, used to label entries and to enforce operation-specific
    /// guards such as allowing one unfinished restack.</summary>
    public string Kind => kind;
    /// <summary>The typed result of the effect, available to the caller after the bus reports.</summary>
    /// <remarks>Null before execution and when the effect throws rather than returns. A failed result keeps the
    /// original <see cref="Problem"/>, including its <see cref="EffectCertainty"/>, which the normalized
    /// <see cref="CommandOutcome"/> also carries. The facade that constructed the command unwraps this after
    /// <see cref="CommandBus"/> returns, returning the value or rethrowing the problem inside its own reporting
    /// boundary.</remarks>
    public Result<T>? Value { get; private set; }
    /// <summary>User-facing description used when an applied effect has no richer message.</summary>
    /// <returns>The kind by default; subclasses can override with a clearer label.</returns>
    public virtual string Describe() => kind;
    /// <summary>Produces the default read-only plan of one journaled effect.</summary>
    /// <param name="ct">Ignored; the default plan reads nothing and cannot be blocked.</param>
    /// <returns>A ready plan with a single <see cref="StepState.Planned"/> step whose id is <c>effect</c>, whose kind
    /// is <see cref="Kind"/> and whose target is the command target.</returns>
    /// <remarks>Subclasses with blockers or several effects override this and usually set
    /// <see cref="AdapterJournalsSteps"/> as well. The runner refuses a plan whose <see cref="CommandPlan.CanRun"/>
    /// is false before opening a journal entry.</remarks>
    public virtual Task<CommandPlan> PlanAsync(CancellationToken ct = default) =>
        Task.FromResult(CommandPlan.Ready([JournalStep.Planned("effect", kind, target)]));

    /// <summary>Whether the adapter records each step's state through the journal itself.</summary>
    /// <remarks>False by default, so this class owns the single default step: it writes Started, then Failed or
    /// Unknown, then Done. A multi-effect command sets this true because its plan has no single <c>effect</c> step and
    /// its adapter knows when each branch or layer has landed; the base then leaves journaling entirely to that
    /// adapter. Only a subclass can know which convention its plan and effect follow.</remarks>
    protected virtual bool AdapterJournalsSteps => false;
    /// <summary>Runs the effect, records its step state and translates the typed result into an outcome.</summary>
    /// <param name="context">The opened entry, journal and progress sink for this run.</param>
    /// <param name="ct">The bus-linked token; cancellation propagates to the adapter and to journal writes.</param>
    /// <returns>Done for an applied effect, Restored for an adapter-reported rollback, Refused when the adapter
    /// reports a refused restack, Attention for a conflict or an entry that still needs the user, and Failed with the
    /// effect's problem when the adapter returned a failure. The runner decides the journal phase and any
    /// compensation; this method only describes what the command observed.</returns>
    /// <exception cref="StackerException">Thrown when recording the step fails; the runner catches it and takes the
    /// recovery path.</exception>
    /// <remarks>Unless <see cref="AdapterJournalsSteps"/> is set, the first planned step is marked Started before the
    /// effect and finished afterwards. A failure marks Failed normally, but Unknown when the problem's certainty is
    /// <see cref="EffectCertainty.Unknown"/>: after a lost response the code cannot claim the effect did not happen,
    /// so the step records that uncertainty rather than a clean failure, and the runner reaches the same conclusion
    /// from the problem's certainty and preserves the entry for recovery. On success the step is marked Done and its
    /// new SHA is recorded only when the typed value is a string that parses as a complete commit id (40- or
    /// 64-character lowercase hex), so a branch name, stash message or pull request reference never masquerades as a
    /// commit. A <see cref="RestackOutcome"/> is translated by its own status: a conflict becomes
    /// <see cref="CommandStatus.NeedsUser"/> so the in-progress rebase is not treated as applied, a rollback becomes
    /// <see cref="CommandStatus.RolledBack"/> because the adapter already restored the refs, and a refusal becomes
    /// <see cref="CommandStatus.NeedsUser"/> when the adapter left an entry needing attention or
    /// <see cref="CommandStatus.Refused"/> otherwise. Every other value yields Done with <see cref="Describe"/>.
    /// The default plan always contains the one step this class journals; a subclass that overrides the plan must
    /// either keep that guarantee or set <see cref="AdapterJournalsSteps"/> so the base never touches a step it did
    /// not create.</remarks>
    public async Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        if (!AdapterJournalsSteps)
            (await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0] with { State = StepState.Started }, ct)).OrThrow();
        Value = await effect(context, ct);
        if (Value.IsFailed)
        {
            if (!AdapterJournalsSteps) (await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0] with
            { State = Value.Problem!.Certainty == EffectCertainty.Unknown ? StepState.Unknown : StepState.Failed }, ct)).OrThrow();
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
