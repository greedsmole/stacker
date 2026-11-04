# Local repository execution

Stacker uses the opened repository/worktree for all workspace Git commands. PR review and History share its
object database. Additional clones, bare repositories and temporary worktrees are not created by these flows.
The explicit offline demo still creates its own example repository.

## Execution and transport

`IPullRequestObjects.EnsureAsync` takes `RepositoryIdentity`, GitHub context and selected PR snapshots, and returns
`Result<Unit>`. It cannot change the working directory by returning a different repository. The UI projects PR
refs over the original repository snapshot. History, replay, stack analysis and reanchoring also take an explicit
repository. A shared `RepositoryGit` serializes object/ref writes by common Git directory and leases underlying
reads and displayed views. Worktrees execute in their own root while Git resolves the shared object database.

Missing objects are fetched through configured origin, honoring native Git SSH/HTTPS, credentials, proxy and
certificate settings. The application does not inject a gh helper or SSL backend. `gh` remains responsible for
API authentication and writes. Fetch is batched, atomic, noninteractive, disables automatic maintenance/tags
and preserves FETCH_HEAD; downloaded SHA values must match the selected PR snapshot. Existing objects work
offline. Background metadata polling never downloads code.

Each Git process has an explicit working directory. Repository-redirecting Git environment variables are removed
for Git commands, including a configured custom Git binary. Read-only diff/analysis policies remain: no external
diff/textconv, no project hooks, and no user merge-driver execution during trial merge or normalization.

## Retention and cleanup

Application references are namespaced by local repository identity and scope:

- `refs/stacker/pr/<repository-id>/<context-key>/...` retains selected PR objects.
- `refs/stacker/history/<repository-id>/<scope-key>/<sha>` retains observed historical commits.
- Existing `refs/stacker/backup/...` belongs to branch recovery and is excluded from object cleanup.

Local/remote scope binding migrates owned history pins inside the same repository, preserving the objects.
Clear releases only expected application refs through a Git ref transaction. Active readers defer removal;
cleanup never deletes repository directories/objects, user branches, remote-tracking refs, or forces Git GC.
The native Git storage size/location replaces the obsolete disk-cache budget in Settings. Patch/syntax memory
budgets remain. Working files, staged/unstaged changes, HEAD, index, local Git config and FETCH_HEAD are preserved
by preparation, viewing, history import and analysis.

## Migration and backup

SQLite schema 3 backs up schema 1/2 before upgrading. Content availability and import completion are tracked
per local repository, so two clones of the same GitHub repository cannot mistake each other's objects for local
availability. Import completion is recorded after verifying the fetched objects; failed imports can be retried.
Legacy `objects` and `history-objects` folders are read-only input sources. All migration Git commands run from
the opened repository, with the old path supplied as a source. The original folders and review metadata remain.
Explicitly removed history is not silently restored by migration.

New ZIP backups use manifest format 2: profile metadata plus a Git bundle of the active repository's history
pins. With no active repository the export contains metadata only. The user's checkout, ordinary branches and
credentials are not included. Format-1 archives with bare-history folders remain supported. Restore checks hashes,
schema, integrity and foreign keys into a separate empty profile; repository connection imports its saved bundle
or legacy objects. Metadata-only restore requires the original repository/network for code that was not bundled.

## Evidence

`GitCacheTests` exercises a real local PR remote with dirty/staged/untracked files, native transport options,
batched downloads, SHA races, failure diagnostics and offline object reuse. `LocalRepositoryHistoryTests` covers
linked worktrees, per-clone migration, both archive formats, multiple history scopes, retained commits after real
fixture garbage collection, leases for underlying diff reads, environment redirects, and blocked custom merge
drivers. These fixtures never fetch from or publish to a live GitHub/GHES service. Architecture checks require
explicit repository parameters on object ports and reject private-clone creation in object/history adapters.
