# Verification — Stacker v0.2

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
