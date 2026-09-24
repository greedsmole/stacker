# Stacker v0.2

A local stacked-branch and GitHub PR review app built with C#, .NET 10 and Avalonia. Release targets: **macOS Apple Silicon** and **Windows x64**. Linux and Intel Mac release work is deferred.

## Run

Install Git; install and authenticate `gh` if you want GitHub integration. A packaged `Stacker.app` includes its .NET runtime. For development install the .NET 10 SDK:

```sh
dotnet run --project src/Stacker.Desktop
# Generate and open a fresh, isolated offline demonstration:
dotnet run --project src/Stacker.Desktop -- --demo
# Open an existing repository:
dotnet run --project src/Stacker.Desktop -- /path/to/repository
```

Build a macOS ARM64 package with `python3 scripts/package.py osx-arm64`, or a Windows x64 package with `python scripts/package.py win-x64`. On Windows, extract the whole ZIP and run `Stacker/Stacker.exe`; keep its adjacent files. No separate .NET runtime is required. Git and optional GitHub CLI are installed separately. The archive is written to `artifacts/Stacker-0.2.0-osx-arm64.tar.gz`. The `.app` is not Developer ID signed or notarized. Public distribution signing requires the publisher's Apple credentials.

## Navigation and comparisons

Stacks are separate expandable groups. Layers are numbered, not represented by indentation characters. Click the chevron to collapse a group; click its title to view its combined changes.

For `main → A → B → C`:

| Action | View | Comparison |
| --- | --- | --- |
| Click the stack title | Entire stack | merge-base(main, C) → C |
| Click layer 2 | This layer | merge-base(A, B) → B |
| Click Through this layer | Through layer 2 | merge-base(main, B) → B |
| Check layers 1 and 3 | Selected layers | main → A and merge-base(B, C) → C, separately |

At the last layer, Through this layer equals Entire stack. The selected layer comparisons never combine unrelated layers into a synthetic patch. Multiple selection stays within one stack. Checkboxes and Cmd-click toggle layers. After returning to a stack, **Restore layer selection** restores its previous single/multiple-layer selection; the stack title itself always opens Entire stack.

Returning to the app **never refreshes data**. Use **Refresh** for local refs and **Refresh GitHub** for remote metadata. A no-change local refresh keeps existing diff sections. Failed refreshes keep the previous snapshot. File choice, filter, expansion and scroll state are retained in the comparison cache while navigating (up to 24 recent comparisons); theme and panel sizes persist across restarts.

## Reproducible demo

Choose **Open demo**. Every invocation creates a new repository in Stacker's application-data folder; no existing repository is overwritten.

- **Authorization:** three layers edit the same `Auth.cs`. Layer 2 adds 2 lines and removes 1; Through layer 2 adds 6 lines relative to main; Entire stack adds 7.
- **Payments:** an independent two-layer stack. Switch here and back to test navigation state.
- **Shared foundation:** Alpha and Beta share the first layer. The offline PR snapshot discovers both paths.
- **Service branches:** `master ← test ← service/request` demonstrates that `test` is a boundary, not a PR-stack layer.
- **Missing branch example:** demonstrates an error isolated to one stack.

The demo includes `demo-manifest.json` assertions, `.stackpr.yml`, offline PR metadata and sample discussion/current/outdated threads. The UI labels it **Demo / offline**. Publication is disabled, even though local draft editing works.

Suggested walkthrough: click Authorization → layer 2 → Through this layer → check layers 1 and 3 → switch to Payments and back. In GitHub stacks select a PR layer, then select code lines and click Review PR to explore discussion and drafts.

## GitHub connection and discovery

Stacker uses the **fetch URL of origin**, not gh's default repository or some other logged-in host. HTTPS, SSH URLs and SCP-style remotes are supported. It verifies both the active account on that host and access to that repository. For an SSH alias, configure `host/owner/repo` in **GitHub → Configure**. Set executable paths for Git/gh in Settings if a Finder launch cannot find Homebrew tools.

Authenticate in a terminal with `gh auth login --hostname HOST`; switch accounts with `gh auth switch --hostname HOST`. Stacker does not change your global gh account or run `gh auth setup-git`. Tokens remain owned by gh.

If an invalid `GH_TOKEN` / `GITHUB_TOKEN` (or Enterprise equivalent) overrides a valid saved account, enable **Settings → Use saved gh credentials**, save, then **Refresh GitHub**. This removes the four token environment variables only from Stacker child processes, including the Git cache credential helper. It does not change shell variables or switch the global gh account. The setting is off by default so explicit environment credentials retain their normal precedence.

Open PRs (including drafts) are fetched with pagination. A stack edge exists only when the child's base repository/branch matches another PR's head repository/branch. Names and commit history are not used as guesses. Fork repository identity matters. Ambiguous parents and cycles produce warnings.

**Stack boundaries** are exact branch names configured per repository. Defaults include the repository's default branch and existing main/master/develop/test branches. A PR into a boundary can start a stack; a link cannot pass through that boundary. Single PRs appear under Other pull requests. Branching paths show a shared-foundation label.

Discovery does not rewrite `.stackpr.yml`. Save as local stack is an explicit action and requires matching local refs. Local stack definitions keep v1 YAML compatibility.

## Isolated Git cache

Selecting a remote PR or stack downloads objects into an application-owned bare repository, partitioned by host, repository ID and account. It uses gh's credential helper only for that child Git invocation. User branches, refs, index, configuration and working tree are untouched.

The cache verifies downloaded head/base SHA against the PR snapshot and rejects a moving snapshot. Existing objects work offline; cached metadata is labeled with its timestamp. Configure → Clear downloaded Git objects removes only the selected connection's object cache. PR metadata and drafts are kept separately. Downloads can take longer than local diff commands and are cancelled when the selected comparison is superseded.

## Review workflow

Each PR layer has a **Review PR** action. Local or aggregate comparisons instead offer **Open PR changes**: inline comments must target an actual PR snapshot, not a guessed layer in an aggregate diff.

1. Open PR changes and select a code line or contiguous range on one side of the diff.
2. Open Review PR. Inspect Discussion, Reviews and Code threads.
3. Write a comment. Add it to the local draft, post it as a line comment, post a general PR comment, or reply to the selected thread.
4. Add a review summary, choose COMMENT / APPROVE / REQUEST_CHANGES and explicitly Submit review.

Current code threads appear alongside their anchored lines; outdated threads remain in the Code threads tab. Resolve/reopen is available when GitHub reports permission. Mixed deletion/addition ranges are rejected; select one side. Binary files and omitted GitHub patches cannot receive inline comments through Stacker.

Draft summary, composer text, line comments and pending publication state are stored locally by account and PR, with the base/head version. Account, repository access, current PR SHA and server diff lines are verified before publication. A stale draft is retained for manual rechecking. Remove old anchors before adopting the current PR version for the summary.

Writes are never automatically retried. Publications carry a hidden operation marker so a timeout can be reconciled by reloading the discussion. If the outcome remains unknown, check GitHub before choosing “I checked GitHub — unlock retry”. Partial/uncertain operations preserve draft text. No live comments are posted by the automated test suite.

Not included: editing/deleting published comments, GitLab, PR creation, merging, checkout, rebase, restack, push, CI dashboards or automatic polling.

## Syntax highlighting

TextMateSharp tokenizes the old and new Git blobs separately, preserving multiline lexical state. Token spans map back to unified diff line numbers; patch markers are not treated as code. Supported initial extensions include C#, JSON, YAML, XML/XAML, JS/TS, HTML/CSS, SQL, shell and Markdown.

The diff appears first. Highlighting runs in the background with cancellation and a blob/language/theme cache. Unknown languages, grammar failures, blobs above 2 MiB / 50,000 lines or tokenization exceeding the time budget fall back to plain text. There is no WebView, Monaco, LSP or go-to-definition.

File patches retain the v0.1 limit of 5 MiB / 50,000 lines. Binary, submodule, rename and mode changes have explicit metadata. Non-UTF-8 path bytes remain outside v0.2 support.

## Storage and tests

`.stackpr.yml` retains the v1 schema: stable IDs, full base ref and ordered branch refs. Saves use atomic replacement, content revision checks and a writer lock. Invalid/externally changed configuration is not overwritten. Formatting/comments are rewritten on save.

Preferences, PR snapshots, repository boundaries, drafts, Git objects and generated demos are under the platform ApplicationData/Stacker folder (`v2` for new data). No credentials are written there. Syntax caches are memory-only.

```sh
dotnet build
dotnet test -c Release
python3 scripts/package.py osx-arm64
```

The solution has four projects: Core, Infrastructure, Desktop and Tests. GitHub Actions builds, tests and packages on macOS ARM64 and Windows x64 runners. Tests exercise real temporary Git repositories, the demo manifest, headless Avalonia windows, recorded/fake gh JSON responses, review failure/reconciliation scenarios and isolated object caches. A successful contract test is not a claim of live GitHub publishing verification.

See VERIFICATION.md for the actual local verification record and THIRD_PARTY_NOTICES.md for dependency notices.
