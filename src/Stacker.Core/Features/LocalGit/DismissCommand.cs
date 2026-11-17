using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Closes the active journal entry as dismissed without changing any Git ref.</summary>
/// <remarks>This is for an entry the user has inspected and chosen not to recover. Dismissal neither restores nor deletes anything, so uncertain effects stay where they are and only their record stops being reported; the effect fails when no entry is active or undoable. The fixed target <c>recovery</c> labels the entry, and the result is <see cref="Unit"/> because there is no value to return.</remarks>
/// <param name="effect">Adapter that closes the entry.</param>
public sealed class DismissCommand(Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("local.dismiss", "recovery", effect);
