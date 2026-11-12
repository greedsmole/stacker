using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Defines the process boundary used by Git and GitHub CLI adapters.</summary>
/// <remarks>Keeping arguments separate from the executable prevents shell interpretation and makes cancellation testable.
/// Every port in this file follows one failure contract: an expected failure - a tool that cannot start, a timeout, output over
/// its limit, or a refusal reported by an adapter - comes back as a failed <see cref="Result{T}"/> carrying a catalogued
/// <see cref="Problem"/>, never as an exception crossing the boundary; only cancellation is rethrown as
/// <see cref="OperationCanceledException"/>. <see cref="Result{T}"/> is a reference type on purpose, so an unguarded default is
/// a null reference rather than a silent success, and a successful call always carries a value.</remarks>
public interface IProcessRunner
{
    /// <summary>Runs one bounded process operation and returns both output streams and its exit code.</summary>
    /// <remarks>Both streams are captured and drained concurrently, so a tool that fills one pipe cannot deadlock against a reader
    /// waiting on the other. A nonzero exit code is returned as data in <see cref="ProcessResult"/> rather than as a failed result:
    /// the meaning of an exit code belongs to the adapter that issued the command. The call fails when the process cannot be
    /// started, when combined output passes <see cref="ProcessRequest.MaxOutputBytes"/> (the failure is an
    /// <see cref="OutputLimitException"/>), or when it runs past its timeout; in those cases the child process tree is killed before
    /// the problem is returned. <see cref="ProcessRequest.StandardInput"/> is written and then closed, so a tool that waits for
    /// end-of-input proceeds instead of hanging.</remarks>
    /// <param name="request">The executable, its already-split arguments and the resource bounds for this run.</param>
    /// <param name="ct">Cancels the run and kills the child; cancellation is an exception, not a problem.</param>
    /// <returns>The exit code and the decoded standard output and error streams; an empty stream is an empty string, not null.</returns>
    Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken ct = default);
}
/// <summary>Starts a program without waiting for it or capturing its output, for editors and other interactive tools.</summary>
/// <remarks>Fire-and-forget by design: an editor stays open for as long as the user wants, so there is no timeout, no output
/// capture and no exit code to report. A program that cannot be started fails the result with a problem the settings dialog can
/// show; everything after a successful start is outside the app's control and outside its recovery model.</remarks>
public interface IProcessLauncher
{
    /// <summary>Launches the process and returns immediately; a program that cannot be started comes back as a failed result.</summary>
    /// <remarks>The started process receives the request's arguments, working directory and environment edits but no redirected
    /// streams, so it keeps running after this call returns and after the app exits. A successful result means "handed to the
    /// operating system", not that the tool did its job, and it never reports the tool's later exit status.</remarks>
    /// <param name="request">The executable, arguments and environment to launch with.</param>
    /// <returns><see cref="Unit"/> on a successful start.</returns>
    Result<Unit> Start(ProcessRequest request);
}
/// <summary>Resolves and opens the text editor the user chose, without ever involving a shell.</summary>
/// <remarks>Resolution is layered so a user can rely on Git's own configuration without repeating it in Stacker, while a machine
/// with no editor configured still gets a usable default. Both methods return the command text that was resolved, so the caller
/// can show the user exactly what will run instead of guessing.</remarks>
public interface IEditorLauncher
{
    /// <summary>Explicit setting, then git core.editor, then GIT_EDITOR/VISUAL/EDITOR, then the platform default.</summary>
    /// <remarks>Each candidate is trimmed and a blank one falls through to the next, so an empty setting never wins. Reading Git's
    /// configuration is a bounded, non-interactive read that does not change repository state. The platform default is supplied by
    /// the adapter, which is why this port normally resolves something even on a bare system; null is reserved for an adapter that
    /// truly cannot resolve any command.</remarks>
    /// <param name="root">Repository root used to read Git's configuration.</param>
    /// <param name="overrideCommand">The user's saved editor setting, or null to fall back to the layers below it.</param>
    /// <param name="ct">Cancels the configuration read.</param>
    /// <returns>The command text to show and reuse, or <see langword="null"/> when no candidate resolved.</returns>
    Task<Result<string?>> ResolveAsync(string root, string? overrideCommand, CancellationToken ct = default);
    /// <summary>Opens the paths in the resolved editor and returns the command that was used, or null when none resolved.</summary>
    /// <remarks>Paths are appended as separate arguments after the command, so a path with spaces needs no quoting here and never
    /// reaches a shell. The launch is detached: this method returns as soon as the editor process has started, and the returned
    /// command is only a report of what was launched.</remarks>
    /// <param name="root">Repository root used to resolve the editor when no setting is supplied.</param>
    /// <param name="paths">Files to open, passed as separate arguments.</param>
    /// <param name="overrideCommand">The user's saved editor setting, or null to resolve from the usual order.</param>
    /// <param name="ct">Cancels the resolution step; the launched editor is not affected.</param>
    /// <returns>The command that was launched, or <see langword="null"/> when nothing could be resolved.</returns>
    Task<Result<string?>> OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct = default);
}
/// <summary>Reads repository history and file content without changing refs, the index, or the working tree.</summary>
/// <remarks>The read side of Git, kept separate from <see cref="IGitBranchWriter"/> so a caller can prove that a screen or a
/// comparison only reads. Every method takes an explicit root and explicit commit IDs, and the shipped adapter runs non-interactive
/// commands with paging, prompts and optional index writes disabled, so opening, refreshing or comparing a repository does not
/// leave a lock, a changed ref or a half-written index behind. It also validates commit IDs before building a command, so a stale
/// snapshot produces a problem rather than a command assembled from arbitrary text.</remarks>
public interface IGitRepositoryReader
{
    /// <summary>Opens a repository and captures its refs and working-tree change count.</summary>
    /// <remarks>Accepts a path inside a working tree and resolves the root from it. The snapshot keeps direct local and
    /// remote-tracking refs only - symbolic refs such as <c>origin/HEAD</c> are skipped - and counts working-tree status entries,
    /// including untracked files, with a rename counted once. A folder that does not exist or is not a working tree is a problem,
    /// never an empty snapshot.</remarks>
    /// <param name="path">A folder inside the working tree to open.</param>
    /// <param name="ct">Cancels the underlying reads.</param>
    /// <returns>The root, refs, working-tree summary and, when available, the registered repository identity.</returns>
    Task<Result<RepositorySnapshot>> OpenAsync(string path, CancellationToken ct = default);
    /// <summary>Returns every best common ancestor; callers reject zero or ambiguous results.</summary>
    /// <remarks>More than one entry means the histories meet at several equally good points, so any single choice would make a
    /// comparison arbitrary; an empty list means the two commits share no history. The caller decides whether ambiguity is
    /// acceptable, which is why this method reports all answers instead of picking one.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="left">A commit ID in the repository.</param>
    /// <param name="right">Another commit ID in the repository.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Every best common ancestor, in Git's order.</returns>
    Task<Result<IReadOnlyList<string>>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default);
    /// <summary>Counts commits unique to each side relative to the proposed parent/head pair.</summary>
    /// <remarks>Ahead counts commits reachable from the head but not the parent; behind counts parent commits missing from the head.
    /// The pair is a divergence measure, not a merge status: a nonzero behind value is exactly when a merge-base diff stops being a
    /// preview of the merge result.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="parent">The parent-side commit ID.</param>
    /// <param name="head">The layer-side commit ID.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The head's own commit count and the parent's unmerged commit count.</returns>
    Task<Result<(int Ahead, int Behind)>> CountsAsync(string root, string parent, string head, CancellationToken ct = default);
    /// <summary>Reads machine-parsed file metadata for a fixed pair of commit IDs.</summary>
    /// <remarks>One <see cref="FileChange"/> per changed path: kind, old and new path, line counts and file modes. The output is
    /// parsed from NUL-delimited metadata so spaces, Unicode and newlines in paths stay unambiguous, and renames are detected by
    /// content similarity - a rename Git cannot detect appears as a delete plus an add. No patch text is read here, making this the
    /// cheap call that fills a comparison's file list before any large content is loaded.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="base">The base-side commit ID, normally the merge base.</param>
    /// <param name="head">The head-side commit ID.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>File metadata in Git's report order.</returns>
    Task<Result<IReadOnlyList<FileChange>>> FilesAsync(string root, string @base, string head, CancellationToken ct = default);
    /// <summary>Loads the patch for one previously listed file, subject to the display-size limit.</summary>
    /// <remarks>The file must come from <see cref="FilesAsync"/> for the same commit pair: both paths are used to select the patch
    /// section, because a rename whose old path was reused by another file would otherwise return the wrong one. Output is bounded
    /// by the adapter's display limit; a file over it fails with <see cref="OutputLimitException"/> instead of being truncated, so
    /// the UI can offer a smaller comparison. Hunk headers, line coordinates and the flat line list are parsed here once and reused
    /// for rendering, search, review anchors and history analysis.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="base">The same base-side commit ID used for the file list.</param>
    /// <param name="head">The same head-side commit ID used for the file list.</param>
    /// <param name="file">One entry from <see cref="FilesAsync"/> for this commit pair.</param>
    /// <param name="ct">Cancels the read and kills the child process when the adapter runs one.</param>
    /// <returns>The raw patch, its hunks and a flat line stream.</returns>
    Task<Result<FileDiff>> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default);
}
/// <summary>Persists user-authored stack definitions in the repository configuration.</summary>
/// <remarks>Revisioned rather than locked for its whole lifetime: a load returns the revision of the bytes it read, and a save
/// must present that revision so a second window or an external editor is not overwritten silently. A write takes an exclusive
/// lock kept outside the working tree and replaces the configuration atomically, so a crash cannot leave a half-written document
/// and a failed save leaves the previous one intact.</remarks>
public interface IStackStore
{
    /// <summary>Loads and validates definitions while capturing a revision for optimistic concurrency.</summary>
    /// <remarks>An absent configuration is not a failure: it loads as an empty document with a null revision, and a save can then
    /// create the document. Stored definitions are validated with the same rules as saving, so a hand-edited document that would
    /// break the workspace is reported with the file untouched instead of rendering a broken stack list.</remarks>
    /// <param name="root">Repository root that owns the configuration.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The definitions plus the revision of the bytes that produced them; null when nothing is stored.</returns>
    Task<Result<StackDocument>> LoadAsync(string root, CancellationToken ct = default);
    /// <summary>Saves only when the on-disk revision still matches the revision last read.</summary>
    /// <remarks>Three checks run in order: the definitions are validated, the revision read back from disk is compared with
    /// <c>expectedRevision</c>, and the bytes are compared again immediately before the atomic replace, so an edit that lands
    /// between validation and writing is still detected. A null revision on disk means no document exists, so a first save is
    /// allowed even when the caller has nothing to compare against; a null expected revision against an existing document is a
    /// stale-save problem. The returned document carries the new revision, so the caller can save again without reloading.</remarks>
    /// <param name="root">Repository root that owns the configuration.</param>
    /// <param name="stacks">Every definition to write; the document is replaced as a whole.</param>
    /// <param name="expectedRevision">The revision returned by the last load, or null when the caller believes nothing exists.</param>
    /// <param name="ct">Cancels before the replace; a cancelled save leaves the previous document intact.</param>
    /// <returns>The saved definitions and their new revision.</returns>
    Task<Result<StackDocument>> SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct = default);
}
/// <summary>Reads source blobs by immutable commit and path for syntax-aware diff rendering.</summary>
/// <remarks>Blob IDs are content-addressed, so highlighted output can be reused for every comparison that shows the same content.
/// The read is bounded and optional: a blob over the adapter's limit, or one that is not present locally, comes back as
/// <see langword="null"/> rather than failing the comparison, because missing highlighting is cosmetic while a missing file list is
/// not.</remarks>
public interface IBlobReader
{
    /// <summary>Returns the blob at one path in one commit, or null when it does not exist there.</summary>
    /// <remarks>An absent path is normal and not an error: an added file has no old-side blob and a deleted file has no new-side
    /// blob, and the caller renders that side unhighlighted. The read is a local object lookup: a partial clone must not start a
    /// network fetch from a renderer, so missing content is reported as absent instead of downloaded.</remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="commit">The immutable commit ID whose tree is read.</param>
    /// <param name="path">Repository-relative path of the blob at that commit.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The blob id and text, or <see langword="null"/> for an absent or unavailable blob.</returns>
    Task<Result<BlobContent?>> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default);
}
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
/// <summary>Stores per-user application preferences outside the repository.</summary>
/// <remarks>Preferences are not part of a repository's history, so they live in application data and are saved as one document. There is no
/// revision parameter: the shell is the only writer in normal use, and a save replaces the whole object. An absent document loads as
/// defaults; a damaged one is preserved and reported by the adapter instead of being deleted, and a document written by a newer schema
/// blocks later writes so an older build cannot drop fields it does not understand.</remarks>
public interface ISettingsStore
{
    /// <summary>Reads the stored preferences, falling back to defaults when nothing usable is stored.</summary>
    /// <remarks>Defaults are the property initializers on <see cref="AppSettings"/>, so the loaded object is always complete and safe to bind.
    /// Loading also normalizes values that cannot be trusted - window and column sizes, theme name - which is why callers do not need to
    /// re-validate what they read.</remarks>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>A complete settings object; never null.</returns>
    Task<Result<AppSettings>> LoadAsync(CancellationToken ct = default);
    /// <summary>Replaces the stored preferences with one complete object.</summary>
    /// <remarks>The write is atomic and serialized inside the adapter, so a crash leaves the previous document readable. A document from a
    /// newer schema is preserved: once a load has reported it, writes stay blocked with a problem that explains the version conflict.</remarks>
    /// <param name="settings">The object to store; it is stamped with the current schema version.</param>
    /// <param name="ct">Cancels before the replace.</param>
    /// <returns><see cref="Unit"/> when the document was replaced.</returns>
    Task<Result<Unit>> SaveAsync(AppSettings settings, CancellationToken ct = default);
}
