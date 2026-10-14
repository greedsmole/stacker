# Stacker architecture

Stacker is a single-process desktop application. It reads Git history, rewrites stack branches when the user asks,
publishes reviews to GitHub, and stores its own drafts and journals under application data. This document is the
contract every change is expected to keep.

## Rings

| Ring | Project | May do | Must never do |
| --- | --- | --- | --- |
| Domain | `Stacker.Core` | plans, rules, values, ports, the command runner | touch a process, a file or the network |
| Adapters | `Stacker.Infrastructure` | run git/gh, read and write files, talk to GitHub | contain UI state |
| UI | `Stacker.Desktop` | present, collect input, run commands through the bus | run git, read files, or start processes |

The boundaries are enforced by `tests/Stacker.Tests/ArchitectureTests.cs`, which fails the build when a ring
reaches outside its column.

## Three rules

**R1 — No primitives at boundaries.** A ref that may be rewritten is a `LocalBranch`; a repository path is a
`RepoRoot`; an editor is a `ShellCommand`; a pending publication is a `PendingOperation`. Values that cannot be
constructed wrong cannot be passed wrong.

**R2 — No raw exceptions across a port.** Ports return `Result<T>` carrying a `Problem` with a code from
`ProblemCodes`. Adapters translate operating-system, process and JSON failures; the UI never sees an
`IOException`, a `JsonException` or a process exit code.

**R3 — No effect without a write-ahead journal record.** Every mutation is an `IStackCommand` executed by
`CommandRunner`, which opens a journal entry *before* the first effect, records each finished step, and closes the
entry with its outcome. A failure reverses what already happened; a crash leaves a `Running` entry the next launch
offers to resume, restore or dismiss.

## The four primitives

| Primitive | Where | Guarantee |
| --- | --- | --- |
| `AtomicFile` | `Infrastructure/Framework/Storage.cs` | unique temporary name, atomic move, verification of what landed, stale temporary sweep |
| `FileLockFactory` | same file | an exclusive *held handle* in application data; a crash cannot leave a marker that blocks later runs |
| `JsonStateStore<T>` | same file | one JSON document per identity; a damaged file is quarantined (`.bad`), never thrown at the caller |
| `JsonJournal` | `Infrastructure/Framework/JsonJournal.cs` | the write-ahead record behind undo, resume and recovery |

Nothing else in the codebase opens a file for writing, and nothing else starts a process.

## The lifecycle of an action

```
user presses a control
  └─ CommandBus.RunAsync(command)            single-flight, reports every outcome
       ├─ command.PlanAsync()                read-only; blockers refuse before anything opens
       ├─ journal.OpenAsync(Running)         write-ahead record exists before the first effect
       ├─ command.ExecuteAsync(context)      performs effects; journal.StepAsync after each one
       └─ journal.CloseAsync(Applied | NeedsUser | RolledBack)
            └─ on any throw: IUndoableCommand.UndoAsync() so the repository is never half-changed
```

Recovery is a first-class state, not an error path: `IJournal.ActiveAsync` returns the entry that still needs the
user, and the shell offers **Resume**, **Restore** or **Dismiss**.

## Adding a feature

1. **One slice folder** — `src/Stacker.Core/Features/<Feature>/` for the plan and the rules,
   `src/Stacker.Infrastructure/<Adapter>/` for the effects, `src/Stacker.Desktop/Features/<Feature>/` for the view.
2. **One command** — implement `IStackCommand` (and `IUndoableCommand` when its effects can be reversed). The plan
   returns the `JournalStep` list the runner will record.
3. **One spec** — a `Given/When/Then` test in `tests/Stacker.Tests` that drives the command against a real temporary
   repository (`GitFixture`) and asserts both the effect and the journal phase.

If a change needs a mechanism that is not one of the four primitives, it needs an ADR first.

## Fitness rules (enforced by tests)

- `Core` contains no `System.Diagnostics`, `File.`, `Directory.`, `HttpClient` or `Process.Start`.
- `Core` and `Infrastructure` contain no `async void`.
- Problem codes come from `ProblemCodes`; literal codes fail the build.
- Only `Infrastructure` implements `IGitBranchWriter`, `IGitRepositoryReader` and `IJournal`.
- Every `IStackCommand` implementation is reachable from the composition root.
- No source file exceeds 700 lines (tightening as the write path is split).

## Application data

`%APPDATA%/Stacker/v3` (macOS: `~/Library/Application Support/Stacker/v3`) holds workspace state, PR snapshots,
drafts, the journal and locks. Data written by the previous layout (`v2`) is imported once, without deleting the
original. Locks live here and never inside a user's repository.
