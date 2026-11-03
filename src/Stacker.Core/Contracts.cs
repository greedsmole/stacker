using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Defines the process boundary used by Git and GitHub CLI adapters.</summary>
/// <remarks>Keeping arguments separate from the executable prevents shell interpretation and makes cancellation testable.</remarks>
public interface IProcessRunner
{
    /// <summary>Runs one bounded process operation and returns both output streams and its exit code.</summary>
    Task<Result<ProcessResult>> RunAsync(ProcessRequest request, CancellationToken ct = default);
}
/// <summary>Starts a program without waiting for it or capturing its output, for editors and other interactive tools.</summary>
public interface IProcessLauncher
{
    /// <summary>Launches the process and returns immediately; a missing program is reported as a StackerException.</summary>
    Result<Unit> Start(ProcessRequest request);
}
/// <summary>Resolves and opens the text editor the user chose, without ever involving a shell.</summary>
public interface IEditorLauncher
{
    /// <summary>Explicit setting, then git core.editor, then GIT_EDITOR/VISUAL/EDITOR, then the platform default.</summary>
    Task<Result<string?>> ResolveAsync(string root, string? overrideCommand, CancellationToken ct = default);
    /// <summary>Opens the paths in the resolved editor and returns the command that was used, or null when none resolved.</summary>
    Task<Result<string?>> OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct = default);
}
/// <summary>Reads repository history and file content without changing refs, the index, or the working tree.</summary>
public interface IGitRepositoryReader
{
    /// <summary>Opens a repository and captures its refs and working-tree change count.</summary>
    Task<Result<RepositorySnapshot>> OpenAsync(string path, CancellationToken ct = default);
    /// <summary>Returns every best common ancestor; callers reject zero or ambiguous results.</summary>
    Task<Result<IReadOnlyList<string>>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default);
    /// <summary>Counts commits unique to each side relative to the proposed parent/head pair.</summary>
    Task<Result<(int Ahead, int Behind)>> CountsAsync(string root, string parent, string head, CancellationToken ct = default);
    /// <summary>Reads machine-parsed file metadata for a fixed pair of commit IDs.</summary>
    Task<Result<IReadOnlyList<FileChange>>> FilesAsync(string root, string @base, string head, CancellationToken ct = default);
    /// <summary>Loads the patch for one previously listed file, subject to the display-size limit.</summary>
    Task<Result<FileDiff>> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default);
}
/// <summary>Persists user-authored stack definitions in the repository configuration.</summary>
public interface IStackStore
{
    /// <summary>Loads and validates definitions while capturing a revision for optimistic concurrency.</summary>
    Task<Result<StackDocument>> LoadAsync(string root, CancellationToken ct = default);
    /// <summary>Saves only when the on-disk revision still matches the revision last read.</summary>
    Task<Result<StackDocument>> SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct = default);
}
/// <summary>Reads source blobs by immutable commit and path for syntax-aware diff rendering.</summary>
public interface IBlobReader
{
    Task<Result<BlobContent?>> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default);
}
/// <summary>Rewrites stack refs only after an explicit, previewed request; separate from the reader so reads stay provably non-mutating.</summary>
/// <remarks>Restack writes keep backup refs. Remote effects and user commits require explicit recovery when their outcome is uncertain.</remarks>
public interface IGitBranchWriter
{
    /// <summary>Gathers the read-only facts a plan needs, including refs, worktree occupancy and in-progress operations.</summary>
    Task<Result<RestackPlan>> PlanAsync(RepositorySnapshot repository, StackDefinition stack, bool preserveMerges = false, CancellationToken ct = default);
    /// <summary>Rebases each planned layer onto its parent, or restores every touched ref and reports the conflict.</summary>
    Task<Result<RestackOutcome>> RestackAsync(RestackPlan plan, IProgress<string>? progress = null, CancellationToken ct = default);
    /// <summary>Pushes the restacked branches under a force-with-lease against a freshly fetched remote SHA.</summary>
    Task<Result<IReadOnlyList<string>>> PushAsync(RestackPlan plan, CancellationToken ct = default);
    /// <summary>Returns the most recent stack write for one repository, used to offer undo across restarts.</summary>
    Task<Result<JournalEntry?>> LastJournalAsync(string root, CancellationToken ct = default);
    /// <summary>Restores the branches of the last applied write; refused when the working tree is not clean.</summary>
    Task<Result<RestackOutcome>> UndoLastAsync(string root, CancellationToken ct = default);
    /// <summary>Subject line of a branch's newest commit, used as a pull-request title suggestion.</summary>
    Task<Result<string?>> LastCommitSubjectAsync(string root, string branch, CancellationToken ct = default);
    /// <summary>Creates and checks out a new local branch at a start point so work can continue on a new layer.</summary>
    Task<Result<string>> CreateLayerAsync(string root, string branchName, string startRef, CancellationToken ct = default);
    /// <summary>Pushes every layer of the stack, using a lease only where the remote is not an ancestor.</summary>
    Task<Result<IReadOnlyList<string>>> PushStackAsync(RestackPlan plan, CancellationToken ct = default);
    /// <summary>Reads what the working tree holds, including any unfinished operation and unmerged paths.</summary>
    Task<Result<WorkingTreeState>> StateAsync(string root, CancellationToken ct = default);
    /// <summary>Stages the named paths so a resolved conflict can be continued.</summary>
    Task<Result<IReadOnlyList<string>>> StageAsync(string root, IReadOnlyList<string> paths, CancellationToken ct = default);
    /// <summary>Commits the working tree, honouring the user's hooks and signing configuration.</summary>
    Task<Result<string>> CommitAsync(string root, string message, bool stageAll, CancellationToken ct = default);
    /// <summary>Stashes tracked changes and returns the stash commit, or null when there was nothing to stash.</summary>
    Task<Result<string?>> StashAsync(string root, string message, CancellationToken ct = default);
    /// <summary>Pops the recorded stash and clears it from the journal.</summary>
    Task<Result<WorkingTreeState>> RestoreStashAsync(string root, CancellationToken ct = default);
    Task<Result<IReadOnlyList<SavedStash>>> SavedStashesAsync(string root, CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<SavedStash>>.Ok([]));
    Task<Result<WorkingTreeState>> RestoreRecordedStashAsync(string root, string sha, CancellationToken ct = default) =>
        RestoreStashAsync(root, ct);
    /// <summary>Checks out an existing local branch; refused when the working tree has tracked changes.</summary>
    Task<Result<Unit>> SwitchAsync(string root, string branch, CancellationToken ct = default);
    /// <summary>Resumes an interrupted rebase after the user resolved and staged the conflicts.</summary>
    Task<Result<RestackOutcome>> ContinueAsync(string root, string? editorCommand, CancellationToken ct = default);
    /// <summary>Drops the commit that conflicted and resumes the rebase.</summary>
    Task<Result<RestackOutcome>> SkipAsync(string root, CancellationToken ct = default);
    /// <summary>Aborts the operation in progress and restores every branch the interrupted run moved.</summary>
    Task<Result<RestackOutcome>> AbortAsync(string root, CancellationToken ct = default);
    /// <summary>The newest journal entry that still needs the user: undoable, conflicted, or holding a stash.</summary>
    Task<Result<JournalEntry?>> ActiveJournalAsync(string root, CancellationToken ct = default);
    /// <summary>Closes the newest entry that still needs the user, without changing the repository.</summary>
    Task<Result<Unit>> DismissAsync(string root, CancellationToken ct = default);
}
/// <summary>Stores per-user application preferences outside the repository.</summary>
public interface ISettingsStore
{
    Task<Result<AppSettings>> LoadAsync(CancellationToken ct = default);
    Task<Result<Unit>> SaveAsync(AppSettings settings, CancellationToken ct = default);
}
