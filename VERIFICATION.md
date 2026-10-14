# Verification — shutdown contract and recovery banner

Recorded on 2026-10-13. `dotnet build -c Release` is clean and `dotnet test -c Release` reports **201 passed, 0 failed** — the 197 scenarios of the previous phase plus 4 for the shutdown path and the recovery banner.

## Automatically verified

- **A window always closes.** With application data pointed at a root that cannot be created — so the workspace, draft and settings saves all fail — closing the window still closes it; the failures are collected and written to the error stream instead of cancelling the close. This was the finding that could previously leave the user with no way to quit except force-quitting, which also lost the very saves the handler was protecting.
- **An interrupted run is visible.** An entry left `Running` by a killed process (one branch already moved, nothing closed) appears in the main window's banner with its kind, phase, moved-branch count and message, and offers **Restore branches**; restoring puts the branch back at its pre-run commit and clears the banner.
- **Dismissing leaves the repository alone.** The entry is closed as `Dismissed` while every branch stays exactly as it is, and the result is reported rather than being a silent no-op.
- **Disposal is idempotent.** The container and the shutdown path both dispose the view model; doing it twice, then shutting down again, is harmless.
- **Persistence no longer depends on the window being ready**: the close path saves window geometry and workspace state even when the window is still loading.
- The discussion composer keeps its own buffer, so text typed after a reply is never written into the reply draft.
- Packaging no longer offers the deferred `linux-x64` target, and the third-party notices name SkiaSharp and HarfBuzzSharp, which ship inside the self-contained archive.

## Not yet verified (remaining minor findings)

- The deferred plumbing and performance findings: the shared patch cache for repeated searches (m6), Git runner timeout classes and executable probing (m11, n8, n9, n10), session and demo growth bounds (m13, m14), workspace-state items (m12, m16, n6) and the per-command reporting path (m23, m24) — the last one is partly covered by the banner reporting its results.
- The recovery banner's **Resume** action for a rebase that is still in progress remains the sync window's conflict panel; the banner itself offers Restore and Dismiss.
- Live GitHub publication is still unexercised; every GitHub server in the tests is an in-memory double.

## Earlier verification records

# Verification — diff, search and syntax hardening

Recorded on 2026-10-13. `dotnet build -c Release` is clean and `dotnet test -c Release` reports **197 passed, 0 failed** — the 194 scenarios of the previous phase plus 3 for the diff and search surfaces.

## Automatically verified

- **Git's submodule format is pinned**: with `diff.submodule=diff` configured in the repository, the diff command still passes `--submodule=short`, so the patch shape the parser and the anchor coordinates assume cannot be changed by user configuration (asserted through the diagnostics log).
- **A patch with no hunks explains itself**: a rename or empty file shows "Renamed — no textual changes in this comparison." instead of a blank pane.
- **Search results never outlive their sections**: clearing the comparison sections clears the hit list, so a click can no longer land on a disposed view.
- **Search has one deadline and honest counters**: the whole search stops after five seconds with "stopped after 5 s, narrow the search", and pattern timeouts are reported as such instead of being counted as unavailable files.
- **An evicted comparison releases its buffers**: disposing a view clears its patch, rows, tokens and threads instead of holding them until the cache entry is replaced.
- **A cancelled poller cannot suspend itself afterwards**: backoff and authentication suspension are skipped once the loop's token is cancelled, so a manual reset is not undone by an in-flight failure.
- **A failing draft save cannot escape an event handler**: the reply action reports the error through the view model instead of throwing out of an `async void`.
- **Range comments are built from commentable rows only** and their anchor is tied to one file, so a range that crosses a hunk header or a "No newline" notice no longer fails with a misleading message.
- **Syntax rendering is defensive**: the initial grammar state is established by a real tokenize call, the palette is resolved once per file, only non-empty results are cached, and an unparsable theme colour falls back instead of throwing during layout.

## Not yet verified (remaining work)

- **Phase F** — the UX pass: the recovery banner for a `Running` journal entry, one reporting path for command results, per-tab composer ownership, and the close path that must never be blocked by a failing draft flush (M5).
- Deferred minor findings: the shared patch cache for repeated searches (m6, performance only), the Git runner timeout classes and executable probing (m11, n8, n9, n10), the session/demo growth bounds (m13, m14) and the remaining workspace-state items (m12, m16, n6).

## Earlier verification records

# Verification — publishing and review on validated values

Recorded on 2026-10-13. `dotnet build -c Release` is clean and `dotnet test -c Release` reports **194 passed, 0 failed** — the 189 scenarios of the previous phase plus 5 for publishing correctness, the publication marker and the diagnostics log.

## Automatically verified

- **A remote-tracking stack base is normalized**: a stack based on `refs/remotes/origin/main` compares against the pull request's `main` and would send `main`, never `origin/main` (the plan previously proposed a base that does not exist and the request was rejected mid-publish).
- **Nothing is written when the open pull requests change after the check**: with a pull request opened for the last layer between the check and the publish, the run is refused with "changed since the check", and **no create or retarget call is made**.
- **A non-zero `gh` exit no longer hides its own diagnosis**: `gh auth status` exits 1 exactly when a host is unauthenticated, and the JSON payload is now read, so the "GITHUB_TOKEN is invalid … use saved gh credentials" hint is produced instead of a generic failure.
- **A persisted publication marker round-trips and a damaged one is refused**: all five kinds encode and decode, the resolve marker keeps its thread and state, and `resolve|thread|maybe`, `garbage`, an unknown kind and an empty string are all rejected rather than throwing during a reload.
- **A git binary that is not called `git` is still logged**: the diagnostics log matches the resolved executable path, so a configured or renamed Git is recorded (asserted with a symlinked binary; skipped on Windows where symlink creation needs privileges).

## Not yet verified (remaining migration phases)

- **Phase E** — the diff, search and syntax findings (range selection over rendered rows, unreachable empty state, `diff.submodule`, blob reads bypassing the runner, the search deadline and cache, and the syntax nits) are untouched.
- **Phase F** — the UX pass: the recovery banner for a `Running` journal entry, one reporting path for command results, typing every composer per tab (only the publication marker is typed so far), and the close path that must never be blocked by a failing draft flush.

## Earlier verification records

# Verification — write path on the write-ahead journal

Recorded on 2026-10-13. `dotnet build -c Release` is clean and `dotnet test -c Release` reports **189 passed, 0 failed** — the 184 scenarios of the previous phase plus 4 recovery specs for the write path and one that resolves the whole object graph (a missing registration can no longer ship as an app that will not start).

## Automatically verified

- **A failure between an effect and its record is recoverable.** With a journal that fails on the second step of a three-layer cascade, the restack returns `RolledBack`, **every** branch — including the one whose step was never recorded — is back at its pre-restack commit, the working tree is clean, the original branch is checked out, and the entry is closed `RolledBack`. This is the defect that previously left a rewritten stack with no record at all.
- **A crash is recoverable.** An entry left `Running` with one branch already moved is found by `ActiveJournalAsync` on the next run and `AbortAsync` restores that branch and returns to the branch the user started on (which is itself recorded in the entry).
- **A resolved conflict stays undoable.** After a conflict on a layer, resolving it and continuing rewrites and **records** that layer, so `UndoLastAsync` puts it back; the layers above correctly wait for the next restack. Previously the journal recorded nothing for the resolved layer, so undo silently restored nothing.
- **A stash never hides undo.** Taking a stash after an applied restack leaves the same entry undoable and carries the stash on it; after an undo the stash is still recorded, so the changes can still be restored. Previously the stash entry replaced the applied one and the undo affordance disappeared.
- The previous phase's 184 scenarios (contracts, storage primitives, commands, restack, push, publish, layer creation, local Git, headless UI, fitness rules) still pass unchanged.

## Not yet verified (remaining migration phases)

- **Phase D** — GitHub publishing and the review flow are still on their pre-contract code: the publish plan is not re-snapshotted immediately before writing, the parent ref is not normalized to a local branch, and the composer/pending-operation work is not typed.
- **Phase E** — the diff, search and syntax cleanups are untouched.
- **Phase F** — the UX pass: the main window still has no recovery banner for a `Running` entry (the journal is written and readable, but only the sync window surfaces it), and command results are still reported through the view model rather than one reporting path.
- Phases D–F of the migration plan cover the remaining review findings.

## Earlier verification records

# Verification — contract framework and storage primitives

Recorded on 2026-10-13. `dotnet build -c Release` is clean and `dotnet test -c Release` reports **182 passed, 0 failed** — the 164 scenarios of the previous phase plus 18 for the new contracts, the storage primitives and the architecture fitness rules.

## Automatically verified

- **Contracts**: `Result<T>`/`Problem` with a closed code catalogue; `LocalBranch`/`RemoteTrackingRef`/`StackBranchRef` refuse a remote-tracking layer with an actionable message (the publish-plan defect from the review); `CommitSha`, `CommitMessage` and `ShellCommand` validate at construction; an editor command with spaces survives both the direct launch and the `GIT_EDITOR` shell string.
- **Command runner**: a blocked plan never opens a journal entry; a command that fails after its first effect is reversed through `IUndoableCommand` and closed as `RolledBack`; a command that stops for the user keeps a `NeedsUser` entry.
- **Storage primitives**: a write leaves no temporary file and verifies what landed; a lock file left behind by a killed process no longer blocks the next run (the review's permanent-save-block defect) while a *held* lock still refuses a second writer; a damaged JSON document is quarantined to `.bad`, defaults are used and the damaged file is preserved; the stack file saves without creating any lock or temporary file inside the user's repository.
- **Journal**: open → step → close is persisted, an `Applied` entry stays undoable, a recorded stash survives, and `ActiveAsync` reports what still needs the user.
- **Settings**: a damaged `settings.json` is quarantined with a message, an unknown theme falls back to Dark, and the schema version is stamped.
- **Application data**: a second instance cannot acquire the single-instance lock; data from the `v2` layout is imported into `v3` once, leaving the original untouched.
- **Fitness rules** (fail the build): `Core` contains no `System.Diagnostics`/`File.`/`Directory.`/`HttpClient`; `Core` and `Infrastructure` contain no `async void`; problem codes come from the catalogue; only `Infrastructure` implements the git and journal ports; every command is reachable from the composition root; no source file exceeds 700 lines.

## Not yet verified (remaining migration phases)

- The **write path** is still on its pre-contract implementation: the interrupted-rewrite, partial-undo and stash-records findings (M1–M3) are **not** closed yet, nor are the publish/review findings (M8, M9) or the unclosable-window finding (M5). Phases C–F of the plan move them onto the journal and the command bus.
- The **diff, search and syntax** cleanups (M? minor findings m1–m8 and the syntax nits) are untouched.
- The `v2` → `v3` import is covered by a synthetic file; a real upgrade from an existing installation has not been exercised.

## Earlier verification records

# Verification — local Git work and conflict resolution

Recorded on 2026-10-10. `dotnet build -c Release` completes with no warnings or errors and `dotnet test -c Release` reports **164 passed, 0 failed** on macOS ARM64 — the 148 scenarios of the previous phase plus 16 for the working-tree commands, the editor hand-off and conflict resolution.

## Automatically verified

- **A conflict stops the restack and keeps the work.** Against real temporary repositories: the outcome is `Conflicted`, the rebase is left in progress, the conflicting files are listed, the conflicted layer has not moved, and the journal is `conflicted` so the operation can be aborted after a restart. Resolving the file in the working tree, staging it and continuing completes the layer, returns to the branch the user started from and leaves the tree clean; skipping drops the commit; aborting restores every branch the interrupted run moved — including the ones already rewritten — and leaves no `rebase-merge` or `rebase-apply` directory.
- **A second writer instance** (the app relaunching) reads the same state from the git directory and the journal, and aborts successfully — nothing about an interrupted restack lives only in memory.
- **Editor resolution**: the explicit setting wins and makes no git call, then `git config core.editor`, then `GIT_EDITOR`/`VISUAL`/`EDITOR`, then the platform default. Commands are split with quote awareness, and each file path is passed as its own argument — a path with spaces or Unicode is never re-split, and no shell is involved.
- **Working tree**: porcelain v1 is classified into staged, unstaged, untracked, renamed (the original path is consumed) and conflicted entries. **Untracked files no longer block a restack**; a tracked edit does, and the stash takes exactly that change while leaving untracked files alone. A bare stash is journaled, survives a restart, and is restored by index into the stash list rather than by assuming it is the newest.
- **Commits** refuse an empty message and a commit with nothing staged, and the repository's own `pre-commit` hook runs for a user commit (asserted on macOS) — unlike the internal rewrites, which stay deterministic.
- **Switching branches** moves only when the tracked tree is clean and refuses a branch that does not exist.
- **Pushing the stack** publishes every layer, including ones that needed no rebase; it pushes plainly when the recorded remote commit is an ancestor and forces only when it is not, and the existing lease checks still refuse a remote that moved after the preview.
- **Preserve merge commits**: a layer containing a merge commit is refused until the option is set, and the merge survives the rewrite when it is.
- **Headless UI, real XAML**: the main-window banner appears on a conflicted restack, names the rebase and the conflicted files, and clears after abort; the conflict panel lists the files, opens them through the editor abstraction, stages them and continues the rebase from real button presses; the commit window commits the working tree; stash-and-restack clears a dirty tree and *Restore stashed changes* reports back.

## Not verified

- **No real editor was opened.** Resolution and hand-off are asserted through a recording double plus a no-op program that git runs for the commit message. The interaction with an actual GUI editor — and whether a given editor needs a wait flag — is unverified, as is the macOS default (`open -t`).
- No push to a real GitHub/GHES remote and no pull request created or retargeted; every GitHub server in the tests is an in-memory double.
- No native macOS or Windows walkthrough of the new banner, conflict panel or commit window. User hooks were exercised on macOS only; the Windows path compiles but was not run here.
- A conflicted restack finishes by resolving in the editor and continuing, then pressing Restack again for the remaining layers — the cascade is deliberately not resumed automatically.

## Earlier verification records

# Verification — stack restack and publish

Recorded on 2026-10-10. `dotnet build -c Release` completes with no warnings or errors and `dotnet test -c Release` reports **148 passed, 0 failed** on macOS ARM64 — the 109 existing scenarios plus 39 covering the write path, pull-request publishing and layer creation.

## Automatically verified

- **Planning** is a pure function of a captured snapshot: an up-to-date stack has nothing to execute; a moved base rebases every layer above it, including layers that never diverged themselves; only the top layer moves when only it diverged; a layer with no commits of its own follows its parent; a missing ref blocks the plan and the layers above it; a remote-tracking layer is refused rather than silently detaching HEAD.
- **Execution** against real temporary Git repositories: all three layers of a moved stack are rewritten, each layer still contributes exactly its own file, the working tree ends clean on the branch the user started from, and the old commits remain reachable through the backups.
- **Rollback**: a conflicting layer aborts the rebase, restores every branch to its previous commit, leaves no `rebase-merge` or `rebase-apply` directory, reports the layer and the conflicting file, and records a rollback that is never offered as an undo.
- **Undo** restores every branch including the one currently checked out, refuses a dirty working tree, and keeps the backups.
- **Push** publishes the rewritten branches against a local bare remote under `--force-with-lease`, refuses when the remote branch moved after the preview was built (leaving the remote and the remaining layers untouched), and pushes a never-published branch without a lease.
- **Preview window** (headless, real XAML): lists every layer, stays hidden when there is nothing to do, disables the action while a blocker exists, and the view model reloads the workspace after a restack and after an undo.
- **Demo repository**: the generated demo is restackable; its own workspace files are ignored by Git rather than making the tree dirty.
- **Pull-request planning** is a pure function of a server snapshot: a stack with no pull requests needs one per layer, each into its own parent; an existing pull request is retargeted only when its base is wrong; a fork's pull request or one for another branch is never borrowed; two open pull requests for one layer are refused; a complete stack has nothing to publish.
- **Publication payloads** pin the host, choose POST to `repos/{owner}/{repo}/pulls` and PATCH to `.../pulls/{number}`, and send the title, head, base and draft through stdin so no review or pull-request text is ever interpreted as an argument.
- **Starting a layer** creates exactly one branch at the chosen start point, checks it out, leaves the working tree clean, and refuses a duplicate name, an invalid name, an empty name, a dirty tree or a missing start point without writing anything. Adding a layer from the view model appends it to `.stackpr.yml`, and the window does the same from a real button press.
- **An absent stack definition is recreated** rather than refused: checking out a branch that does not carry `.stackpr.yml` no longer blocks saving, because an absent file cannot hold someone else's edit.
- **The publish flow** through the real window: pressing **Check pull requests** renders the per-layer plan and writes nothing, pressing **Publish** creates only the missing pull requests and retargets only the wrong bases (a stateful in-memory server double reflects the writes, so a second check reports nothing pending), and the demo repository disables remote actions.

## Not verified

- Nothing was pushed to a real GitHub/GHES remote and no pull request was created or retargeted by Stacker. Every Git remote used in tests is a local bare repository, and the GitHub server in every test is an in-memory double.
- The new window has not been walked through natively on macOS or Windows, and the packaged `.app` was not rebuilt for this change.
- A conflicted restack always rolls back. There is no "resolve in an editor and continue" flow, and merge commits inside a layer are refused rather than flattened.

## Earlier verification records

# Verification — Stacker v0.4.1

Recorded on 2026-10-01. Local test suite: **109 passed, 0 failed** on macOS ARM64. Coverage includes native folder-picker ownership in headless Avalonia, multiline range selection and its full-height comment button, allowed unchanged context anchors, suggestion generation/parsing/rendering, and rejection of suggestions on deleted-side anchors. No live comments or reviews were posted.

Release packages target macOS ARM64 and Windows x64. GitHub Actions tests and packages both platforms for the release source commit. Interactive Windows/GHES testing and live publication remain unverified. The macOS app is not Developer ID signed or notarized.

## Earlier verification records

# Verification — Stacker v0.4

Local implementation on 2026-09-30: standalone PRs open directly in reviewable Layer mode; selected diff rows create a range anchor; code wrapping, file/change search and a live Git log are available. The Git cache batches missing refs and requests only the PRs required by the chosen comparison. No clone or pull is issued by the application.

Local Release test suite: **86 passed, 0 failed** on macOS ARM64. Regression tests cover standalone PR navigation, selective/batched cache fetch, multi-line editor controls, file and regex changed-line search, wrapping, and running/completed Git log entries. The previous v0.3 release record follows below. Live GHES fetch duration, interactive Windows behavior and real comment publication require verification on the Windows GHES workstation.

## Previous release verification

### Stacker v0.3

Recorded locally on 2026-09-29. Current target packages: macOS ARM64 and Windows x64.

- Release test suite: **80 tests passed** on macOS ARM64. New coverage includes real control bindings for inline review, independent composers, restored navigation across restart, staged remote changes, preserving text during background reads, older draft recovery, uncertain review reconciliation, left-side ranges, and the delayed/pause/backoff/authentication refresh coordinator.
- Native macOS walkthrough of the initial v0.3 package: generated the offline demo, selected Authorization layer 2 (+2/−1), opened its PR snapshot through Review, and inspected the embedded review panel, file discussion, code thread and clean highlighted diff. Publication was disabled in demo mode.
- The Mac locked during the final native recheck. Later changes are covered by headless tests; the final native inline-editor walkthrough, light theme and target-size/scaling matrix remain unverified.
- macOS ARM64 and Windows x64 packages are built locally and checked for archive integrity and executable architecture. Windows is cross-published on this Mac; interactive Windows validation was not performed.
- CI builds/tests/packages on both target operating systems. Release publication requires a successful run for its source commit; the release notes link to that run.
- Live posting to GitHub/GHES has not been exercised. Tests use fake responses and never post comments or reviews. The existing Windows Schannel cache behavior is retained.

### Stacker v0.2

Scope: macOS ARM64 and Windows x64 packaging. Recorded locally on 2026-09-24 with .NET SDK 10.0.201 and Git 2.42.0.

## Automated checks

- `dotnet test -c Release --nologo`: **54 passed, 0 failed, 0 skipped** on the final source, including restored nonadjacent layer selections.
- Release compilation completed without reported warnings/errors.
- `python3 scripts/package.py osx-arm64`: self-contained application and `Stacker-0.2.0-osx-arm64.tar.gz` package.
- CI runs tests and packaging on macOS ARM64 and Windows x64. Release publishing checks the run for its target commit.

Coverage includes real temporary Git histories and worktrees; all four comparisons; divergence, missing refs and multiple/no merge bases; filename/patch edge cases; configuration revisions; process cancellation, timeout, stdin and output bounds; multiple stacks and repository isolation; no-change/failed refresh; activation without reload; view-state/selection restoration; deterministic demo manifest; PR graph boundaries/forks/cycles/ambiguity; gh JSON authentication/pagination; isolated Git cache behavior; review draft persistence, stale anchors, access denial and uncertain publication reconciliation; real TextMate tokenization and fallback.

Avalonia headless tests load the real XAML. GitHub writes use test doubles only and do not post to external repositories.

## Native packaged application

The v0.2 self-contained application was launched and inspected through native accessibility and a screenshot. Verified:

- Open demo creates a separate repository and displays multiple numbered local stacks.
- Offline discovery displays four PR paths, shared-foundation labels and two service-boundary PRs under Other pull requests.
- Authorization Entire stack: one file, +7/−0.
- Authorization layer 2: one file, +2/−1.
- Through layer 2: one file, +6/−0.
- Nonadjacent layers 1 and 3: separate sections, +5/−0 and +1/−0, with the third layer still compared to layer 2.
- C# syntax colors and added/removed backgrounds appear in the native diff.
- Open PR changes opens the offline PR snapshot; a current thread appears alongside its code line.
- Review window distinguishes Discussion, Reviews and Code threads; outdated comments appear separately and all publish controls are disabled in demo mode.

The final archive adds the explicit Restore layer selection action after this native walkthrough. Its behavior is verified by headless regression testing; that last button has not received a second native walkthrough. A full release acceptance pass remains appropriate for folder-picker/restart flows, clipboard, theme changes and larger repositories.

## External verification limits

Live GitHub authentication, network fetching and publication were not exercised against an owner-selected test PR in this run. Contract tests and fake-writer tests are not substitutes for that live integration check. No comments, reviews or account changes were sent to GitHub.

TextMate was exercised on ARM64 with actual grammars and bounded input; this is not a comprehensive performance benchmark across all languages and pathological files.

The app is not Developer ID signed or notarized. Windows x64 is additionally cross-published locally; interactive Windows execution has not been checked on this Mac. Linux and Intel Mac validation remain out of scope.

## File and PR comments update

The local update adds native whole-file comments, preserves file paths on file/outdated threads, exposes PR discussion independently of Git object downloads, and saves per-file comment drafts. Regression tests cover the distinct PR/file API payloads, binary/renamed file membership, paginated file/line thread parsing, separate drafts across files and restarts, stale PR rejection, uncertain-send reconciliation, and the real review-window tabs. External comment publication remains untested and is never performed by these tests.

Local verification of the comments update: **62 tests passed**, Release build without warnings/errors, macOS ARM64 and Windows x64 self-contained archives rebuilt. Native macOS walkthrough opened PR comments directly, selected the File comments tab and Auth.cs, and displayed its file-level discussion and composer. Demo publication controls remained disabled. Windows was cross-published locally for this update; the previous release has separate native Windows CI coverage.

## Windows GHES certificate trust update

Cache downloads now select Schannel on Windows without writing Git configuration or disabling TLS/revocation verification. Certificate-chain errors include remediation and retain the original Git diagnostic. Regression coverage checks process-scoped options, preserved saved-account handling, no automatic retries after certificate failures, and unchanged handling of unrelated fetch errors.

Local verification: **67 tests passed** on macOS ARM64; Windows x64 self-contained archive cross-published successfully. The OS-specific backend assertion runs for the host platform; the new Windows branch and live corporate GHES certificate chain have not been exercised on a Windows machine in this local run. The GitHub release assets have not been updated with this change.
