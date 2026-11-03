# Contributing to Stacker

Keep changes inside the existing Core, Infrastructure, Desktop and Tests projects. Read
[the architecture](docs/ARCHITECTURE.md) before changing execution or recovery policy.

## Add a feature

1. Put pure rules, plans and command inputs in `Stacker.Core/Features/<Feature>`.
2. Implement effects behind a Core port in the corresponding Infrastructure adapter folder.
3. Keep presentation state and controls in `Stacker.Desktop/Features/<Feature>`; the shell delegates to controllers.
4. For a foreground repository, configuration or publication mutation, construct a concrete `IStackCommand`
   through the shared `CommandBus`. Use the existing command-backed adapter facades as examples.
5. Add a regression test for the user-visible invariant. Risky Git changes need a real temporary repository and
   assertions on refs, checkout, working tree and journal, not just a mocked method-call assertion.

## Commands and errors

Validate inputs using the existing values in `Core/Framework/Values.cs`. New fallible ports return `Result<T>`;
add stable codes to `ProblemCodes`. Translate external exceptions in the adapter boundary. Do not swallow failed
journal or draft writes, and do not turn a nonzero process exit into success because stdout happens to contain JSON.

`PlanAsync` is read-only and returns blockers plus intended effects. Execution records `Started` before an effect
and `Done` immediately after a confirmed effect. Preserve unknown outcomes. A failure result and a thrown exception
both reach the runner's recovery path. Implement `IUndoableCommand` only if its effects can actually be reversed
from persisted evidence. Do not use a loop of branch resets as transactional Undo: preflight every target, then use
the existing expected-SHA ref transaction. Never reset planned but unexecuted layers.

Remote writes may partially succeed. Report what is known, retain an uncertain operation marker and require
explicit reconciliation before retrying. Never promise automatic remote rollback or silently retry publication.
When acting from a preview, pin its repository/account and revalidate the live plan before the first write.

Preferences, workspace navigation, draft autosave and caches use dedicated ports directly. They are not a command
per keystroke. Persist draft text and a publication marker successfully before sending it anywhere.

## Storage and UI

Use `IApplicationState`, `IStateStore<T>`, `IAtomicFile`, `IFileLockFactory` and `IJournal`. Keep locks in application
data. YAML stays schema v1; journal entries are schema v3 with v2 reads; workspace schema v2 reads the old monolithic
schema. Preserve originals during migration. Never quarantine a critical operation journal and proceed as if there
were no unfinished work.

Feature controllers/views must not reference concrete Infrastructure adapters or perform file/process I/O. Route
async event handlers through `AsyncHandler`, and report failures on the owning view model. Release subscriptions,
background reads and retained buffers when a session/view is replaced. Keep caches bounded by both count and an
estimated retained size where large text is stored. Save immutable snapshots of changed workspace state.

## Verify

```sh
dotnet build -c Release
dotnet test -c Release
python3 scripts/package.py osx-arm64
python3 scripts/package.py win-x64
```

Architecture tests enforce the port/result boundaries, command reachability, service resolution and a maximum
of 300 lines per product source file. Add a behavioral test when a source rule could pass without the production
path using the mechanism.

Use `GitFixture` for isolated histories, worktrees and local bare remotes. GitHub tests use doubles and recorded
transport responses. Native Windows execution and live GitHub/GHES publication require their own validation;
cross-publishing a Windows archive on macOS does not provide either. Record actual evidence and limitations in
`VERIFICATION.md`, and maintain the finding register in `docs/MIGRATION.md`.


## Foundation invariants

Keep workspace/controller dependencies behind their typed presentation interface and `WorkspaceRuntime`.
Displayed data changes explicitly; return asynchronous reads only while their session/selection/snapshot ticket
remains current. Use the shared reader for patch deduplication and hold an object lease for every private Git
read, including work continuing after a view is disposed.

Use `IReviewDraftRepository` with an explicit revision. Observe a graph through `ApplyObservationBatchAsync`;
do not save a partial set of nodes/versions and then separately replace the graph. Preserve immutable versions
and operation payloads. Keep merge-base evidence/materialization and repeated observations separate from a
version's identity. Repeated unchanged metadata must not grow the observation log on every poll.

An operation ID is a correlation and recovery identity, not a remote idempotency guarantee. Carry it through
publication and journal effects. Confirmed/rejected acknowledgement and draft cleanup share a SQLite transaction.
Only classify an external failure as Rejected when the transport proves that the effect was refused; network
errors and server failures remain Unknown. Do not clear comments edited after their submitted snapshot.

Run foundation fault/workflow/load tests with:

```sh
dotnet test -c Release --filter 'FullyQualifiedName~Foundation'
```

Load profiles print timings; they assert correctness, bounded work and virtualization, rather than imposing
machine-dependent latency thresholds on CI. Record native response measurements separately from headless data.
