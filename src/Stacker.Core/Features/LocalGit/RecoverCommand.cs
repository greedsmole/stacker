using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Aborts an interrupted Git operation and restores the branches recorded in the journal.</summary>
/// <remarks>Recovery aborts a separate Git operation on its own. For an interrupted restack, the adapter matches Git's rebase markers and verifies the recorded refs before restoring them.</remarks>
/// <param name="effect">Adapter that aborts and restores.</param>
public sealed class RecoverCommand(Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("local.recover", "restack", effect);
