using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Commits the working tree with a validated message and returns the new commit id.</summary>
/// <remarks>The target is the trimmed <see cref="CommitMessage.Value"/>, so the journal records the validated text. <see cref="CommitMessage"/> has already rejected an empty or over-long message; the effect receives the caller's original string, checks it again for blankness, refuses unresolved conflicts and (without stage-all) an empty index, and passes the message to Git as one process argument. The returned string is the commit SHA read back after Git succeeds, and the base command stores it as the journal step's new value when it parses as a full commit id.</remarks>
/// <param name="message">Validated non-empty commit message.</param>
/// <param name="effect">Adapter that stages if asked and runs the commit.</param>
public sealed class CommitCommand(CommitMessage message, Func<CommandContext, CancellationToken, Task<Result<string>>> effect)
    : EffectCommand<string>("local.commit", message.Value, effect);
