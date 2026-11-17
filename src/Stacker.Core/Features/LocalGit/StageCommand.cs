using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Stages selected working-tree paths for the next commit.</summary>
/// <remarks>The target is the newline-joined path list, so the journal shows the staged set; the list itself is passed to the effect unchanged. The effect rejects an empty list and passes the paths after a <c>--</c> separator, so a path beginning with a dash is never read as an option. The returned list is the same set of paths for the caller to confirm.</remarks>
/// <param name="paths">Paths to stage; an empty list is refused by the effect.</param>
/// <param name="effect">Adapter that runs the stage.</param>
public sealed class StageCommand(IReadOnlyList<string> paths, Func<CommandContext, CancellationToken, Task<Result<IReadOnlyList<string>>>> effect)
    : EffectCommand<IReadOnlyList<string>>("local.stage", string.Join("\n", paths), effect);
