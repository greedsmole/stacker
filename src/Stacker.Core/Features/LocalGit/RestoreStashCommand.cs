using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Restores a stash recorded on a journal entry and returns the working-tree state after the pop.</summary>
/// <remarks>The command takes no stash argument: the adapter resolves the active, undoable or explicitly selected entry and refuses while an operation or tracked change is present. The fixed target <c>stash</c> labels the journal entry. Success returns a fresh state, so the caller does not need a second status read.</remarks>
/// <param name="effect">Adapter that resolves the stash and performs the restore.</param>
public sealed class RestoreStashCommand(Func<CommandContext, CancellationToken, Task<Result<WorkingTreeState>>> effect)
    : EffectCommand<WorkingTreeState>("local.restore-stash", "stash", effect);
