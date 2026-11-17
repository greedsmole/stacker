using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Stashes tracked changes under a message and returns the stash commit id, or null when there was nothing tracked to stash.</summary>
/// <remarks>Null is a normal outcome, not a failure: the effect checks for tracked changes first, so a caller that only wanted a clean tree can continue either way. The message is the journal target; the stash commit itself is attached to the active or undoable journal entry by the effect, so undo and restore can still find it once the command has closed.</remarks>
/// <param name="message">Message shown in the stash list and in the journal.</param>
/// <param name="effect">Adapter that creates the stash and records its commit id.</param>
public sealed class StashCommand(string message, Func<CommandContext, CancellationToken, Task<Result<string?>>> effect)
    : EffectCommand<string?>("local.stash", message, effect);
