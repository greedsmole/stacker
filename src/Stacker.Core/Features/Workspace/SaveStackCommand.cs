using Stacker.Core.Framework;
namespace Stacker.Core.Features.Workspace;

/// <summary>Journaled command that saves the stack definition document through an effect supplied by the caller.</summary>
/// <param name="root">Validated repository root, recorded as the entry's target; the caller passes the same root to
/// the bus, which uses it as the journal scope.</param>
/// <param name="effect">The save to run under the journal. It returns the saved document or a catalogued problem and
/// is invoked at most once per execution.</param>
/// <remarks>Saving the stack document mutates user configuration outside Git, and before the command boundary an
/// interrupted save left no way to tell whether the previous revision had been replaced. Here the shared bus opens a
/// write-ahead journal entry, the base class records one <c>effect</c> step while the write runs and marks that step
/// done, failed or unknown, and the runner closes the entry with the outcome. The inherited plan has no blockers, so
/// an admitted command always opens an entry and a stale revision is reported as a failed effect rather than as a
/// refusal. Because the command is not undoable, a failure after the write ran closes the entry as needing the user
/// instead of claiming a rollback. All storage work stays in the effect, so this type holds no I/O and the adapter
/// keeps its own expected-revision check; the stack store adapter translates the document store's refusal into
/// <see cref="ProblemCodes.StoreWrite"/>. The saved document is left on <see cref="EffectCommand{T}.Value"/> so the
/// caller can return it after the bus reported the outcome.</remarks>
public sealed class SaveStackCommand(RepoRoot root, Func<CommandContext, CancellationToken, Task<Result<StackDocument>>> effect)
    : EffectCommand<StackDocument>("workspace.save-stack", root.Value, effect);
