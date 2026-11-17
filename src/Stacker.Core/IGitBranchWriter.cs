using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Rewrites stack refs only after an explicit, previewed request; separate from the reader so reads stay provably non-mutating.</summary>
/// <remarks>Restack writes keep backup refs. Remote effects and user commits require explicit recovery when their outcome is uncertain.
/// Every mutation is a separate call on purpose: the caller plans first with <see cref="PlanAsync"/>, shows that plan, and then executes
/// exactly that plan, so a ref that moved between preview and execution is detected instead of silently applied. Only local branches
/// may be rewritten; a remote-tracking layer can be compared but not restacked. Ref-moving calls record a journal entry before their
/// effects, and an interrupted run is resumed or reversed through the journal lookups below rather than by guessing what happened.
/// Expected failures are returned as failed results; cancellation is the only exception that crosses this boundary.</remarks>
public interface IGitBranchWriter
{
    /// <summary>Gathers the read-only facts a plan needs, including refs, worktree occupancy and in-progress operations.</summary>
    /// <remarks>Nothing is written, no journal entry is opened, and an empty blocker list is still not permission to run: the caller
    /// shows the plan and the user approves it. Every SHA the plan will use is captured here, so execution can refuse a layer that
    /// moved in the meantime.</remarks>
    /// <param name="repository">The snapshot the plan is built from.</param>
    /// <param name="stack">The definition whose layers are planned.</param>
    /// <param name="preserveMerges">Replays merge commits instead of flattening them while rebasing.</param>
    /// <param name="ct">Cancels the read-only facts gathering.</param>
    /// <returns>The plan with its steps and blockers.</returns>
    Task<Result<RestackPlan>> PlanAsync(RepositorySnapshot repository, StackDefinition stack, bool preserveMerges = false, CancellationToken ct = default);
    /// <summary>Rebases each planned layer onto its parent; a conflict stays resumable, an unexpected failure restores every touched ref.</summary>
    /// <remarks>A conflict is not rolled back: it leaves Git's rebase in progress, records the pending layer in the journal and returns
    /// a conflicted outcome, because the user must resolve and stage the files before the remaining layers can follow. Any other
    /// failure restores every branch this run moved and reports a rollback. A successful run keeps backup refs so a later undo can
    /// still find the original commits.</remarks>
    /// <param name="plan">The approved plan; its captured SHAs are the expected state.</param>
    /// <param name="progress">Optional per-layer progress text for the sync window.</param>
    /// <param name="ct">Cancels between layers; a cancelled run restores the refs it already moved.</param>
    /// <returns>How the attempt ended, the conflicting layer and files when there were any, and the journal entry that records it.</returns>
    Task<Result<RestackOutcome>> RestackAsync(RestackPlan plan, IProgress<string>? progress = null, CancellationToken ct = default);
    /// <summary>Pushes the restacked branches under a force-with-lease against a freshly fetched remote SHA.</summary>
    /// <remarks>The entire remote snapshot is verified before the first branch is published, and the local source is pinned to a SHA,
    /// so a branch moved after the preview stops the push instead of publishing something else. A partial failure is possible: leases
    /// protect each branch individually, but a multi-branch remote update is not atomic and is not automatically rolled back.</remarks>
    /// <param name="plan">The approved plan whose rewritten layers are pushed.</param>
    /// <param name="ct">Cancels before the next branch; branches already pushed stay published.</param>
    /// <returns>The short names of the branches that were pushed, in push order.</returns>
    Task<Result<IReadOnlyList<string>>> PushAsync(RestackPlan plan, CancellationToken ct = default);
    /// <summary>Returns the newest stack write whose moved refs can still be undone, used to offer undo across restarts.</summary>
    /// <remarks>The journal is the only record of what an earlier run did, so this survives a restart. An interrupted run still counts as
    /// undoable when it moved at least one ref, which is what makes recovery possible after a crash. Null means there is no restack with
    /// recorded moves to undo.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The undoable entry, or <see langword="null"/> when none exists.</returns>
    Task<Result<JournalEntry?>> LastJournalAsync(string root, CancellationToken ct = default);
    /// <summary>Restores the branches of the newest undoable stack write; refused when the working tree is not clean.</summary>
    /// <remarks>Undo checks every affected branch and other worktrees before changing a ref, and only reverses effects that actually
    /// completed. Uncommitted work must be committed or stashed first, because restoring refs under a dirty tree could lose it.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="ct">Cancels before the restore; a cancelled undo leaves refs as they were.</param>
    /// <returns>The outcome of the reversal, with the journal entry that was undone.</returns>
    Task<Result<RestackOutcome>> UndoLastAsync(string root, CancellationToken ct = default);
    /// <summary>Subject line of a branch's newest commit, used as a pull-request title suggestion.</summary>
    /// <remarks>Returns null when the branch has no commits or cannot be resolved, so a missing branch degrades to an empty suggestion
    /// rather than an error dialog.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="branch">Branch ref or name to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The commit subject, or <see langword="null"/> when there is none.</returns>
    Task<Result<string?>> LastCommitSubjectAsync(string root, string branch, CancellationToken ct = default);
    /// <summary>Creates and checks out a new local branch at a start point so work can continue on a new layer.</summary>
    /// <remarks>Refused when the name is invalid or already exists, when the start point cannot be resolved, or when the working tree
    /// has tracked changes or an unfinished operation: creating the layer would otherwise carry someone else's uncommitted work onto
    /// the new branch.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="branchName">Name for the new layer branch.</param>
    /// <param name="startRef">Ref or commit the branch starts at.</param>
    /// <param name="ct">Cancels before the checkout.</param>
    /// <returns>The new branch as a full <c>refs/heads/</c> ref.</returns>
    Task<Result<string>> CreateLayerAsync(string root, string branchName, string startRef, CancellationToken ct = default);
    /// <summary>Pushes every local, unblocked layer of the stack, each under the lease recorded in the plan.</summary>
    /// <remarks>Unlike <see cref="PushAsync"/>, which publishes the rewritten layers, this sends the whole stack including layers that were
    /// already up to date, so a freshly configured stack can be published without a restack first. The same whole-snapshot verification
    /// and per-branch lease apply.</remarks>
    /// <param name="plan">The approved plan whose local layers are pushed.</param>
    /// <param name="ct">Cancels before the next branch; branches already pushed stay published.</param>
    /// <returns>The short names of the branches that were pushed, in push order.</returns>
    Task<Result<IReadOnlyList<string>>> PushStackAsync(RestackPlan plan, CancellationToken ct = default);
    /// <summary>Reads what the working tree holds, including any unfinished operation and unmerged paths.</summary>
    /// <remarks>Untracked files never block a rewrite; only tracked changes do, and those can be stashed. The state is read for display
    /// and for preconditions, and reading it has no side effects.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The current branch, the unfinished operation and every status entry.</returns>
    Task<Result<WorkingTreeState>> StateAsync(string root, CancellationToken ct = default);
    /// <summary>Stages the named paths so a resolved conflict can be continued.</summary>
    /// <remarks>Only the listed paths are added, so staging part of a conflict cannot sweep unrelated edits into the commit. An empty path
    /// list is refused because it would be a no-op that looks like success.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="paths">Repository-relative paths to stage.</param>
    /// <param name="ct">Cancels the stage operation.</param>
    /// <returns>The staged paths, echoing the request.</returns>
    Task<Result<IReadOnlyList<string>>> StageAsync(string root, IReadOnlyList<string> paths, CancellationToken ct = default);
    /// <summary>Commits the working tree, honouring the user's hooks and signing configuration.</summary>
    /// <remarks>Conflicts must be resolved and staged first. With <c>stageAll</c> every change including untracked files is added; without
    /// it something must already be staged, because committing nothing would look like success while leaving the work unrecorded.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="message">The commit message; a blank message is refused before anything runs.</param>
    /// <param name="stageAll">Stages every change, including untracked files, before committing.</param>
    /// <param name="ct">Cancels before the commit starts.</param>
    /// <returns>The commit ID the new HEAD points at.</returns>
    Task<Result<string>> CommitAsync(string root, string message, bool stageAll, CancellationToken ct = default);
    /// <summary>Stashes tracked changes and returns the stash commit, or null when there was nothing to stash.</summary>
    /// <remarks>Untracked files are not stashed, so a layer write never hides work the user has not added to Git. The stash is recorded
    /// against the journal entry that owns it, which is what makes it restorable later instead of only visible in Git's stash list.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="message">Stash message shown in the list.</param>
    /// <param name="ct">Cancels the stash operation.</param>
    /// <returns>The stash commit ID, or <see langword="null"/> when the tree had no tracked changes.</returns>
    Task<Result<string?>> StashAsync(string root, string message, CancellationToken ct = default);
    /// <summary>Pops the recorded stash and clears it from the journal.</summary>
    /// <remarks>Refused while an operation is in progress or tracked changes exist, because a pop needs a clean base to layer the saved
    /// changes onto. A pop that conflicts is not a failure: Git keeps the stash, the returned state shows the conflicted files, and the
    /// record is cleared so recovery is not blocked by a stash the user is already resolving.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="ct">Cancels before the pop.</param>
    /// <returns>The working-tree state after the restore, including any conflicts the pop produced.</returns>
    Task<Result<WorkingTreeState>> RestoreStashAsync(string root, CancellationToken ct = default);
    /// <summary>Lists stashes recorded in the journal, newest first, so the user can choose one to restore.</summary>
    /// <remarks>The default returns an empty list: an adapter that records no stashes simply offers none, which keeps older implementations
    /// source-compatible. A recorded stash is the stash commit plus the journal entry that owns it.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The saved stashes; empty when none were recorded.</returns>
    Task<Result<IReadOnlyList<SavedStash>>> SavedStashesAsync(string root, CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<SavedStash>>.Ok([]));
    /// <summary>Restores one recorded stash by its commit id; the default ignores the id and restores whatever is recorded.</summary>
    /// <remarks>Implementations that list stashes override this to select by <c>sha</c> and refuse an id that is no longer recorded, so a
    /// stale list entry cannot restore the wrong changes. The default exists so an adapter that keeps no stash records still compiles and
    /// behaves like <see cref="RestoreStashAsync"/>.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="sha">Commit id of the recorded stash to restore.</param>
    /// <param name="ct">Cancels before the pop.</param>
    /// <returns>The working-tree state after the restore.</returns>
    Task<Result<WorkingTreeState>> RestoreRecordedStashAsync(string root, string sha, CancellationToken ct = default) =>
        RestoreStashAsync(root, ct);
    /// <summary>Checks out an existing local branch; refused when the working tree has tracked changes.</summary>
    /// <remarks>Also refused while an operation is in progress and when the branch does not exist. Already being on the branch is a
    /// successful no-op, so a caller need not compare the current branch first.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="branch">Branch to check out.</param>
    /// <param name="ct">Cancels before the checkout.</param>
    /// <returns><see cref="Unit"/> on a successful switch or a no-op.</returns>
    Task<Result<Unit>> SwitchAsync(string root, string branch, CancellationToken ct = default);
    /// <summary>Resumes an interrupted rebase after the user resolved and staged the conflicts.</summary>
    /// <remarks>Refused when no rebase is in progress or when conflicts are still unresolved, so the caller can report what remains instead of
    /// asking Git to fail. The editor command is used for the commit message Git asks for while continuing. The remaining layers of the
    /// original plan then run in their original order.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="editorCommand">Editor command for the message prompt, or null to resolve one normally.</param>
    /// <param name="ct">Cancels before the resume; Git's rebase stays in progress.</param>
    /// <returns>The outcome after the rebase was resumed.</returns>
    Task<Result<RestackOutcome>> ContinueAsync(string root, string? editorCommand, CancellationToken ct = default);
    /// <summary>Drops the commit that conflicted and resumes the rebase.</summary>
    /// <remarks>Used when the user decides the commit is not worth keeping: Git drops it, the journal records the layer the rebase rewrote so a
    /// later undo reverses it too, and the remaining layers of the original plan then run in their recorded order.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="ct">Cancels before the resume; Git's rebase stays in progress.</param>
    /// <returns>The outcome after the commit was dropped.</returns>
    Task<Result<RestackOutcome>> SkipAsync(string root, CancellationToken ct = default);
    /// <summary>Aborts the Git operation in progress and restores recorded refs when it belongs to an interrupted restack.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="ct">Cancels before the restore; an abort that cannot finish is reported as a failure.</param>
    /// <returns>The outcome of the recovery, with the journal entry that was closed.</returns>
    Task<Result<RestackOutcome>> AbortAsync(string root, CancellationToken ct = default);
    /// <summary>Restores a specific interrupted restack after checking the journal and the current Git operation.</summary>
    Task<Result<RestackOutcome>> RestoreInterruptedRestackAsync(string root, string entryId, CancellationToken ct = default);
    /// <summary>The newest journal entry that still needs the user: undoable, conflicted, or holding a stash.</summary>
    /// <remarks>Drives the recovery banner. A null result means there is nothing waiting, so the shell can stop offering recovery actions.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The entry needing attention, or <see langword="null"/> when the repository is clean of pending work.</returns>
    Task<Result<JournalEntry?>> ActiveJournalAsync(string root, CancellationToken ct = default);
    /// <summary>Closes the newest entry that still needs the user, without changing the repository.</summary>
    /// <remarks>Dismissal says "I have seen this and I am not undoing it": the journal phase becomes dismissed and later undo offers no longer
    /// include it. It never moves a ref, so any effects the entry recorded stay in place.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="ct">Cancels the journal write.</param>
    /// <returns><see cref="Unit"/> when an entry was dismissed.</returns>
    Task<Result<Unit>> DismissAsync(string root, CancellationToken ct = default);
}
