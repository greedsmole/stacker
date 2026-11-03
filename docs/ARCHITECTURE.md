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

Shutdown stops foreground work with a deadline, attempts workspace/draft/settings persistence, collects failures
and closes the window. UI async events use `AsyncHandler` to catch and report failures. Corrupt publication markers
keep sending blocked until the user explicitly resolves uncertainty; unreadable drafts do not permit a send.

## Fitness and evidence

`ArchitectureTests` enforce Core's OS boundary, Result-returning ports, feature isolation, the UI event boundary,
registered services, nonempty command reachability, catalogued problems and a 300-line product-source limit.
Behavioral tests additionally run real commands through the production bus; a source scan alone does not prove
that the app uses an architecture.

[Migration register](MIGRATION.md) maps the original 47 findings and ten safety regressions to evidence.
[Verification record](../VERIFICATION.md) states the checks actually run and the remaining external validation.

## Versioned review history

`Core/Features/History` owns change identity, observed versions, dependency snapshots, textual version comparison,
review checkpoints, blocking questions, stack analysis and file evolution. The graph stores the exact version IDs
used for analysis; a later metadata poll cannot silently change a running review's inputs. Navigation projects
active remote paths from this graph, while closed/merged nodes remain available in historical snapshots.

`Infrastructure/History` implements `IHistoryStore` with Microsoft.Data.Sqlite and `IHistoryGit` with a private bare
repository per scope. The indexed SQLite document tables use transactions for version identity and draft import,
and compare revisions for draft writes. Unknown database schema versions and damaged legacy drafts fail closed.
The existing JSON workspace/settings and v1 YAML definitions retain their formats. Legacy draft JSON is preserved.

Observed PR metadata does not imply downloaded code. Opening a layer pins the available commits in a separate
history cache; opening History captures its selected stack path. Base-change comparison uses `merge-tree` with an
explicit old merge-base to obtain an expected tree and compares it with the new head. Missing objects, conflicts
and unsupported Git capabilities remain explicit unavailable results. No repository code, merge driver configured
by the user's global configuration, hook, checkout or index operation is invoked by this analysis adapter.

`ReviewSessionService` owns persistence/validation policy and explicit publication phases independently of Avalonia.
An immutable operation payload is persisted before sending. The existing foreground command bus still owns the
actual GitHub effect and journal. View controllers manage presentation and clear only the acknowledged draft
buffers. Historical anchor proposals require exact unique context, explicit confirmation and publication-time
server validation; original anchors remain in the draft's history.
