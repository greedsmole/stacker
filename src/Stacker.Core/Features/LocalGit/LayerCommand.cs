using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Creates a new local layer branch from a start ref, recording the new branch ref as the command target.</summary>
/// <remarks>The branch has been validated as a local ref before the command exists; the effect still runs Git's own <c>check-ref-format</c>, requires the name to be unused, and refuses a working tree with tracked changes or an operation in progress. The returned string is the created <c>refs/heads/...</c> ref, which the caller stores as the layer.</remarks>
/// <param name="branch">Validated local branch to create.</param>
/// <param name="effect">Adapter that validates and creates the branch.</param>
public sealed class LayerCommand(LocalBranch branch, Func<CommandContext, CancellationToken, Task<Result<string>>> effect)
    : EffectCommand<string>("local.layer", branch.Ref, effect);
