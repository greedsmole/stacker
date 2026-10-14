# Contributing to Stacker

## The rules

Three rules keep the review findings closed. They are enforced by `tests/Stacker.Tests/ArchitectureTests.cs`, so a
violation fails the build rather than a review.

1. **No primitives at boundaries.** A ref that may be rewritten is a `LocalBranch`, a repository path is a
   `RepoRoot`, an editor is a `ShellCommand`, a pending publication is a `PendingOperation`. If a value can be
   constructed invalid, it will reach a boundary invalid.
2. **No raw exceptions across a port.** Ports return `Result<T>`; failures carry a code from
   `Stacker.Core/Framework/Result.cs` (`ProblemCodes`). Add a code there rather than a new literal.
3. **No effect without a write-ahead journal record.** Anything that changes git, disk or GitHub is an
   `IStackCommand` run by `CommandRunner`.

## Adding a command

```csharp
public sealed class SomethingCommand(IGitEffects git) : IUndoableCommand
{
    public string Kind => "stack.something";                    // journal discriminator + UI label
    public string Describe() => "Do the thing";

    // 1. Preconditions only: no effect may run here.
    public async Task<CommandPlan> PlanAsync(CancellationToken ct = default)
    {
        var state = await git.StateAsync(ct);
        return state.IsFailed
            ? CommandPlan.Blocked(state.Problem!.Message)
            : CommandPlan.Ready([JournalStep.Planned("branch:A", "move", "refs/heads/A")]);
    }

    // 2. The effects; record every finished step so an undo can reverse exactly what happened.
    public async Task<CommandOutcome> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var moved = await git.MoveAsync(...);
        if (moved.IsFailed) return CommandOutcome.Failed(moved.Problem!);
        await context.Journal.StepAsync(context.Entry, context.Entry.Steps[0].Finished(oldSha, newSha, StepState.Done), ct);
        return CommandOutcome.Done("The thing was done.");
    }

    // 3. Reversal from the journal entry alone - never from in-memory state.
    public async Task<CommandOutcome> UndoAsync(JournalEntry entry, CancellationToken ct = default)
    {
        foreach (var step in entry.Moved) await git.RestoreAsync(step.Target, step.OldSha!, ct);
        return CommandOutcome.Restored($"Restored {entry.Moved.Count} branch(es).");
    }
}
```

Run it through the composition root: `bus.RunAsync(command)`. The runner journals, executes, reverses on failure
and reports exactly once — do not add a second reporting path.

## Adding a value

Put it in `Stacker.Core/Framework/Values.cs`. Give it a private constructor and a `Parse` (or `Create`) that
returns `Result<T>`. Never add a public constructor that skips validation, and never accept a raw `string` where a
value exists.

## Storage

Reads and writes go through `IAtomicFile`, `IStateStore<T>`, `IFileLockFactory` and `IJournal`. Do not call
`File`/`Directory` from `Desktop`, and do not create lock or temporary files inside a user's repository.

## Tests

- **BDD spec** for user-visible behaviour: `Given <state>, When <command>, Then <effect and journal phase>`, against
  a real temporary repository from `GitFixture`.
- **Unit test** for pure logic (planners, parsers, values) with no processes.
- **Fitness test** for a new architectural rule (add it to `ArchitectureTests`).

```sh
dotnet build -c Release
dotnet test -c Release
```

## Migration status

The contract framework and the storage primitives are in place (phases A and B of the migration). The write path,
GitHub/review and the diff/search surfaces are still on their pre-contract code and are being moved to commands
feature by feature; `docs/adr/0001-contract-first-commands.md` records the plan and the finding list it closes.
