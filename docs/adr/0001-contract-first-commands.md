# ADR 0001 — Contract-first commands on a hexagonal core

**Status:** accepted · **Date:** 2026-10-13 · **Supersedes:** nothing

## Context

A review of every feature found 9 major, 27 minor and 11 nit findings. Every one of them was the same shape: a
boundary accepted a value it should have refused, or performed an effect it could not undo.

- `origin/main` reached a publish plan as a base branch.
- A temporary path was assumed unique; a lock file was assumed to disappear after a crash.
- A journal was written *after* the rewrite it described, so an interrupted command left no record.
- A plan was assumed to still match the server; a persisted marker was assumed parseable.

The codebase was already layered (Core ← Infrastructure ← Desktop) and its planners were already pure. What was
missing was an enforcing mechanism at the boundary and a single owner for effects.

## Decision

Adopt **Design by Contract** as the working methodology, expressed as:

1. **Validated value types** for user command inputs and publication ownership (R1).
2. **`Result<T>`/`Problem`** on ports instead of thrown exceptions (R2).
3. **`IStackCommand` + write-ahead `IJournal`** for foreground Git, configuration and GitHub mutations, run by one
   `CommandRunner` (R3).
4. **Fitness functions** in the test suite so the rules fail the build instead of relying on review comments.

Architecture stays **hexagonal**, hardened with the command/journal core and organised as **vertical slices**
inside each ring. Verification uses **BDD** specs (one per user-visible invariant) and **TDD** for the framework
types; the planners stay a **functional core** so the risky logic is testable without processes.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Microservices / event bus / CQRS read models | Solves distribution and multi-writer problems this single-process app does not have; would add ceremony to every change. |
| Full DDD with aggregates and repositories | The domain is thin and mostly data; the planners already are functional cores. Aggregate ceremony would not have prevented a single finding. |
| Onion/Clean with more projects | Re-derives the existing layering with extra mapping code and no new guarantee. |
| Pure functional core | Git processes, files and the UI need an imperative shell. |
| Data-oriented design | Data volumes are trivial; latency is I/O, not layout. |
| Patch each finding where it surfaced | Leaves the same class of defect available to the next feature. |

## Consequences

- Foreground mutations leave durable evidence. Proven local ref effects can be restored; unknown effects and remote writes may require user reconciliation.
- New mutations use a feature slice and a command. Recovery still requires explicit preconditions and provenance; a journal alone does not prove ownership.
- Boundary calls are slightly more verbose (`Result` handling), and pure planners keep throwing for programmer
  errors — the churn is limited to ports.
- The review's finding list becomes the acceptance checklist for the migration, phase by phase.

## Implementation update — 2026-10-16

A–G are implemented in the existing projects. The migration closes the original finding register and the ten
additional safety regressions; see [MIGRATION.md](../MIGRATION.md) for evidence per item.

The original absolute wording is narrowed deliberately: draft autosave, preferences and caches are not journaled
commands. Low-level adapter APIs retain some string parameters and concrete compatibility methods; Core ports
return Result and user commands use validated values. Only operations with proven reversible effects compensate.
Unknown crash windows remain blocked for inspection, and remote multi-step actions report partial success.

The [architecture document](../ARCHITECTURE.md) defines current ownership, schemas, concurrency and recovery policy.
The [verification record](../../VERIFICATION.md) distinguishes local automated checks, cross-built packages and
external platform/integration checks.
