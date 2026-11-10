namespace Stacker.Core;

/// <summary>User-authored ordered description of a stack; branch names are full Git refs.</summary>
/// <param name="Id">Stable identifier used as the configuration key and as the workspace group key; it is never shown and must be
/// unique among the stacks of one repository, which <see cref="StackValidation"/> enforces.</param>
/// <param name="Name">Display name of the stack group.</param>
/// <param name="Base">Full ref of the boundary the lowest layer sits on, normally <c>refs/heads/main</c>; validated on save, resolved
/// when the stack is opened.</param>
/// <param name="Branches">Layer refs in bottom-up order: the first layer sits directly on <c>Base</c> and every later layer sits on the
/// branch before it. Order is the stack itself, so reordering is a definition change, not a display preference.</param>
public sealed record StackDefinition(string Id, string Name, string Base, IReadOnlyList<string> Branches);
/// <summary>A named ref resolved to its immutable commit ID in one repository snapshot.</summary>
/// <param name="Name">Full ref name, for example <c>refs/heads/main</c>.</param>
/// <param name="Sha">Commit ID the ref pointed at when the snapshot was taken.</param>
public sealed record BranchRef(string Name, string Sha)
{
    /// <summary>The ref with its <c>refs/heads/</c> or <c>refs/remotes/</c> prefix removed, for display.</summary>
    /// <remarks>Only those two branch prefixes are stripped, so any other ref keeps its full namespace. This is display text only:
    /// comparisons and lookups always use <c>Name</c>, which is why stripping cannot change which object a comparison reads.</remarks>
    public string DisplayName => Name.Replace("refs/heads/", "").Replace("refs/remotes/", "");
    /// <summary>Returns <see cref="DisplayName"/>, so a bound or interpolated ref reads as a branch name instead of a full ref.</summary>
    public override string ToString() => DisplayName;
}
/// <summary>Immutable view of the repository refs and working-tree summary used by a comparison.</summary>
/// <param name="Root">Absolute working-tree root Git reports; every path the app resolves for this repository starts here.</param>
/// <param name="Refs">Direct local and remote-tracking refs from the moment of the snapshot, or the synthetic PR refs when the snapshot
/// was projected onto pull-request SHAs for a review comparison.</param>
/// <param name="ChangedFiles">Number of working-tree status entries, untracked files included and a rename counted once. It summarizes
/// the tree, not the difference between two commits.</param>
/// <remarks>The record is a value: taking a new snapshot produces a new instance, and nothing in Core mutates an existing one. A
/// comparison therefore keeps describing the state it was requested for even if refs move while it runs.</remarks>
public sealed record RepositorySnapshot(string Root, IReadOnlyList<BranchRef> Refs, int ChangedFiles)
{
    /// <summary>True when the repository carries the offline demo marker, which disables publication and background polling.</summary>
    public bool IsDemo { get; init; }
    /// <summary>The registered repository and worktree identity, or null when the snapshot came from a path without one.</summary>
    /// <remarks>Identity is what keeps history and workspace state attached to a repository when its folder moves; a snapshot without it
    /// falls back to path keys, which is the shape older records used.</remarks>
    public RepositoryIdentity? Identity { get; init; }
    /// <summary>Stable history scope for this repository: the identity scope when known, otherwise a path-based fallback.</summary>
    /// <remarks>PR bindings and history queries key on this value, so it survives moving or renaming the folder when an identity exists.
    /// The fallback reproduces the key older versions wrote from the root path, which is why it is computed instead of dropped.</remarks>
    public string Scope => Identity?.Scope ?? "local:" + Root;
    /// <summary>Key for the saved workspace view of this working tree, so two worktrees of one repository keep separate views.</summary>
    /// <remarks>Uses worktree identity when available and the root path otherwise; the path form is also what older workspace documents used,
    /// so they can be imported on first load instead of being ignored.</remarks>
    public string WorkspaceKey => Identity is { } identity ? "worktree:" + identity.Worktree.Value : Root;
}
/// <summary>Resolved state for one ordered layer, including parent relationship and divergence warning.</summary>
/// <param name="Position">Zero-based index in definition order; the UI shows it as position plus one.</param>
/// <param name="Branch">Full ref of the layer.</param>
/// <param name="Parent">Full ref the layer should sit on: the stack base for the first layer, otherwise the previous branch.</param>
/// <param name="HeadSha">Commit the layer ref pointed at in the snapshot, or null when the ref was missing.</param>
/// <param name="ParentSha">Commit the parent ref pointed at in the snapshot, or null when the ref was missing.</param>
/// <param name="Ahead">Commits reachable from the layer but not from the parent; stays zero when a ref was missing.</param>
/// <param name="Behind">Parent commits missing from the layer; stays zero when a ref was missing, and a nonzero value otherwise means the
/// parent is not an ancestor.</param>
/// <param name="Warning">Display-ready caveat - a missing ref, or a comparison that can only use the merge base - or null when the layer is
/// comparable as it stands.</param>
/// <remarks>The counts are only meaningful together with <c>Warning</c>: when a ref is missing the counts were never measured and their zeros
/// must not be read as "in sync".</remarks>
public sealed record StackLayerSnapshot(int Position, string Branch, string Parent, string? HeadSha,
    string? ParentSha, int Ahead, int Behind, string? Warning);
/// <summary>Selects the layer comparisons one diff request asks for.</summary>
/// <remarks>The mode decides both which positions are used and which parent side each comparison takes, so the same request can describe
/// "this layer", "everything through this layer", "the whole stack" or several independent layers. <see cref="DiffService"/> normalizes the
/// request from the mode rather than trusting the caller to send matching positions.</remarks>
public enum DiffMode
{
    /// <summary>One layer against its immediate parent, with the stack base as the parent of the first layer.</summary>
    Layer,
    /// <summary>The whole path from the stack base through a layer, compared as one base-to-layer diff.</summary>
    Cumulative,
    /// <summary>The entire stack from the base to the topmost layer; positions other than the last are ignored.</summary>
    FullStack,
    /// <summary>Each selected layer against its own parent, shown as independent sections.</summary>
    MultiLayer
}
/// <summary>Requests one or more layer comparisons against a consistent repository and stack snapshot.</summary>
/// <param name="Repository">Snapshot that supplies the root and refs; both parents and layers are resolved from it, never from fresh reads.</param>
/// <param name="Stack">Definition whose <c>Branches</c> are the layers.</param>
/// <param name="Mode">Which comparisons are wanted; it overrides or normalizes <c>Positions</c>.</param>
/// <param name="Positions">Zero-based layer indexes: ignored by <c>FullStack</c>, reduced to the highest for <c>Layer</c> and <c>Cumulative</c>,
/// and every distinct value honored for <c>MultiLayer</c>.</param>
public sealed record DiffRequest(RepositorySnapshot Repository, StackDefinition Stack, DiffMode Mode, IReadOnlyList<int> Positions);
/// <summary>Diff metadata and file list for one layer; BaseSha and HeadSha record the exact compared objects.</summary>
/// <param name="Layer">Full ref of the compared layer, also used as the section title.</param>
/// <param name="ParentRef">Full ref of the parent side that was requested; it is not necessarily the merge base.</param>
/// <param name="BaseSha">Merge base the patch is taken from, so the file list shows exactly where parent and layer diverge.</param>
/// <param name="HeadSha">Commit the layer pointed at when the request was captured.</param>
/// <param name="Warning">Display-ready caveat when the merge base is not the parent tip, otherwise null.</param>
/// <param name="Files">File metadata only; each patch is requested later, per file.</param>
public sealed record DiffResult(string Layer, string ParentRef, string BaseSha, string HeadSha,
    string? Warning, IReadOnlyList<FileChange> Files)
{
    /// <summary>Total added lines across the listed files, with a binary file's null count counted as zero.</summary>
    /// <remarks>The sum describes textual changes only, so it matches the file rows the user can open rather than raw byte growth.</remarks>
    public int Additions => Files.Sum(f => f.Additions ?? 0);
    /// <summary>Total removed lines across the listed files, with a binary file's null count counted as zero.</summary>
    public int Deletions => Files.Sum(f => f.Deletions ?? 0);
}
/// <summary>Git's file-level change classification, including renames and mode changes.</summary>
/// <remarks>Derived from Git's name-status letter for one path pair. A path can exist on both sides while its content type changes, which is why
/// <see cref="TypeChanged"/> is distinct from add and delete.</remarks>
public enum FileChangeKind
{
    /// <summary>The path exists only in the new commit.</summary>
    Added,
    /// <summary>The path exists in both commits with different content.</summary>
    Modified,
    /// <summary>The path exists only in the old commit.</summary>
    Deleted,
    /// <summary>The path moved; <c>OldPath</c> and <c>NewPath</c> differ.</summary>
    Renamed,
    /// <summary>The content was copied to a new path while the old path remains.</summary>
    Copied,
    /// <summary>The path's object type changed, for example a regular file became a symlink or a submodule.</summary>
    TypeChanged
}
/// <summary>File metadata returned before an individual patch is requested.</summary>
/// <param name="OldPath">Path in the base commit; equal to <c>NewPath</c> for everything except a rename or copy.</param>
/// <param name="NewPath">Path in the head commit.</param>
/// <param name="Kind">Git's classification for the pair.</param>
/// <param name="Additions">Added line count from Git's numstat, or null when Git reports no counts - binary content. Null is not zero.</param>
/// <param name="Deletions">Removed line count, with the same null-for-binary meaning.</param>
/// <param name="OldMode">Octal file mode in the base commit, or <c>000000</c> when the path does not exist there.</param>
/// <param name="NewMode">Octal file mode in the head commit, or <c>000000</c> when the path does not exist there.</param>
/// <remarks>Kind and modes come from Git's raw name-status output while the counts come from numstat; both describe the same commit pair, so
/// they cannot disagree. Modes are Git's octal strings: <c>100644</c> regular, <c>100755</c> executable, <c>120000</c> symlink and
/// <c>160000</c> submodule pointer. A rename is reported with both paths, and a rename the reader could not detect appears instead as a delete
/// plus an add.</remarks>
public sealed record FileChange(string OldPath, string NewPath, FileChangeKind Kind,
    int? Additions, int? Deletions, string OldMode, string NewMode)
{
    /// <summary>True when Git reported no line counts, which is how binary content is recognized.</summary>
    /// <remarks>The flag is derived rather than stored: a hand-built change with zero counts is not binary, which matters for adapters that
    /// construct metadata without running Git.</remarks>
    public bool IsBinary => Additions is null;
    /// <summary>True when either side is the <c>160000</c> gitlink mode, so the change is a submodule pointer move rather than file text.</summary>
    public bool IsSubmodule => OldMode == "160000" || NewMode == "160000";
    /// <summary>True when both sides exist with different modes, so the change is executable/symlink metadata rather than content.</summary>
    /// <remarks><c>000000</c> is excluded on purpose: an add or delete also differs from the other side, but that is a new or removed path, not a
    /// mode change on an existing one.</remarks>
    public bool ModeChanged => OldMode != NewMode && OldMode != "000000" && NewMode != "000000";
    /// <summary>Old and new path joined with an arrow for a rename or copy, otherwise the single path.</summary>
    public string DisplayPath => OldPath == NewPath ? NewPath : $"{OldPath} → {NewPath}";
    /// <summary>Compact added/removed summary for a file row, or <c>binary</c> when there are no counts.</summary>
    public string Stats => IsBinary ? "binary" : $"+{Additions}  −{Deletions}";
    /// <summary>Short classification for the file header: submodule, a mode change, or the kind name.</summary>
    /// <remarks>The submodule label wins over a mode change because the mode pair alone does not tell a reader that the path is a gitlink.</remarks>
    public string Detail => IsSubmodule ? "Submodule" : ModeChanged ? $"Mode {OldMode} → {NewMode}" : Kind.ToString();
}
/// <summary>Semantic role of a rendered patch line; line numbers are null on the side where it does not exist.</summary>
public enum DiffLineKind
{
    /// <summary>An unchanged line present on both sides, with both line numbers.</summary>
    Context,
    /// <summary>A line only in the new side, with a new-side line number.</summary>
    Added,
    /// <summary>A line only in the old side, with an old-side line number.</summary>
    Removed,
    /// <summary>A hunk header row; it carries no line number and is never selectable for a comment.</summary>
    Header,
    /// <summary>A patch line that is neither a hunk header nor part of a hunk body, such as a file-mode line or the no-final-newline marker.
    /// It has no line numbers, and the renderer hides all of them except the newline marker.</summary>
    Notice
}
/// <summary>One patch row with its semantic role and the line numbers it occupies on each side.</summary>
/// <param name="Kind">Role of the row, which also decides whether it can carry a review comment.</param>
/// <param name="OldLineNumber">Line number in the old file, or null for an added line, a header or a notice.</param>
/// <param name="NewLineNumber">Line number in the new file, or null for a removed line, a header or a notice.</param>
/// <param name="Text">The raw patch line including its leading marker (<c>+</c>, <c>-</c> or space) for code rows; renderers drop the marker
/// for display and keep the original text for a patch copy.</param>
public sealed record DiffLine(DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);
/// <summary>A patch hunk and its parsed line coordinates, used to place inline review anchors.</summary>
/// <param name="Header">The raw <c>@@</c> line exactly as Git wrote it, including any trailing section heading.</param>
/// <param name="Lines">The hunk body only: context, added and removed rows in order, plus a notice for the no-final-newline marker. The header
/// row is not repeated here.</param>
/// <param name="OldStart">First old-side line number; for an empty old side the range is empty, and Git uses 0 for a new file or the preceding
/// line otherwise.</param>
/// <param name="OldCount">How many old-side lines the hunk covers, or 0 when it only adds. Git omits the count when it is 1, and the parser
/// normalizes that to 1.</param>
/// <param name="NewStart">First new-side line number, with the same empty-range convention as <c>OldStart</c>.</param>
/// <param name="NewCount">How many new-side lines the hunk covers, or 0 when it only removes; an omitted count normalizes to 1.</param>
/// <param name="Context">Text after the closing <c>@@</c>, trimmed: Git's enclosing function or section hint, empty when Git found none.</param>
/// <remarks>Coordinates are inclusive on both sides, so a hunk covering <c>OldCount</c> lines from <c>OldStart</c> ends at
/// <c>OldStart + OldCount - 1</c>; that is why the renderer clamps with <c>Math.Max(0, count - 1)</c> for an empty side. Mapping a new-side line
/// back to the old side across a hunk above it adds <c>OldStart + OldCount - NewStart - NewCount</c>, the shift that hunk introduced. The Git
/// adapter fills every field from Git's header; the defaults exist so a caller can construct a hunk from just a header and its lines.</remarks>
public sealed record DiffHunk(string Header, IReadOnlyList<DiffLine> Lines, int OldStart = 0, int OldCount = 0, int NewStart = 0, int NewCount = 0, string Context = "");
/// <summary>Parsed and raw forms of a single file patch.</summary>
/// <param name="File">The metadata this patch belongs to; its kind decides the binary, submodule and mode notes in the header.</param>
/// <param name="Patch">The raw patch text exactly as Git produced it, kept for the copy-patch action.</param>
/// <param name="Hunks">Hunks with parsed coordinates, used for rendering and for remapping a line to the other side.</param>
/// <param name="Lines">Flat stream of every patch row, including one header row per hunk and notices outside hunks, so a lookup by line number
/// can scan once.</param>
/// <remarks><c>Hunks</c> and <c>Lines</c> are two views of the same <c>Patch</c> and cannot disagree: rendering and history remapping walk the
/// hunks, while search, selection and review anchoring use the flat stream.</remarks>
public sealed record FileDiff(FileChange File, string Patch, IReadOnlyList<DiffHunk> Hunks, IReadOnlyList<DiffLine> Lines);
/// <summary>Definitions plus the content revision used to prevent overwriting external edits.</summary>
/// <param name="Stacks">Every definition in the document, in authored order.</param>
/// <param name="Revision">Content hash (uppercase SHA-256 hex) of the bytes that were read, or null when no configuration document exists; it is
/// saved back with the document so a later save can prove it replaces exactly what was read.</param>
public sealed record StackDocument(IReadOnlyList<StackDefinition> Stacks, string? Revision);
/// <summary>Complete execution policy for one external process, including resource limits and isolated environment edits.</summary>
/// <param name="Executable">Program to run: a bare name resolved on PATH, or an absolute path.</param>
/// <param name="Arguments">Already-split arguments, passed as separate entries so no shell parses them; an argument with spaces stays one
/// argument.</param>
/// <param name="WorkingDirectory">Working directory for the child, or null for the application's current directory; repository commands pass
/// the repository root explicitly.</param>
/// <param name="Timeout">Upper bound for the whole run; null leaves the budget to the runner, whose fallback is 30 seconds. At the boundary the
/// child process tree is killed and the call fails with a timeout problem.</param>
/// <param name="MaxOutputBytes">Combined cap across stdout and stderr, in bytes: the run fails with <see cref="OutputLimitException"/> once the
/// combined output passes it, so exactly the cap is still accepted. The default is 16 MiB (<c>16 * 1024 * 1024</c>); it bounds what one read
/// may retain and turns an oversized answer into a clear message instead of memory pressure.</param>
/// <param name="Environment">Variables to add or override for the child, applied on top of the inherited environment.</param>
/// <param name="StandardInput">Text written to the child's standard input, which is then closed so a tool waiting for end-of-input proceeds; null
/// closes it immediately.</param>
/// <param name="UnsetEnvironment">Variables to remove from the child's environment after <c>Environment</c> is applied, so a leaked token or
/// redirect can be dropped; null removes nothing.</param>
/// <remarks>One record carries everything a run needs, so a caller cannot forget a limit and an adapter can enforce every bound in one place.
/// An adapter applies the environment additions and removals to the child, writes and closes its input, and enforces the timeout and output cap
/// on the same run.</remarks>
public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory = null,
    TimeSpan? Timeout = null, int MaxOutputBytes = 16 * 1024 * 1024,
    IReadOnlyDictionary<string, string>? Environment = null, string? StandardInput = null, IReadOnlyList<string>? UnsetEnvironment = null);
/// <summary>The bounded result of one process run: exit code and both decoded output streams.</summary>
/// <param name="ExitCode">The exit code as reported; a nonzero value is data, not necessarily a failure, because the meaning belongs to the tool
/// that ran.</param>
/// <param name="StdOut">Standard output decoded as UTF-8; empty when the tool wrote nothing.</param>
/// <param name="StdErr">Standard error decoded as UTF-8; empty when the tool wrote nothing.</param>
/// <remarks>Both streams share the request's output cap and are drained concurrently, so a tool that fills one pipe cannot deadlock the read.
/// An empty stream is an empty string, never null.</remarks>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
/// <summary>Expected user-facing failure that can be shown without treating it as an application crash.</summary>
/// <remarks>Fallible ports return failed <see cref="Framework.Result{T}"/> values instead of throwing across the boundary; the UI and the Core
/// services above them unwrap those results, and that rethrow becomes this type, keeping the catalogue code in <see cref="Framework.Problem"/>. A
/// message-only instance carries no code and no certainty, so callers cannot switch on it - only the text is available.</remarks>
public class StackerException : Exception
{
    /// <summary>The catalogued problem behind the failure, or null when only a message was supplied.</summary>
    /// <remarks>Carries the code the UI switches on and, through <see cref="Framework.EffectCertainty"/>, whether the change already happened,
    /// which decides whether recovery may offer an undo or must ask the user to inspect.</remarks>
    public Framework.Problem? Problem { get; }
    /// <summary>Creates a failure with display text but no catalogue code.</summary>
    /// <param name="message">Text shown to the user.</param>
    public StackerException(string message) : base(message) { }
    /// <summary>Creates a failure that keeps the problem's code and certainty for callers that act on them.</summary>
    /// <param name="problem">The catalogued problem; its message becomes the exception message.</param>
    public StackerException(Framework.Problem problem) : base(problem.Message) => Problem = problem;
}
/// <summary>Marks a GitHub authentication failure so the shell can offer sign-in instead of a generic error.</summary>
/// <remarks>A failed result whose problem code is <see cref="Framework.ProblemCodes.GitHubAuth"/> is rethrown as this type when a result is
/// unwrapped, so a review or sync screen can check for it without matching message text. The token variables that can override gh's saved
/// account are described on <see cref="AppSettings.UseSavedGhCredentials"/>.</remarks>
public sealed class GitHubAuthenticationException : StackerException
{
    /// <summary>Creates an authentication failure with display text but no catalogue code.</summary>
    /// <param name="message">Text shown to the user.</param>
    public GitHubAuthenticationException(string message) : base(message) { }
    /// <summary>Creates an authentication failure that keeps the problem's code and certainty.</summary>
    /// <param name="problem">The catalogued problem, normally code <c>github.auth</c>.</param>
    public GitHubAuthenticationException(Framework.Problem problem) : base(problem) { }
}
/// <summary>Reports that a process or patch produced more output than the display can accept.</summary>
/// <remarks>Thrown by the process runner when combined output would pass <see cref="ProcessRequest.MaxOutputBytes"/>, and by the patch reader when a
/// single patch is over its line budget. The message is already user-facing and suggests a smaller selection. A reader may catch this type to
/// drop one optional result (blob text used for highlighting) and keep the comparison usable, but a file list or patch that hits the limit is
/// reported instead of truncated.</remarks>
public sealed class OutputLimitException() : StackerException("Output exceeds the display limit. Select a smaller file or comparison.");

/// <summary>Per-user preferences stored outside the repository, in the application-data folder.</summary>
/// <remarks>Loaded and saved as one document, with no per-property revision. Loading clamps the window and column sizes and fixes an unknown theme,
/// so a hand-edited or older document cannot put the shell into an unusable layout. A document whose <see cref="SchemaVersion"/> is newer than
/// <see cref="CurrentSchema"/> is preserved and writes stay blocked, rather than dropping fields this build does not know about.</remarks>
public sealed class AppSettings
{
    /// <summary>Bumped when a property is renamed or its meaning changes; unknown files are treated as defaults.</summary>
    /// <remarks>Schema 3 is current. Loading resets <see cref="SchemaVersion"/> to this value, so a stored document is read in place rather than migrated
    /// field by field: a property that is absent from the document keeps its default, and renaming or reinterpreting one is therefore what the bump records.
    /// A greater stored value blocks writes instead of being downgraded, because an older build would otherwise drop fields it cannot see.</remarks>
    public const int CurrentSchema = 3;
    /// <summary>The schema stamp read from the settings document and rewritten on every save.</summary>
    /// <remarks>Defaults to <see cref="CurrentSchema"/> so a fresh instance serializes at the current version. Only the comparison against
    /// <see cref="CurrentSchema"/> matters: greater blocks writes, smaller triggers no field-by-field migration.</remarks>
    public int SchemaVersion { get; set; } = CurrentSchema;
    /// <summary>Most recently opened local repository paths, ordered newest first by the UI.</summary>
    /// <remarks>The shell trims the list to ten when it records a repository, so the document cannot grow without bound. Entries are history
    /// suggestions, not guarantees: a path may no longer exist, and opening it then fails with an error instead of disappearing from the list.</remarks>
    public List<string> RecentRepositories { get; set; } = [];
    /// <summary>Enables GitHub metadata polling while the window is active; off means only an explicit refresh reads the remote.</summary>
    /// <remarks>On by default. The shell never polls for the offline demo or while no repository is open, and losing focus pauses the loop, so this
    /// setting alone cannot keep a background read running.</remarks>
    public bool BackgroundRefresh { get; set; } = true;
    /// <summary>Legacy profile field; repository objects are now managed by the selected Git repository.</summary>
    /// <remarks>Kept at its old default of 2048 MiB so an existing settings document round-trips unchanged. Nothing in the current read or write path
    /// consults it: disk usage belongs to Git, and the patch and syntax caches keep their own memory budgets, so changing it has no observable effect.
    /// Why 2048 MiB was chosen is not recorded.</remarks>
    public int CacheBudgetMiB { get; set; } = 2048;
    /// <summary>Persisted "wrap code" preference for diff panes; wrapping is applied to each section as it loads.</summary>
    /// <remarks>Changing it is immediate for open sections and is saved with the other preferences. It affects presentation only.</remarks>
    public bool WrapCode { get; set; }
    /// <summary>Root of the repository that was last opened, so the shell can reopen it at launch.</summary>
    /// <remarks>Null before the first successful open. A path given on the command line wins over this value; a remembered path that has disappeared is
    /// reported as an error rather than silently dropped.</remarks>
    public string? LastRepository { get; set; }
    /// <summary>Explicit path to the Git executable; null asks for Git to be found on PATH and in the usual installation folders.</summary>
    /// <remarks>Needed when a Finder or Start-menu launch cannot see a non-system installation. Saving hands the new path to the shared executable
    /// resolver, so the next command uses it without a restart.</remarks>
    public string? GitPath { get; set; }
    /// <summary>Explicit path to the GitHub CLI executable; null asks for gh to be found on PATH and in the usual installation folders.</summary>
    /// <remarks>Applies to the API calls Stacker makes; Git keeps using its own transport and credentials.</remarks>
    public string? GhPath { get; set; }
    /// <summary>Editor used for conflicts and commit messages; empty means git core.editor, then the environment, then a platform default.</summary>
    /// <remarks>Stored as typed and split by <see cref="CommandLine"/> into a program plus arguments, then handed to <see cref="IEditorLauncher"/>, which
    /// trims it and prefers it over Git's own configuration. It is never run through a shell.</remarks>
    public string? EditorCommand { get; set; }
    /// <summary>Uses gh's saved account instead of token variables from the environment for GitHub API calls.</summary>
    /// <remarks>Off by default so an explicit <c>GH_TOKEN</c> or <c>GITHUB_TOKEN</c> keeps its normal precedence. When on, <c>GH_TOKEN</c>,
    /// <c>GITHUB_TOKEN</c>, <c>GH_ENTERPRISE_TOKEN</c> and <c>GITHUB_ENTERPRISE_TOKEN</c> are removed from the environment of gh API processes only; shell
    /// variables, Git credentials and gh's global account are not changed.</remarks>
    public bool UseSavedGhCredentials { get; set; }
    /// <summary>Shell theme name, <c>Dark</c> or <c>Light</c>; any other stored value is reset to <c>Dark</c> on load.</summary>
    /// <remarks>The theme is part of the diff view cache key, so switching it rebuilds highlighted sections instead of reusing tokens from the other palette.</remarks>
    public string Theme { get; set; } = "Dark";
    /// <summary>Width of the navigation column in logical pixels; default 270.</summary>
    /// <remarks>Loading clamps the stored value to 200-500 and falls back to 270 when it is not a finite number, so a corrupt value cannot collapse or hide
    /// the column. The pane enforces the same limits while the user resizes it.</remarks>
    public double NavigationWidth { get; set; } = 270;
    /// <summary>Width of the per-comparison file column in logical pixels; default 290.</summary>
    /// <remarks>Loading clamps the stored value to 200-600 and falls back to 290 when it is not finite. The pane saves the width the user actually dragged to,
    /// which is why the stored value is re-clamped on the next load.</remarks>
    public double FilesWidth { get; set; } = 290;
    /// <summary>Initial window width in logical pixels; default 1400.</summary>
    /// <remarks>Loading clamps the stored value to 1000-4000 and falls back to 1400 when it is not finite, so a window cannot be restored below the layout's
    /// minimum or wider than a practical desktop.</remarks>
    public double WindowWidth { get; set; } = 1400;
    /// <summary>Initial window height in logical pixels; default 880.</summary>
    /// <remarks>Loading clamps the stored value to 640-2500 and falls back to 880 when it is not finite.</remarks>
    public double WindowHeight { get; set; } = 880;
}
