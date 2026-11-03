# Architecture migration A–G

Source: DeepSeek Harness, “MVP gaps and build plan”, accepted architecture plan (event 2045), and the independent review of 2026-10-15.

Implemented in the existing four projects: A contracts, B storage/lifecycle, C Git commands and recovery,
D GitHub publication, E feature ownership and bounded resources, F UI reporting/state, G fitness/docs/packages.
No dependencies or projects were added. The original work is preserved in Git history; this register records the current mechanisms and their evidence.

The register distinguishes automated scenarios from source inspection. “Tested + inspected” does not mean every
failure mode has a dedicated fault-injection test. Platform behavior, large-repository performance and live server
integration are separate acceptance checks; see [VERIFICATION.md](../VERIFICATION.md). Storage race preservation
and unknown-effect recovery intentionally retain the limitations stated below.

| ID | Finding | Disposition | Evidence |
| --- | --- | --- | --- |
| M1 | Interrupted rewrite leaves no recovery record | Tested + guarded | Write-ahead schema 3, Started/Done; RecoveryTests crash case, CommandExecutionTests corrupt-journal guard. Unknown ownership refuses restoration. |
| M2 | Undo under-restores after a resolved conflict | Tested | RecoveryTests.A_resolved_conflict_is_still_fully_undoable; Continue completes the recorded cascade. |
| M3 | Stash destroys the undo record | Tested | RecoveryTests.A_stash_never_hides_undo; independent stash collection, StateMigrationTests restore by SHA. |
| M4 | Stale `.stackpr.yml.lock` blocks saving forever | Tested | StoreTests held-lock reacquisition and stale repository-marker scenario. |
| M5 | Window can become unclosable | Tested | RecoveryUiTests.A_window_closes_even_when_nothing_can_be_saved; bounded command stop and persistence attempts. |
| M6 | Corrupt workspace JSON locks a repo out | Inspected + store test | NavigationController stages loads, defaults only after StoreCorrupt; StoreTests damaged-document quarantine. Read failures retain previous repository. |
| M7 | Two instances interleave temp writes | Tested | StoreTests single-instance ownership and unique temporary cleanup; SafetyRegressionTests startup import. |
| M8 | Publish plan names a bogus base / is not revalidated | Tested | PullRequestPublicationTests remote base; StackSyncUiTests live-plan and context-ownership refusals. |
| M9 | Reload during a send wipes an unrelated draft | Tested + inspected | ReviewTests uncertain-send reconciliation; ReviewPublicationController pins composer and sent snapshot. Navigation/Update changes refuse busy sessions. |
| m1 | Range built over rendered rows | Tested | ReviewSelection uses commentable coordinates; ReviewToolsTests multi-line selection and plus-button range scenarios. |
| m2 | No textual changes state unreachable | Tested | DiffQualityTests.A_patch_without_hunks_explains_itself. |
| m3 | `diff.submodule` not pinned | Tested | DiffQualityTests.The_diff_command_pins_the_submodule_format. |
| m4 | `ReadBlobAsync` bypasses wrapper | Inspected + integration | GitRepositoryReader.ReadBlobAsync uses hardened process request; SyntaxTests exercise actual blob highlighting. |
| m5 | Regex budget per call, timeouts misreported | Inspected + functional test | SearchRequest five-second shared deadline, 100 ms match bound, separate timeout counter; matching runs on worker. ReviewToolsTests search behavior, no pathological-regex timing benchmark. |
| m6 | Search re-diffs every file per query | Inspected | Composition shares CachingGitReader with view and search; SHA/path cache keys, 32 entries / estimated 48 MiB budget. |
| m7 | Stale search hits after sections clear | Tested | DiffQualityTests.Search_results_are_cleared_with_their_sections. |
| m8 | Caches retain patches and tokens | Inspected | Disposed DiffSectionViewModel clears buffers; views capped at 24, patch and syntax retained-size estimates, oversized results excluded. No whole-process memory benchmark. |
| m9 | Corrupt settings silently reset | Tested + inspected | StoreTests damaged settings and unknown theme; ISettingsDiagnostics shown by NavigationController. StateMigrationTests blocks future-settings overwrite. |
| m10 | Lock/temp files in working tree | Tested | StoreTests.The_stack_file_saves_without_touching_the_repository; lock handles in app data, temporary replacement files swept. |
| m11 | 30-second timeout on every read | Inspected | GitRunner selects read 2 min, write 5 min, network 3 min and interactive 30 min policies; GitRepositoryReader read requests use bounded read policy. |
| m12 | Stale pending remote on reopen | Inspected + workspace tests | NavigationController resets pending state on explicit open and refreshes GitHub even for the same root; WorkspaceV3Tests pending-change preservation. |
| m13 | Review cache unbounded, never detached | Tested + inspected | 12-session linked-list LRU; SessionLifecycleTests verifies touch/eviction/draft reload and view detachment; cancelled initialization is disposed. |
| m14 | Demo repos never pruned | Inspected + demo test | DemoRepositoryGenerator prunes marked generated directories, keeps new plus two previous; failure cleanup and Windows attributes handled. Demo manifest test exercises generated history. |
| m15 | Post-cancel backoff/suspend | Tested + inspected | RefreshCoordinator checks cancellation before recording errors; WorkspaceV3Tests manual-clock pause, backoff and auth suspension. |
| m16 | Save returns refresh outcome | Inspected | NavigationController.SaveStacksAsync returns successful persistence separately from the following refresh diagnostic. |
| m17 | Auth diagnosis unreachable on exit 1 | Tested | GitHubCliTests auth-status JSON on failure; API write failures remain failures (R4). |
| m18 | `async void` rethrows draft-save error | Tested + fitness | AsyncHandler catches event failures; ArchitectureTests forbids raw async void in features; safety draft-flush regression. |
| m19 | `bool.Parse` on persisted marker | Tested | FrameworkTests damaged PendingOperation parsing; ReviewViewModel keeps malformed markers blocked pending explicit reconciliation. |
| m20 | Three tabs share one composer | Tested | ComposerKey separates destinations; WorkspaceV3Tests and FileCommentsTests preserve buffers across tabs/files/restarts. |
| m21 | Close before ready skips persistence | Tested + inspected | MainWindow always invokes ShutdownAsync; RecoveryUiTests close failure path, no ready-state persistence guard. |
| m22 | Double dispose | Tested | RecoveryUiTests.Disposing_the_view_model_twice_is_harmless. |
| m23 | Banner/action discards result | Tested + inspected | CommandBus reports outcomes; RecoveryUiTests restore/dismiss; SyncController.UndoAsync returns actual outcome status. |
| m24 | Stale plan after mutation | Tested | StackSyncUiTests.Preview_replans_after_restack_and_undo_in_the_same_window; other local actions use the same refresh path and invalidate PR preview. |
| m25 | Command log misses renamed git binary | Tested on macOS | LocalGitTests.A_configured_git_binary_is_still_logged; resolved executable path updated on settings save. Windows symlink branch not exercised locally. |
| m26 | Package script offers deferred Linux | Inspected + package check | package.py choices limited to osx-arm64 / win-x64; obsolete Linux branch removed. |
| m27 | Notices omit native dependencies | Inspected | THIRD_PARTY_NOTICES.md includes SkiaSharp / HarfBuzzSharp; packaging copies notices. |
| n1 | Dead tokenize call, per-line theme lookup | Inspected + syntax tests | Empty-line tokenization establishes initial grammar state; palette resolved once per file, SyntaxTests multiline/color coverage. |
| n2 | Empty highlight fallback cached | Inspected | TextMateSyntaxHighlighter caches only nonempty successful outputs within its budget. |
| n3 | Brush parsing can throw during layout | Inspected + UI suite | SyntaxText uses Color.TryParse and a fallback. |
| n4 | Double PropertyChanged subscription | Inspected | New review subscribes once; eviction, repo change and disposal unsubscribe; LRU test verifies view detachment. |
| n5 | TOCTOU on revision check | Guarded, limitation documented | AtomicFile.WriteIfUnchangedAsync validates preimage and preserves a racing external version in .conflict. StorageTests cover ordinary stale revisions. Arbitrary external-editor CAS is not promised. |
| n6 | Unbounded comparisons, full JSON clone per click | Tested + inspected | 40 views per stack; WorkspacePersistence caches changed snapshots and writes changed stack documents plus index. StateMigrationTests migration, queued-snapshot isolation and failed-write retry. |
| n7 | No settings schema, unvalidated theme | Tested | StoreTests settings schema/theme validation; future schema blocks writes including shutdown defaults. |
| n8 | Unbounded reap on kill path | Tested + inspected | ProcessRunnerTests timeout/cancel/output bounds; five-second cleanup budget also bounds pipe-drain wait. |
| n9 | Executable lookup repeated per command | Inspected | GitExecutable and GhExecutable cache resolution; settings override invalidates it. |
| n10 | Device hook path on Windows | Fitness + code inspection | Neutral creates unique real empty hook directory/config; ArchitectureTests rejects /dev/null/NUL policies. Native Windows execution still external. |
| n11 | Empty ReviewSession abstraction | Inspected | Removed empty abstraction; actual ReviewDraft/Publication/Recovery controllers own behavior, ReviewViewModel owns presentation state. |
| R1 | Undo resets unexecuted layers | Tested | SafetyRegressionTests.Undo_never_resets_a_planned_but_unexecuted_layer. |
| R2 | Undo refuses after partially restoring refs | Tested | SafetyRegressionTests.Undo_checks_every_layer_before_moving_any_ref; expected-SHA ref transaction. |
| R3 | Amended parent replayed as child commits | Tested | SafetyRegressionTests.Amend_parent_replays_only_the_childs_own_commits; proven fork-point cutoff. |
| R4 | JSON API errors reported as successful sends | Tested | SafetyRegressionTests rejects HTTP 403/404/422/500 with JSON stdout and nonzero exit. |
| R5 | Journal failure permits Git mutation | Tested | SafetyRegressionTests.Failed_journal_write_prevents_every_ref_change; CommandExecutionTests corrupt critical journal. |
| R6 | Draft persistence failure permits send | Tested | SafetyRegressionTests.Failed_draft_flush_blocks_publication_and_preserves_text. |
| R7 | Startup lock suppresses v2 import | Tested | SafetyRegressionTests.Startup_lock_does_not_suppress_legacy_import. |
| R8 | Next restack forgets pending stash | Tested | SafetyRegressionTests.Another_restack_keeps_unrestored_stash; StateMigrationTests multiple stash identity/restart. |
| R9 | Abort of a terminal merge undoes a previous completed restack | Tested | SafetyRegressionTests.Aborting_a_terminal_merge_does_not_undo_an_earlier_completed_restack; recovery only restores an interrupted restack. |
| R10 | Partial push offers branch restoration / misleading rollback | Tested | SafetyRegressionTests.A_partial_push_never_offers_branch_restoration_or_claims_remote_rollback; CommandRecoveryTests explicit partial outcome and effect list. |
