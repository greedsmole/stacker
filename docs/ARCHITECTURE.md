# Stacker architecture

Stacker is one desktop process with a functional core, adapters for Git/GitHub/storage, and an Avalonia shell.
The migration uses Design by Contract and feature folders within the existing four projects. There is no new
service, message broker or read-model database.

## Ownership

| Project / folders | Owns |
| --- | --- |
| `Stacker.Core/Framework` | validated values, `Result<T>`/`Problem`, command execution and journal contracts |
| `Stacker.Core/Features` | local Git commands, restack and PR planners, review rules, workspace persistence policy |
| `Stacker.Infrastructure/Git` | planning reads, restack, recovery, push and working-tree effects; process policy |
| `Stacker.Infrastructure/GitHub` | gh transport, object cache, command-backed publication adapters |
| `Stacker.Infrastructure/Storage` | atomic files, held locks, application state, YAML settings and journal adapters |
| `Stacker.Infrastructure/Syntax`, `Demo` | highlighting and isolated offline repositories |
| `Stacker.Desktop/Features` | feature controllers, views and presentation state for Browser, Workspace, Sync and Review |
| `Stacker.Desktop/Shell` | window and binding facade; delegates work to the feature controllers |
| `Stacker.Desktop/Composition.cs` | concrete adapters and the shared foreground command bus |

Namespaces and the existing XAML binding surface are retained where useful. Folder ownership does not require
another assembly. Infrastructure alone implements the Git and journal ports. Feature code consumes Core ports;
it does not instantiate storage or launch processes.

## Boundary contracts

User commands validate repository roots, writable local branches, commit messages and editor commands using
`RepoRoot`, `LocalBranch`, `CommitMessage` and `ShellCommand`. Review destinations use `ComposerKey` and
`PendingOperation`. GitHub context includes host, repository ID and account. Checked PR publication plans also
carry their local root and context identity; a plan cannot be reused after changing those owners.

Fallible ports return `Result<T>` with a catalogued `Problem`. Adapter interface implementations translate OS,
process and JSON failures. Concrete adapters retain some throwing compatibility methods for internal callers
and existing integration fixtures. Presentation controllers may unwrap a result with `OrThrow` inside their
reporting boundary. This preserves the code and message in `StackerException`; it does not make exceptions the
port contract. Low-level transport parameters still include strings; validated command inputs are the migration
boundary, not a claim that every string in the program is a value object.

## Foreground command execution

```text
user action → feature controller → adapter facade → shared CommandBus
  → read-only command plan
  → durable Running journal entry
  → Started effect → adapter → Done effect
  → Applied / NeedsUser / RolledBack / Dismissed journal phase
  → reported CommandOutcome
```

The bus permits one foreground mutation at a time and cancels stale shell reads when a command starts.
`LocalGitClient`, `CommandGitHubWriter` and `CommandStackStore` construct concrete Core commands. The runner
handles both returned failures and exceptions. It compensates an `IUndoableCommand` only when compensation is
implemented; otherwise the durable entry remains `NeedsUser`. Remote publication is never automatically retried
or described as rolled back. `CommandOutcome.Status` distinguishes applied, refused, failed, cancelled,
partially applied, rolled back and work requiring the user.

Draft autosaves, preferences, navigation state, syntax caches and downloaded Git objects use their storage/cache
ports directly. Journaling each keystroke or cache fill would add no recoverable business operation. Opening the
user's editor is likewise an explicit launcher operation rather than a reversible repository command.

## Git safety and restart recovery

The journal uses entry schema 3, reads schema 2, and preserves unfinished entries. Steps distinguish `Planned`,
`Started`, `Done`, `Failed` and `Unknown`. Critical malformed/unsupported journals block writes and remain on disk;
they are never silently replaced with an empty history. Older noncritical state may be quarantined with a notice.
The journal keeps up to 40 resolved entries, plus entries needed for unresolved work or saved stashes.

A restack stores the original branch, the complete approved plan and each layer's original SHA. Replay cutoffs
are separate from the current parent tip. A rewritten/amended parent requires a proven reflog fork point;
missing evidence blocks the plan. Continue/Skip records the resolved layer and executes the remaining original
cascade. A parent moved outside the operation stops that cascade for inspection.

Undo checks every affected branch and other worktrees before changing a ref. Only completed rebase effects
are undone; planned layers are excluded. Recovery also considers started/unknown effects, but refuses a move
unless the current SHA can be proven to belong to the operation. After detaching a checked-out target, all ref
restorations use one `git update-ref --stdin` transaction with expected-current SHA checks. Checkout then returns
to the original branch. Recovery preserves unrelated newer work; uncertainty requires inspection rather than a
guessed reset. A checkout failure after ref restoration is still a recovery failure and is reported accordingly.

Saved stashes are independent journal records keyed by SHA and owner entry, so another restack cannot overwrite
them. The sync window lists them for explicit selection. A successful restore clears only that record. A conflicting
`stash pop` leaves Git's retained stash available for manual inspection while the applied changes are resolved.

Push fetches and verifies the entire approved remote snapshot before publishing the first branch. Every push
uses an explicit lease, including fast-forward updates and expected absence. Local sources are pinned SHAs.
Per-branch journal steps preserve known partial success. Leases protect each branch; there is no atomic multi-branch
remote rollback if a later network operation fails.

## Storage and lifecycle

`Storage/StoragePrimitives.cs` implements unique temporary writes, flush-to-disk, atomic replacement, held file
locks and typed JSON stores. Locks live in application data, not `.stackpr.yml.lock`. The OS releases held handles
on process death. Single-instance acquisition does not suppress legacy data import.

YAML saves compare content revisions. Guarded replacement captures the actual previous file into a unique
`.conflict` backup. A racing external edit is preserved there and reported; this is detection and preservation,
not a filesystem-wide compare-and-swap guarantee against an arbitrary external editor. Configuration remains
v1 YAML. Formatting and comments may be rewritten on a successful save.

Application data lives under the platform ApplicationData `Stacker/v3` directory. The importer copies missing
v2 state and legacy settings once, keeps the originals and does not overwrite existing v3 files. An interrupted
import is retryable. Workspace schema 2 stores a small index plus one document per stack; schema 1 is imported
on save. Queued snapshots clone changed stack state only, and unchanged stack documents are not rewritten.
Comparison history is capped at 40 views per stack.

Review sessions own their subscriptions and buffers. The cache holds at most 12 sessions, flushes before eviction
and disposes on repository changes and shutdown. Comparison views are capped at 24 and disposed when evicted.
Patch and syntax caches have entry and estimated retained-memory budgets. Large single results are not cached;
these budgets are estimates, not a whole-process heap limit. Search has a shared five-second deadline, bounded
regex matches, cancellation and shared patch reuse. Changed-line matching runs off the UI thread.

Shutdown pauses foreground work with a deadline and attempts workspace/draft/settings persistence. A failed
save keeps the close decision with the user: retry, export unsent text, resume editing, or explicitly discard.
Only committing the close permanently stops the bus and disposes the session. UI async events use `AsyncHandler` to catch and report failures. Corrupt publication markers
keep sending blocked until the user explicitly resolves uncertainty; unreadable drafts do not permit a send.

## Fitness and evidence

`ArchitectureTests` enforce Core's OS boundary, Result-returning ports, feature isolation, the UI event boundary,
registered services, nonempty command reachability, catalogued problems and a 300-code-line product-source limit
(XML documentation lines are excluded). The three product projects generate assembly XML documentation; a public
type or member without a `///` comment is a build error, and internal/private members are documented as well.
Behavioral tests additionally run real commands through the production bus; a source scan alone does not prove
that the app uses an architecture.

[Migration register](MIGRATION.md) maps the original 47 findings and ten safety regressions to evidence.
[Verification record](../VERIFICATION.md) states the checks actually run and the remaining external validation.

## Versioned review history

`Core/Features/History` owns change identity, observed versions, dependency snapshots, textual version comparison,
review checkpoints, blocking questions, stack analysis and file evolution. The graph stores the exact version IDs
used for analysis; a later metadata poll cannot silently change a running review's inputs. Navigation projects
active remote paths from this graph, while closed/merged nodes remain available in historical snapshots.

`Infrastructure/History` implements `IHistoryStore` with Microsoft.Data.Sqlite and `IHistoryGit` against an explicit
`RepositoryIdentity`. SQLite schema 3 retains immutable versions, observations, graph revisions, checkpoints,
concerns, drafts and publication evidence. It adds repository-specific content availability and resumable import
markers. Schema-1/2 upgrades back up the database first; legacy JSON drafts and old object directories remain.

PR preparation, historical pins, replay comparisons, trial merges and reanchoring use the opened worktree as
working directory and its actual Git object database. `RepositoryGit` serializes object/ref writes by common Git
directory and owns shared read leases. `IPullRequestObjects.EnsureAsync` ensures objects exist and returns Unit;
it cannot return a substitute comparison directory. The synthetic PR ref list is projected over the original
repository snapshot, preserving its identity/root. Repository-switch cancellation and tickets guard publication
of asynchronous results; view cache keys include the GitHub context.

Fetch uses native origin/credential/proxy/TLS configuration, atomic application refs, no tags, no FETCH_HEAD
write and no automatic maintenance. Explicit SHA validation detects a moving PR snapshot. Object reads disable
lazy fetch, external diff and textconv. Analysis disables renormalization and overrides external merge drivers;
a conflicting result with configured external drivers is unavailable instead of executing user code. Commit,
restack and other explicit editing actions retain their normal local Git behavior.

`ReviewSessionService` owns persistence/validation policy and explicit publication phases independently of Avalonia.
An immutable operation payload is persisted before sending. The existing foreground command bus still owns the
actual GitHub effect and journal. View controllers manage presentation and clear only the acknowledged draft
buffers. Historical anchor proposals require exact unique context, explicit confirmation and publication-time
server validation; original anchors remain in the draft's history.


## Workspace foundation

`IWorkspaceSession` owns displayed/pending snapshots, selection and query tickets independently of Avalonia.
A ticket includes session epoch, selection revision and displayed-snapshot revision. Background metadata is
staged, including its graph, and **Update changes** applies it explicitly. Seven workspace controllers depend
on purpose-specific presentation interfaces; `WorkspaceRuntime` owns their service references and session,
while `MainViewModel` exposes bindings and delegates commands. Review controllers retain their existing
review-view facade; this change does not claim every controller is a pure domain service.

Repository and worktree identities are stored independently of root paths. Registration resolves the Git common
directory and physical working-tree root. Worktrees share repository history, while workspace keys use worktree
identity and import older path-based views on first load. Relocation explicitly preserves both identities.
Local/PR bindings require matching head repository/ref and are established only after a verified, nonbackground
refresh sees the same head SHA. Binding aliases expose checkpoints and versions to both views, and migrate application-owned pin references within the selected repository. An account mismatch refuses that binding rather than merging remote histories.

`QueryCoordinator` shares immutable patch reads and reference-counts subscribers. Canceling one subscription
leaves the others alive; canceling the last cancels the underlying read. Metadata concurrency is capped at four,
patch reads at two; TextMate and refresh each have a single gate. Retained patch/syntax budgets still apply.
Large diff rows are prepared off the UI thread, transferred with one collection reset and rendered with the
existing virtualized list. Autosave has a 300 ms coalescing interval, one in-flight write and one latest snapshot;
Flush bypasses the interval.

Publication uses one operation ID from prepared payload through the bus, journal and diagnostic record. Remote
journal evidence and publication payloads live in the same SQLite database; local Git journals retain their JSON
format. Final draft acknowledgement, publication phase and matching remote journal resolution commit together.
The immutable payload is checked on every update and only one unfinished publication is allowed per draft.
Prepared operations interrupted before dispatch are safe to reject; Sending becomes Uncertain on restart.
NotStarted/Rejected outcomes preserve text and permit a later manual attempt; Unknown never triggers a retry.
Legacy markers remain readable, including cases without enough evidence for automatic acknowledgement.

Repository leases protect both displayed comparisons and underlying Git reads. Clear waits for readers and
deletes only expected application refs through `update-ref --stdin`. It never deletes `.git`, object files, user
branches, remote-tracking branches or restack backup refs, and never forces garbage collection. Disk management
belongs to Git; the old disk-cache budget is a readable legacy settings field. Patch and syntax memory bounds
remain. The old directory-based `IObjectStore` is retained as a compatibility utility and is not wired to active
repository reads or cleanup.

Legacy imports execute from the selected worktree with the old cache/bundle supplied as a fetch source. Import
completion is recorded per repository/source only after objects are verified, so interruption is retryable and a
second clone gets its own import. Original directories remain untouched. Explicitly removed history stays removed.

Backup format 2 exports metadata plus a Git bundle of the active repository's history refs; no active repository
means a metadata-only backup. Restore validates schema, checksums, integrity and foreign keys into a separate
profile. Bundle import occurs on repository connection. Format-1 archives containing old bare-history folders
remain readable and use the same local import path. This is not a backup of the working tree or credentials.
Diagnostics retain their bounded allowlisted metadata format.

[Foundation delivery register](FOUNDATION.md) maps these boundaries to executable evidence.
