namespace Stacker.Core.Framework;

/// <summary>The closed set of problem codes; keep it closed so callers can switch on it.</summary>
/// <remarks>Codes are persisted in diagnostics and matched by the UI and tests, so a code should be added for a new
/// failure class rather than renamed or reused. The naming scheme is <c>area.reason</c>, lowercase with hyphens.
/// The catalogue is intentionally coarser than individual messages: the message explains one occurrence, the code
/// classifies it.</remarks>
public static class ProblemCodes
{
    /// <summary>No more specific code applies; the fallback for an unexpected failure.</summary>
    public const string Unknown = "app.unknown";
    /// <summary>The git executable could not be located.</summary>
    public const string GitMissing = "git.missing";
    /// <summary>Git ran but exited with a non-zero status.</summary>
    public const string GitFailed = "git.failed";
    /// <summary>Git did not finish within its process timeout.</summary>
    public const string GitTimeout = "git.timeout";
    /// <summary>The requested ref does not exist in the repository.</summary>
    public const string GitRefMissing = "git.ref-missing";
    /// <summary>The ref is not under <c>refs/heads/</c> and therefore cannot be rewritten.</summary>
    public const string GitNotLocalBranch = "git.not-local-branch";
    /// <summary>Uncommitted tracked changes block the operation.</summary>
    public const string GitDirtyTree = "git.dirty-tree";
    /// <summary>A rebase, merge or other working operation is already in progress.</summary>
    public const string GitOperationInProgress = "git.operation-in-progress";
    /// <summary>Conflicts must be resolved before the operation can continue.</summary>
    public const string GitConflict = "git.conflict";
    /// <summary>A state document could not be read.</summary>
    public const string StoreRead = "store.read";
    /// <summary>A state document could not be written.</summary>
    public const string StoreWrite = "store.write";
    /// <summary>A persisted document is damaged; the adapter may have quarantined it.</summary>
    public const string StoreCorrupt = "store.corrupt";
    /// <summary>Another instance holds the lock for this resource.</summary>
    public const string StoreBusy = "store.busy";
    /// <summary>The document changed after it was read, so saving would overwrite someone else's edit.</summary>
    public const string StoreStaleRevision = "store.stale-revision";
    /// <summary>A value or a persisted document violates its contract.</summary>
    public const string StateInvalid = "state.invalid";
    /// <summary>No editor command is configured, or it cannot be parsed.</summary>
    public const string EditorMissing = "editor.missing";
    /// <summary>GitHub authentication failed or the active credential is invalid.</summary>
    public const string GitHubAuth = "github.auth";
    /// <summary>GitHub refused the operation for lack of access.</summary>
    public const string GitHubAccess = "github.access";
    /// <summary>A GitHub request failed at the transport or HTTP level; certainty says whether it may still have
    /// landed.</summary>
    public const string GitHubRequest = "github.request";
    /// <summary>A GitHub response was missing or did not match the expected shape.</summary>
    public const string GitHubPayload = "github.payload";
    /// <summary>The approved publication plan no longer matches the current repository or pull request.</summary>
    public const string PublishStalePlan = "publish.stale-plan";
}
