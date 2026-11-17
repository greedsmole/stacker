using Stacker.Core.Framework;
namespace Stacker.Core.Framework;

/// <summary>A user-visible mutation; the only code allowed to change git, disk or GitHub state.</summary>
/// <remarks>Feature code runs a command through the command bus, which permits one foreground mutation at a time,
/// so a command may assume it is the only writer. Planning is read-only; the runner opens the journal only after a
/// runnable plan, and converts an escaping exception into a failure and, when possible, a compensation.</remarks>
public interface IStackCommand
{
    /// <summary>Stable command kind, recorded as the journal entry's kind and used to filter operations.</summary>
    string Kind
    {
        get;
    }
    /// <summary>One line for the UI describing what the command will do; must not change state.</summary>
    /// <returns>The display text.</returns>
    string Describe();
    /// <summary>Read-only preconditions; a plan that cannot run never opens a journal entry.</summary>
    /// <param name="ct">Cancellation for the checks.</param>
    /// <returns>The plan, including any blockers and the effects to journal.</returns>
    Task<CommandPlan> PlanAsync(CancellationToken ct = default);
    /// <summary>Runs the effects and reports what actually happened.</summary>
    /// <remarks>The command records each effect through <paramref name="context"/>'s journal and returns a status
    /// that matches its durable steps. The runner performs the final close and any compensation; an adapter may close
    /// the entry earlier when it stops to ask the user for a decision.</remarks>
    /// <param name="context">The running entry, journal and progress sink.</param>
    /// <param name="ct">Cancellation for the effects.</param>
    /// <returns>The outcome of this execution.</returns>
    Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default);
}
