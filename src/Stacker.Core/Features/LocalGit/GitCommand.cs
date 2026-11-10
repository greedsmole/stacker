using Stacker.Core.Framework;
namespace Stacker.Core.Features.LocalGit;

/// <summary>Restack command that plans one journal step per rewritten layer, preceded by a step recording the branch to return to.</summary>
/// <remarks>The plan is projected from an already-built <see cref="RestackPlan"/>, so this type validates nothing about Git state itself. Repository-level blockers become a <see cref="CommandPlan"/> that cannot run, which makes the runner refuse before opening a journal entry; otherwise each layer that needs a rebase and is not blocked becomes a <c>rebase</c> step whose old value is the layer's pre-move tip. The first <c>original</c> step carries the whole plan on <see cref="JournalStep.RestackPlan"/>, which is what lets resume and undo rebuild the intended sequence from the entry alone. Step indexes are assigned after filtering, matching the order the adapter writes and resumes. The adapter journals its own steps, so the base class must not write a single wrapper step for a run that can stop on one of several layers.</remarks>
/// <param name="plan">Approved plan; its blockers decide whether the command may run.</param>
/// <param name="effect">Adapter that performs the rebases and writes the journal steps.</param>
public sealed class RestackCommand(RestackPlan plan,
    Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("restack", plan.StackId, effect)
{
    /// <summary>Whether the adapter writes the journal steps itself instead of letting the base class write one wrapper step.</summary>
    /// <remarks>The restack adapter records each layer around the actual rebase, including a started step that a conflict leaves open, so a single synthetic step from the base class would describe the wrong effect.</remarks>
    protected override bool AdapterJournalsSteps => true;
    /// <summary>Refuses the command when the plan has blockers, otherwise returns the rebase steps with the original-branch step first.</summary>
    /// <remarks>The returned steps are a snapshot of this command's plan and are written to the journal before execution. Steps are filtered to layers that need a rebase and are not blocked, which is also the exact set the adapter later executes; the index in each id is the filtered position, not the stack position. Cancellation is not observed because the projection completes synchronously.</remarks>
    /// <param name="ct">Unused; the projection is synchronous.</param>
    public override Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(plan.Blockers.Count > 0
        ? CommandPlan.Blocked(plan.Blockers.ToArray())
        : CommandPlan.Ready(plan.Steps.Where(step => step.NeedsRebase && !step.Blocked)
            .Select((step, index) => new JournalStep($"layer:{index}", "rebase", step.Branch, step.BranchTip, null, StepState.Planned))
            .Prepend(new JournalStep("original", "original", plan.OriginalBranch, plan.OriginalBranch, null, StepState.Planned)
            { RestackPlan = plan }).ToArray()));
}

/// <summary>Commits the working tree with a validated message and returns the new commit id.</summary>
/// <remarks>The target is the trimmed <see cref="CommitMessage.Value"/>, so the journal records the validated text. <see cref="CommitMessage"/> has already rejected an empty or over-long message; the effect receives the caller's original string, checks it again for blankness, refuses unresolved conflicts and (without stage-all) an empty index, and passes the message to Git as one process argument. The returned string is the commit SHA read back after Git succeeds, and the base command stores it as the journal step's new value when it parses as a full commit id.</remarks>
/// <param name="message">Validated non-empty commit message.</param>
/// <param name="effect">Adapter that stages if asked and runs the commit.</param>
public sealed class CommitCommand(CommitMessage message, Func<CommandContext, CancellationToken, Task<Result<string>>> effect)
    : EffectCommand<string>("local.commit", message.Value, effect);
/// <summary>Checks out an existing local branch and records the full ref as the command target.</summary>
/// <remarks><see cref="LocalBranch"/> has already required the <c>refs/heads/</c> prefix and rejected control characters and spaces, so this command can only ask Git for a local branch. The effect refuses while tracked changes or another operation are present and does not create the branch when it is missing. The result is <see cref="Unit"/> because the fresh working-tree state is read separately.</remarks>
/// <param name="branch">Validated local branch to check out.</param>
/// <param name="effect">Adapter that performs the checkout.</param>
public sealed class SwitchCommand(LocalBranch branch, Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("local.switch", branch.Ref, effect);
/// <summary>Creates a new local layer branch from a start ref, recording the new branch ref as the command target.</summary>
/// <remarks>The branch has been validated as a local ref before the command exists; the effect still runs Git's own <c>check-ref-format</c>, requires the name to be unused, and refuses a working tree with tracked changes or an operation in progress. The returned string is the created <c>refs/heads/...</c> ref, which the caller stores as the layer.</remarks>
/// <param name="branch">Validated local branch to create.</param>
/// <param name="effect">Adapter that validates and creates the branch.</param>
public sealed class LayerCommand(LocalBranch branch, Func<CommandContext, CancellationToken, Task<Result<string>>> effect)
    : EffectCommand<string>("local.layer", branch.Ref, effect);
/// <summary>Stages selected working-tree paths for the next commit.</summary>
/// <remarks>The target is the newline-joined path list, so the journal shows the staged set; the list itself is passed to the effect unchanged. The effect rejects an empty list and passes the paths after a <c>--</c> separator, so a path beginning with a dash is never read as an option. The returned list is the same set of paths for the caller to confirm.</remarks>
/// <param name="paths">Paths to stage; an empty list is refused by the effect.</param>
/// <param name="effect">Adapter that runs the stage.</param>
public sealed class StageCommand(IReadOnlyList<string> paths, Func<CommandContext, CancellationToken, Task<Result<IReadOnlyList<string>>>> effect)
    : EffectCommand<IReadOnlyList<string>>("local.stage", string.Join("\n", paths), effect);
/// <summary>Stashes tracked changes under a message and returns the stash commit id, or null when there was nothing tracked to stash.</summary>
/// <remarks>Null is a normal outcome, not a failure: the effect checks for tracked changes first, so a caller that only wanted a clean tree can continue either way. The message is the journal target; the stash commit itself is attached to the active or undoable journal entry by the effect, so undo and restore can still find it once the command has closed.</remarks>
/// <param name="message">Message shown in the stash list and in the journal.</param>
/// <param name="effect">Adapter that creates the stash and records its commit id.</param>
public sealed class StashCommand(string message, Func<CommandContext, CancellationToken, Task<Result<string?>>> effect)
    : EffectCommand<string?>("local.stash", message, effect);
/// <summary>Restores a stash recorded on a journal entry and returns the working-tree state after the pop.</summary>
/// <remarks>The command takes no stash argument: the adapter resolves the active, undoable or explicitly selected entry and refuses while an operation or tracked change is present. The fixed target <c>stash</c> labels the journal entry. Success returns a fresh state, so the caller does not need a second status read.</remarks>
/// <param name="effect">Adapter that resolves the stash and performs the restore.</param>
public sealed class RestoreStashCommand(Func<CommandContext, CancellationToken, Task<Result<WorkingTreeState>>> effect)
    : EffectCommand<WorkingTreeState>("local.restore-stash", "stash", effect);
/// <summary>Publishes stack branches with a force-with-lease against the remote commits captured before the restack.</summary>
/// <remarks>The <c>wholeStack</c> flag decides the branch set when the plan is built: every non-blocked local layer, including layers that were already up to date, versus only the layers this restack needs to rewrite. Each step carries the remote tip from the plan, which the effect uses as the lease value; a remote that moved after the preview therefore makes the push fail instead of overwriting someone else's work. The adapter journals each branch from started to done, so the base class must not add a single wrapper step for a multi-branch push.</remarks>
/// <param name="plan">Approved plan whose captured remote tips are the lease values.</param>
/// <param name="wholeStack">Whether to publish every local layer or only the rewritten ones.</param>
/// <param name="effect">Adapter that verifies the remote snapshot and pushes each branch.</param>
public sealed class PushCommand(RestackPlan plan, bool wholeStack, Func<CommandContext, CancellationToken, Task<Result<IReadOnlyList<string>>>> effect)
    : EffectCommand<IReadOnlyList<string>>("local.push", plan.StackId, effect)
{
    /// <summary>Whether the adapter writes the journal steps itself instead of letting the base class write one wrapper step.</summary>
    /// <remarks>The push adapter journals every branch around its actual remote write, including the lease value, so a partial push remains visible in the entry.</remarks>
    protected override bool AdapterJournalsSteps => true;
    /// <summary>Returns one push step per branch the chosen mode will publish, using the plan's captured remote tip as the old value.</summary>
    /// <remarks>Whole-stack mode selects every non-blocked local layer, so an out-of-date remote branch is moved too; normal mode selects only the layers this restack needs to rewrite. The plan is always runnable because origin presence, an empty branch set and remote drift are re-checked by the effect at execution time rather than at planning time. Cancellation is not observed because the projection completes synchronously.</remarks>
    /// <param name="ct">Unused; the projection is synchronous.</param>
    public override Task<CommandPlan> PlanAsync(CancellationToken ct = default) => Task.FromResult(CommandPlan.Ready(
        (wholeStack ? plan.StackPushSteps : plan.PushSteps).Select(step =>
            new JournalStep(step.Branch, "push", step.Branch, step.RemoteTip, null, StepState.Planned)).ToArray()));
}
/// <summary>Reverses the newest undoable journal entry, restoring every branch it moved.</summary>
/// <remarks>The effect refuses when the working tree has tracked changes, an operation is in progress, a moved branch is checked out in another worktree, or a branch no longer points at the SHA the journal recorded. Those guards stop an undo from discarding commits made after the restack. Only local refs recorded as moved are restored; remote branches are not touched. The fixed target <c>restack</c> labels the entry.</remarks>
/// <param name="effect">Adapter that checks ownership and restores the refs.</param>
public sealed class UndoCommand(Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("local.undo", "restack", effect);
/// <summary>Aborts an interrupted Git operation and restores the branches recorded in the journal.</summary>
/// <remarks>Recovery first aborts whatever operation is in progress, then refuses if the working tree now has tracked changes. It restores only refs whose current commit still equals the new SHA recorded for it; anything it cannot prove is preserved and reported instead of overwritten. The fixed target <c>restack</c> labels the entry; an entry that is not an unfinished restack is treated as absent so that an old applied record cannot be reopened accidentally.</remarks>
/// <param name="effect">Adapter that aborts and restores.</param>
public sealed class RecoverCommand(Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("local.recover", "restack", effect);
/// <summary>Continues or skips the rebase that stopped on a conflict.</summary>
/// <remarks>The action is the internal literal <c>continue</c> or <c>skip</c> chosen by the adapter's two entry points, never user text; it is also the journal target, so the record says how the stop was resolved. The effect refuses when no rebase is in progress, and continue additionally refuses unresolved conflicts. After success the pending layer is marked done and the remaining plan can run.</remarks>
/// <param name="action">Action to apply to the stopped rebase.</param>
/// <param name="effect">Adapter that validates the rebase state and resumes it.</param>
public sealed class ResolveConflictCommand(string action, Func<CommandContext, CancellationToken, Task<Result<RestackOutcome>>> effect)
    : EffectCommand<RestackOutcome>("local.resolve", action, effect);
/// <summary>Closes the active journal entry as dismissed without changing any Git ref.</summary>
/// <remarks>This is for an entry the user has inspected and chosen not to recover. Dismissal neither restores nor deletes anything, so uncertain effects stay where they are and only their record stops being reported; the effect fails when no entry is active or undoable. The fixed target <c>recovery</c> labels the entry, and the result is <see cref="Unit"/> because there is no value to return.</remarks>
/// <param name="effect">Adapter that closes the entry.</param>
public sealed class DismissCommand(Func<CommandContext, CancellationToken, Task<Result<Unit>>> effect)
    : EffectCommand<Unit>("local.dismiss", "recovery", effect);
