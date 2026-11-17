using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Continues or skips the rebase that stopped on a conflict.</summary>
/// <remarks>The action is the internal literal <c>continue</c> or <c>skip</c> chosen by the adapter's two entry points, never user text; it is also the journal target, so the record says how the stop was resolved. The effect refuses when no rebase is in progress, and continue additionally refuses unresolved conflicts. After success the pending layer is marked done and the remaining plan can run.</remarks>
/// <param name="action">Action to apply to the stopped rebase.</param>
/// <param name="effect">Adapter that validates the rebase state and resumes it.</param>
public sealed class ResolveConflictCommand(string action, Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("local.resolve", action, effect);
