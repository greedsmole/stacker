using Stacker.Core.Framework;
namespace Stacker.Core;

/// <summary>Defines the process boundary used by Git and GitHub CLI adapters.</summary>
/// <remarks>Keeping arguments separate from the executable prevents shell interpretation and makes cancellation testable.</remarks>
public interface IProcessRunner
{
    /// <summary>Runs one bounded process operation and returns both output streams and its exit code.</summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default);
}
/// <summary>Starts a program without waiting for it or capturing its output, for editors and other interactive tools.</summary>
public interface IProcessLauncher
{
    /// <summary>Launches the process and returns immediately; a missing program is reported as a StackerException.</summary>
    void Start(ProcessRequest request);
}
/// <summary>Resolves and opens the text editor the user chose, without ever involving a shell.</summary>
public interface IEditorLauncher
{
    /// <summary>Explicit setting, then git core.editor, then GIT_EDITOR/VISUAL/EDITOR, then the platform default.</summary>
    Task<string?> ResolveAsync(string root, string? overrideCommand, CancellationToken ct = default);
    /// <summary>Opens the paths in the resolved editor and returns the command that was used, or null when none resolved.</summary>
    Task<string?> OpenAsync(string root, IReadOnlyList<string> paths, string? overrideCommand, CancellationToken ct = default);
}
/// <summary>Reads repository history and file content without changing refs, the index, or the working tree.</summary>
public interface IGitRepositoryReader
{
    /// <summary>Opens a repository and captures its refs and working-tree change count.</summary>
    Task<RepositorySnapshot> OpenAsync(string path, CancellationToken ct = default);
    /// <summary>Returns every best common ancestor; callers reject zero or ambiguous results.</summary>
    Task<IReadOnlyList<string>> MergeBasesAsync(string root, string left, string right, CancellationToken ct = default);
    /// <summary>Counts commits unique to each side relative to the proposed parent/head pair.</summary>
    Task<(int Ahead, int Behind)> CountsAsync(string root, string parent, string head, CancellationToken ct = default);
    /// <summary>Reads machine-parsed file metadata for a fixed pair of commit IDs.</summary>
    Task<IReadOnlyList<FileChange>> FilesAsync(string root, string @base, string head, CancellationToken ct = default);
    /// <summary>Loads the patch for one previously listed file, subject to the display-size limit.</summary>
    Task<FileDiff> PatchAsync(string root, string @base, string head, FileChange file, CancellationToken ct = default);
}
/// <summary>Persists user-authored stack definitions in the repository configuration.</summary>
public interface IStackStore
{
    /// <summary>Loads and validates definitions while capturing a revision for optimistic concurrency.</summary>
    Task<StackDocument> LoadAsync(string root, CancellationToken ct = default);
    /// <summary>Saves only when the on-disk revision still matches the revision last read.</summary>
    Task<StackDocument> SaveAsync(string root, IReadOnlyList<StackDefinition> stacks, string? expectedRevision, CancellationToken ct = default);
}
/// <summary>Reads source blobs by immutable commit and path for syntax-aware diff rendering.</summary>
public interface IBlobReader
{
    Task<BlobContent?> ReadBlobAsync(string root, string commit, string path, CancellationToken ct = default);
}
/// <summary>Rewrites stack refs only after an explicit, previewed request; separate from the reader so reads stay provably non-mutating.</summary>
/// <remarks>Every write is preceded by a backup ref and reversible; a failure must leave the repository exactly as it was found.</remarks>
public interface IGitBranchWriter
{
    /// <summary>Gathers the read-only facts a plan needs, including refs, worktree occupancy and in-progress operations.</summary>
    Task<RestackPlan> PlanAsync(RepositorySnapshot repository, StackDefinition stack, bool preserveMerges = false, CancellationToken ct = default);
    /// <summary>Rebases each planned layer onto its parent, or restores every touched ref and reports the conflict.</summary>
    Task<RestackOutcome> RestackAsync(RestackPlan plan, IProgress<string>? progress = null, CancellationToken ct = default);
    /// <summary>Pushes the restacked branches under a force-with-lease against a freshly fetched remote SHA.</summary>
    Task<IReadOnlyList<string>> PushAsync(RestackPlan plan, CancellationToken ct = default);
    /// <summary>Returns the most recent stack write for one repository, used to offer undo across restarts.</summary>
    Task<JournalEntry?> LastJournalAsync(string root, CancellationToken ct = default);
    /// <summary>Restores the branches of the last applied write; refused when the working tree is not clean.</summary>
    Task<RestackOutcome> UndoLastAsync(string root, CancellationToken ct = default);
    /// <summary>Subject line of a branch's newest commit, used as a pull-request title suggestion.</summary>
    Task<string?> LastCommitSubjectAsync(string root, string branch, CancellationToken ct = default);
    /// <summary>Creates and checks out a new local branch at a start point so work can continue on a new layer.</summary>
    Task<string> CreateLayerAsync(string root, string branchName, string startRef, CancellationToken ct = default);
    /// <summary>Pushes every layer of the stack, using a lease only where the remote is not an ancestor.</summary>
    Task<IReadOnlyList<string>> PushStackAsync(RestackPlan plan, CancellationToken ct = default);
    /// <summary>Reads what the working tree holds, including any unfinished operation and unmerged paths.</summary>
    Task<WorkingTreeState> StateAsync(string root, CancellationToken ct = default);
    /// <summary>Stages the named paths so a resolved conflict can be continued.</summary>
    Task<IReadOnlyList<string>> StageAsync(string root, IReadOnlyList<string> paths, CancellationToken ct = default);
    /// <summary>Commits the working tree, honouring the user's hooks and signing configuration.</summary>
    Task<string> CommitAsync(string root, string message, bool stageAll, CancellationToken ct = default);
    /// <summary>Stashes tracked changes and returns the stash commit, or null when there was nothing to stash.</summary>
    Task<string?> StashAsync(string root, string message, CancellationToken ct = default);
    /// <summary>Pops the recorded stash and clears it from the journal.</summary>
    Task<WorkingTreeState> RestoreStashAsync(string root, CancellationToken ct = default);
    /// <summary>Checks out an existing local branch; refused when the working tree has tracked changes.</summary>
    Task SwitchAsync(string root, string branch, CancellationToken ct = default);
    /// <summary>Resumes an interrupted rebase after the user resolved and staged the conflicts.</summary>
    Task<RestackOutcome> ContinueAsync(string root, string? editorCommand, CancellationToken ct = default);
    /// <summary>Drops the commit that conflicted and resumes the rebase.</summary>
    Task<RestackOutcome> SkipAsync(string root, CancellationToken ct = default);
    /// <summary>Aborts the operation in progress and restores every branch the interrupted run moved.</summary>
    Task<RestackOutcome> AbortAsync(string root, CancellationToken ct = default);
    /// <summary>The newest journal entry that still needs the user: undoable, conflicted, or holding a stash.</summary>
    Task<JournalEntry?> ActiveJournalAsync(string root, CancellationToken ct = default);
    /// <summary>Closes the newest entry that still needs the user, without changing the repository.</summary>
    Task DismissAsync(string root, CancellationToken ct = default);
}
/// <summary>Stores per-user application preferences outside the repository.</summary>
public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
