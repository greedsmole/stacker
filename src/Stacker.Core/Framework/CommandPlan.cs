using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>What a command decided before running: the blockers and the effects it would cause.</summary>
/// <remarks><see cref="IStackCommand.PlanAsync"/> produces this without changing any state. A plan that cannot run
/// makes the runner return a refusal and never opens a journal entry, so a blocked action leaves no recovery
/// record; a runnable plan's effects are journaled before execution begins.</remarks>
/// <param name="CanRun">True when every precondition holds; false makes the runner refuse without any effect.</param>
/// <param name="Blockers">User-facing reasons the command cannot run; joined into the refusal message.</param>
/// <param name="Effects">The effects that will be journaled, in execution order.</param>
public sealed record CommandPlan(bool CanRun, IReadOnlyList<string> Blockers, IReadOnlyList<JournalStep> Effects)
{
    /// <summary>A plan that must not run, carrying the reasons shown to the user.</summary>
    /// <param name="blockers">One or more user-facing reasons.</param>
    /// <returns>A plan with <see cref="CanRun"/> false and no effects.</returns>
    public static CommandPlan Blocked(params string[] blockers) => new(false, blockers, []);
    /// <summary>A runnable plan whose effects will be journaled before execution.</summary>
    /// <param name="effects">The planned effects, in execution order.</param>
    /// <returns>A plan with <see cref="CanRun"/> true.</returns>
    public static CommandPlan Ready(IReadOnlyList<JournalStep> effects) => new(true, [], effects);
    /// <summary>A runnable plan with no planned steps, for commands whose effects are decided while executing.</summary>
    /// <returns>A plan with <see cref="CanRun"/> true and an empty effect list.</returns>
    public static CommandPlan Ready() => new(true, [], []);
}
