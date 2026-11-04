# Workspace foundation delivery

Updated on 2026-11-04: Git execution now uses the opened local repository. See [local repository migration](LOCAL_REPOSITORY.md) for the current object-storage and backup model. The table below records the original foundation delivery.

Implemented on `codex/workspace-foundation`, following the checkpoint pushed as
`becb830ae37e36a27d2d2af44b21a537fde19970` before foundation work started.

| Area | Delivered boundary | Executable evidence |
| --- | --- | --- |
| Repository/change identity | Common-directory repository identity; individual worktree identity; stable worktree navigation keys; explicit relocation; local/PR aliases with account isolation; legacy path-scope migration | `FoundationStateTests`, `FoundationConcurrencyTests`, `HistoryTests` |
| Session and lifetime | Displayed/pending snapshots; epoch/selection/snapshot query tickets; typed controller presentations; subscriber cancellation; close failure retry/copy/resume/discard | `FoundationStateTests`, `FoundationWorkflowTests`, `RecoveryUiTests`, `ArchitectureTests` |
| Durable state | Schema-1 backup and atomic schema-2 migration; relational foreign keys; atomic graph observation with revisions; explicit draft revisions; bounded autosave queue; verified profile export/restore | `FoundationStateTests`, `FoundationFaultTests`, `FoundationRecoveryTests` |
| Publication protocol | Immutable payload and shared operation ID; one unfinished operation per draft; certainty propagation; atomic draft acknowledgement and journal resolution; immediate retry after known refusal; uncertainty retained after transport loss | `FoundationRecoveryTests`, `FoundationWorkflowTests`, `CommandExecutionTests`, existing review/reconciliation tests |
| Integrated History | Embedded panel; common layer/file cursor; Back/Forward with version/mode/path; structured documents rendered by existing diff controls; read-only historical comments; graph/progress/attention/evolution retained | `FoundationWorkflowTests`, `HistoryWorkflowTests`, `HistorySafetyTests` |
| Resource control and diagnostics | Shared patch reads; read budgets; view and Git-read object leases; deferred removal; configurable disposable budget and explicit historical availability; bounded metadata logs; off-thread diff row preparation and virtualized transfer | `FoundationConcurrencyTests`, `FoundationFaultTests`, `FoundationLoadTests`, existing process/cache/syntax tests |

Implementation ownership and the remaining compatibility surfaces are described in
[ARCHITECTURE.md](ARCHITECTURE.md). User workflows are in [README.md](../README.md).

## Load profile

Measured locally on Apple M1 Pro, macOS 26.6.2, .NET SDK 10.0.201 / runtime 10.0.5, Release build.
The dedicated profile was run independently from the full suite. Its p95 values are descriptive measurements,
not machine-independent CI assertions.

| Fixture | Samples | Result |
| --- | --- | --- |
| Discover 1000 PRs as 50 paths of 20 layers | 20 after warmup | p95 13.32 ms |
| Insert 10,000 immutable versions and their graph atomically | 1 | 445.38 ms |
| Read the latest graph with those 10,000 versions present | 20 | p95 0.31 ms |
| Prepare approximately 50,000 patch rows cold | 1 | 64.88 ms |
| Reload the cached patch and perform headless list layout | 10 | p95 114.10 ms |
| Realized diff list controls in a 1200 × 800 headless window | 1 | 27 |

The patch fixture has 50,000 source patch lines and 49,999 rendered rows after hiding the Git header.
This measures local discovery, SQLite access and headless diff loading/layout. Native input-to-paint latency,
network/GHES latency and Windows responsiveness require measurements on their actual target environment.

## Data and recovery limits

- A profile backup contains Stacker state and pinned historical objects. It excludes credentials, disposable
  downloads and the user's source checkout/YAML file. Restore writes into a separate empty profile.
- Registering a repository gives it a persistent identity. Use explicit relocation to associate a new folder
  with an already registered identity; unrelated clones do not merge automatically.
- A PR binding requires the active verified account and a matching local ref/head. The same local scope cannot
  silently transfer an existing remote review history to another account.
- Unknown publication results are reconciled or manually checked. Operation IDs and markers are evidence,
  not a claim that GitHub offers an exactly-once publication API.
- Active leases take precedence over automatic cache eviction. The disposable byte limit is temporarily soft
  while readers retain objects. Historical code is never evicted automatically.
- Code that was never downloaded, or was explicitly forgotten and is no longer reachable remotely, cannot be
  reconstructed from metadata. Trial merge and replay comparisons are textual and do not prove program semantics.
