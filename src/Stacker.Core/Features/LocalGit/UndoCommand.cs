using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Reverses the newest undoable journal entry, restoring every branch it moved.</summary>
/// <remarks>The effect refuses when the working tree has tracked changes, an operation is in progress, a moved branch is checked out in another worktree, or a branch no longer points at the SHA the journal recorded. Those guards stop an undo from discarding commits made after the restack. Only local refs recorded as moved are restored; remote branches are not touched. The fixed target <c>restack</c> labels the entry.</remarks>
/// <param name="effect">Adapter that checks ownership and restores the refs.</param>
public sealed class UndoCommand(Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("local.undo", "restack", effect);
