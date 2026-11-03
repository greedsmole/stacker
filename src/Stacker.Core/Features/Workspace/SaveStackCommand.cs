using Stacker.Core.Framework;
namespace Stacker.Core.Features.Workspace;

public sealed class SaveStackCommand(RepoRoot root, Func<CommandContext, CancellationToken, Task<Result<StackDocument>>> effect)
    : EffectCommand<StackDocument>("workspace.save-stack", root.Value, effect);
