using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Checks out an existing local branch and records the full ref as the command target.</summary>
/// <remarks><see cref="LocalBranch"/> has already required the <c>refs/heads/</c> prefix and rejected control characters and spaces, so this command can only ask Git for a local branch. The effect refuses while tracked changes or another operation are present and does not create the branch when it is missing. The result is <see cref="Unit"/> because the fresh working-tree state is read separately.</remarks>
/// <param name="branch">Validated local branch to check out.</param>
/// <param name="effect">Adapter that performs the checkout.</param>
public sealed class SwitchCommand(LocalBranch branch, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("local.switch", branch.Ref, effect);
