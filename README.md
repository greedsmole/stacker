# Stacker v0.5

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

Build a macOS ARM64 package with `python3 scripts/package.py osx-arm64`, or a Windows x64 package with `python scripts/package.py win-x64`. On Windows, extract the whole ZIP and run `Stacker/Stacker.exe`; keep its adjacent files. No separate .NET runtime is required. Git and optional GitHub CLI are installed separately. The archive is written to `artifacts/Stacker-0.5.0-osx-arm64.tar.gz`. The `.app` is not Developer ID signed or notarized. Public distribution signing requires the publisher's Apple credentials.

## Navigation and comparisons

Stacks are separate expandable groups. Layers are numbered, not represented by indentation characters. Click the chevron to collapse a group; click its title to restore its last view. Overview always opens the combined changes.

For `main → A → B → C`:

| Action | View | Comparison |
| --- | --- | --- |
| Click Overview | Entire stack | merge-base(main, C) → C |
| Click layer 2 | This layer | merge-base(A, B) → B |
| Click Through this layer | Through layer 2 | merge-base(main, B) → B |
| Check layers 1 and 3 | Selected layers | main → A and merge-base(B, C) → C, separately |

At the last layer, Through this layer equals Entire stack. Selected layers keep their own parents. Checkboxes and Cmd/Ctrl-click toggle layers within one stack. Returning to a stack automatically restores its selection, mode, file, filter and scroll position. These are also saved across restarts, together with group expansion. The last available repository reopens on launch.

The repository switcher contains recent folders, Open folder and Open demo. Stack editing lives in each stack's menu. The workspace uses a single toolbar and an embedded PR inspector, which overlays the right side below 1320 logical pixels.

**Standalone pull requests:** PRs outside discovered stacks appear under **Pull requests** as direct review entries. Click one to open its server base/head changes, code threads, discussion and review controls. No stack definition is needed.

**Search:** choose **Find**, or press Cmd/Ctrl+P for file names and Cmd/Ctrl+Shift+F for changed text. File search checks paths across all visible comparison sections. Changed text search checks added and removed lines in their pinned diff snapshots; optional Regex uses a bounded match time. Select a result to open its file and scroll to the line. Search runs on demand, can be cancelled and reports files whose patches exceed the display limit.

**Git logs:** the **Git logs** tab shows each Git command while it runs, then its duration, exit status and up to 2 KiB of error output. Copy or clear the log there. Credentials embedded in HTTPS URLs are redacted. The tab includes reads and local-repository fetches so a slow GHES fetch is visible.

**Refresh and background updates:** one Refresh checks local refs and GitHub. GitHub PR metadata and the selected PR's discussion are also read every 60 seconds while the window is active. Losing focus pauses the timer; regaining focus only restarts the delay. Background reads never download Git objects. Comments update in place; changed code or stack structure waits behind **New changes available → Update changes**. Temporary failures back off to 2/4/8 minutes, authentication failures suspend the loop, and previous data remains visible. Settings can disable background refresh.

The diff displays code, line numbers and compact hunk ranges. Git headers (`diff --git`, `index`, `---/+++`) are omitted; rename/mode/binary/submodule information is shown as file metadata. Missing final newlines remain visible. Text selection copies source text, and Cmd/Ctrl+C on selected diff rows omits markers and line numbers. **Copy patch** in the file menu still copies the original patch.

Use **Wrap code** above a diff to fit long lines to the pane; the preference is saved. For a multi-line comment, Shift-click adjacent code rows and choose **Comment on selected lines**. The editor shows the line range before you add it to a review or post it. Mixed added/removed sides are rejected with an explanation.

## Reproducible demo

Choose **Open demo**. Every invocation creates a new repository in Stacker's application-data folder; no existing repository is overwritten.

- **Authorization:** three layers edit the same `Auth.cs`. Layer 2 adds 2 lines and removes 1; Through layer 2 adds 6 lines relative to main; Entire stack adds 7.
- **Payments:** an independent two-layer stack. Switch here and back to test navigation state.
- **Shared foundation:** Alpha and Beta share the first layer. The offline PR snapshot discovers both paths.
- **Service branches:** `master ← test ← service/request` demonstrates that `test` is a boundary, not a PR-stack layer.
- **Missing branch example:** demonstrates an error isolated to one stack.

The demo includes `demo-manifest.json` assertions, `.stackpr.yml`, offline PR metadata and sample discussion/current/outdated threads. The UI labels it **Demo / offline**. Publication is disabled, even though local draft editing works.

Suggested walkthrough: Authorization → Overview → layer 2 → Through this layer → check layers 1 and 3 → Payments → Authorization. The previous selection returns automatically. Select Session API, click Review to open its PR snapshot, then use the + beside a line to add a pending review comment. Explore Discussion and Comment on file; publishing remains disabled. New demo repositories isolate their drafts from older demos.

## GitHub connection and discovery

Stacker uses the **fetch URL of origin**, not gh's default repository or some other logged-in host. HTTPS, SSH URLs and SCP-style remotes are supported. It verifies both the active account on that host and access to that repository. For an SSH alias, configure `host/owner/repo` in **GitHub → Configure**. Set executable paths for Git/gh in Settings if a Finder launch cannot find Homebrew tools.

Authenticate in a terminal with `gh auth login --hostname HOST`; switch accounts with `gh auth switch --hostname HOST`. Stacker does not change your global gh account or run `gh auth setup-git`. Tokens remain owned by gh.

If an invalid `GH_TOKEN` / `GITHUB_TOKEN` (or Enterprise equivalent) overrides a valid saved account, enable **Settings → Use saved gh credentials**, save, then **Refresh**. This removes the four token environment variables from Stacker’s gh API processes. Git downloads use the opened repository’s normal Git credentials and environment. It does not change shell variables or switch the global gh account. The setting is off by default so explicit environment credentials retain their normal precedence.

Open PRs (including drafts) are fetched with pagination. A stack edge exists only when the child's base repository/branch matches another PR's head repository/branch. Names and commit history are not used as guesses. Fork repository identity matters. Ambiguous parents and cycles produce warnings.

**Stack boundaries** are exact branch names configured per repository. Defaults include the repository's default branch and existing main/master/develop/test branches. A PR into a boundary can start a stack; a link cannot pass through that boundary. Single PRs appear under Other pull requests. Branching paths show a shared-foundation label.

Discovery does not rewrite `.stackpr.yml`. Save as local stack is an explicit action and requires matching local refs. Local stack definitions keep v1 YAML compatibility.

## Opened local repository

All workspace Git commands run from the root of the repository or worktree you opened. PR diffs, version comparisons, History and reanchoring read that repository’s object database. Opening a PR does not create another clone, bare repository or worktree.

Existing objects are reused offline. Missing PR base/head commits are fetched into the opened repository through its configured **origin**. Git uses that origin’s SSH/HTTPS URL, credentials, proxy and certificate configuration. `gh` provides GitHub API access; Stacker does not inject a gh credential helper or override Git’s SSL backend. For corporate certificates, configure Git exactly as you would for a successful fetch in the same repository from a terminal.

Downloads use namespaced `refs/stacker/pr/...` references and verify the exact PR snapshot after fetching. Historical commits are retained under `refs/stacker/history/...` so ordinary Git maintenance preserves observed versions. User branches, HEAD, staged/unstaged files, index, Git configuration and `FETCH_HEAD` are preserved during viewing. A changing server snapshot or failed download retains the previous view and reports the original Git error. Background metadata refresh does not fetch objects.

**GitHub settings → Clear PR retention references** releases only Stacker’s PR references. **Remove history pins** releases history references after active readers close. Git manages object storage and collection; these actions do not delete the repository or run forced garbage collection. Settings show the selected repository’s Git directory and object size. Memory caches remain bounded.

Earlier application-owned object directories are imported into each opened repository once and retained as migration sources. Drafts, graph/checkpoint metadata, navigation and diagnostics remain in Stacker’s application-data profile. Explicit **Restack**, **Push**, **Publish**, **Add layer**, **Commit**, **Stash and restack**, **Switch** and conflict actions continue to operate on the selected local repository.

## Review workflow

Select a GitHub PR layer: its changes and discussion load automatically. The header offers **Discussion** and **Review (N)** in the main window. On a local linked layer, Review opens the confirmed PR snapshot. Aggregate comparisons require choosing a PR layer before making inline comments.

- **Line / range:** click the + beside a line, or Shift-click line numbers to extend a range on one side. An inline composer offers **Add to review** (local draft) and **Post now** (immediate publication).
- **File:** **Comment on file** opens a composer for the selected file, including binary files. Publish explicitly with Post comment.
- **PR:** **Discussion** displays general comments and review decisions and provides its own PR composer.
- **Threads:** current threads appear with the code; replies and resolve/reopen actions are contextual. Resolved threads collapse. Outdated threads remain in the inspector and are never attached to an arbitrary current line.
- **Submit:** **Review (N)** lists pending line comments, with Edit/Remove, summary and Comment / Approve / Request changes. Request changes requires an explanation. Submission is always explicit.

Drafts are isolated by host, repository, account, PR and code version. Line, reply, file and PR composer text is kept separately. Old ambiguous composer text is displayed as a recovered draft and requires choosing a destination. Background reads do not disable editors or clear typed text.

Select adjacent diff rows with Shift-click. One blue **+** spans the selected range; click it to comment on that range. Deleted lines use LEFT; additions and unchanged context use RIGHT. Gaps and mixed-side ranges cannot form a line comment. Context comments remain supported: the [GitHub review-comment API](https://docs.github.com/en/rest/pulls/comments) explicitly accepts unchanged lines shown for context, and [GitLab's discussion API](https://docs.gitlab.com/api/discussions/#create-a-new-thread-in-the-merge-request-diff) accepts unchanged lines with both old/new coordinates. GitHub.com's [new Files changed experience](https://github.blog/changelog/2025-09-25-pull-request-files-changed-public-preview-now-supports-commenting-on-unchanged-lines/) also supports comments further outside the original hunks; Stacker currently displays patch context and validates publication against the server-provided patch.

In the line editor, **Suggest code change** prefills a standard GitHub suggestion fence with the selected right-side code. Edit the replacement inside the fence, inspect the current/replacement preview, then **Add to review** or **Post now**. Empty replacement blocks represent deletion. Existing prose is retained. Deleted-side comments cannot generate applicable suggestions. Suggestions use the existing draft persistence and PR-version validation. Loaded suggestions are automatically rendered as replacement code with a copy action, including in threads and drafts; outdated threads never borrow current source as their original code. **Open demo** includes an offline suggestion example. Suggestions are submitted as comments; applying them to a branch remains a GitHub action.

Before publication Stacker rechecks the account, access, current PR base/head and code anchors/file membership. A changed snapshot blocks anchored publication until Update changes and draft review. Existing comments are not relocated automatically. Publication keeps its original PR/account even while navigating elsewhere.

Writes are never automatically retried. A hidden operation marker allows uncertain sends to be reconciled by subsequent discussion reads. Retry recovery controls only appear when needed; if the result is still uncertain, inspect GitHub before explicitly unlocking a retry. Draft changes made after an uncertain send are retained when it is confirmed.

Not included: editing/deleting published comments, emoji reactions, GitLab, merging two branches, interactive rebase (`rebase -i`), reordering a stack and retargeting its pull requests, per-file staging with diff selection, editing pull-request titles and descriptions beyond the suggested commit subject, or CI dashboards. Linux and Intel Mac releases are deferred. A restack rewrites only the branches of the selected local stack.

## Starting and extending a stack

**Add layer…** in a local stack's `···` menu creates the next branch: give it a name and a start point (the current checkout, the stack's base, or any layer). Stacker creates and checks the branch out, then appends it to the stack definition — your commits, and committing the updated `.stackpr.yml`, stay with you. A layer that has no commits yet is still valid: it follows its parent, so a restack carries it along.

Starting a layer is refused when the working tree is dirty, when a rebase, merge or cherry-pick is in progress, when the name already exists, or when the name is not a valid Git branch name. If the branch you start from does not carry `.stackpr.yml`, Stacker writes the definition onto the new branch.

## Working on the stack locally

Stacker runs the local Git a stacked workflow needs, and never hides what it is doing.

**Conflicts are yours to resolve.** When a layer does not apply cleanly the restack stops there: the rebase stays in progress, the layers already rewritten stay rewritten, and the conflicting files are listed in the sync window and in a banner in the main window. Then:

- **Resolve in editor** opens the conflicted files in the editor you chose — the command in **Settings → Editor command**, otherwise `git config core.editor`, otherwise `GIT_EDITOR`/`VISUAL`/`EDITOR`, otherwise the platform default. Add a wait flag (`code --wait`) if your editor returns immediately.
- **Stage resolved files** stages exactly the files that were in conflict.
- **Continue** resumes the rebase with your editor for the commit message; **Skip commit** drops the conflicting commit instead. When the layer is finished, Stacker continues the remaining layers from the recorded plan. If an external branch movement makes that plan unsafe, it stops for inspection.
- **Abort and restore** aborts the rebase and puts every branch the interrupted run moved back where it was, including the branch you started on.

The banner survives closing the app, and it also notices a rebase, merge, cherry-pick or revert that was started in a terminal.

**Everyday local commands.** **Commit…** in a stack's menu stages the working tree (optionally including new files) and commits it with your message — your hooks and signing configuration run exactly as in a terminal. **Stash and restack** takes your uncommitted *tracked* changes out of the way, restacks, and keeps a saved stash entry. Choose it in the sync window and press **Restore selected stash**; multiple saved entries survive later restacks and restarts. A conflicting restore simply shows up as conflicts to resolve. Untracked files are not stashed automatically; Git can still refuse a checkout or rebase that would overwrite them. The sync window can also check out another branch (`Switch`), and **the working tree** line always shows what the repository currently holds.

## Restacking and publishing

Local stacks can be rewritten in place. **Restack…** appears in a stack's `···` menu and in the comparison header whenever a local stack is selected. It opens a preview first: every layer, its parent, the commits it will replay, and any blocker. Nothing is written until you press **Restack (local)**, and the preview re-reads the repository so it cannot describe a stale snapshot.

- **What moves:** each layer is replayed onto its parent with `git rebase --onto`, starting at the lowest diverged layer and cascading upward. Only the branches of that stack move; other refs, the index and unrelated files are left alone.
- **Before anything runs:** the tracked working tree must be clean (untracked files are ignored, and *Stash and restack* clears the rest), no rebase, merge or cherry-pick may be in progress, no stack branch may be checked out in another worktree, and a layer with merge commits is refused until you tick **Preserve merge commits** (`--rebase-merges`). Every rewritten branch is backed up under `refs/stacker/backup/<timestamp-and-operation-id>/<branch>` first; the ten most recent runs are kept.
- **Conflicts:** the restack stops and leaves the rebase for you — see **Working on the stack locally** above. An unexpected failure attempts to restore the refs it can prove it owns. If that proof is missing, recovery stops and reports it; conflicting work is left for resolution.
- **Undo:** a successful restack is journaled outside the repository. **Undo last restack** in the stack menu restores completed rebase effects and refuses when the working tree is dirty or an affected branch has moved since. All target refs are checked before one expected-SHA Git ref transaction restores them. Unexecuted layers are excluded. Undo never changes remote branches.
- **Pushing:** **Push the stack** publishes every layer under an explicit `--force-with-lease`, including fast-forward updates and branches expected to be absent. All remote layers are fetched and checked against the preview before the first push. A mismatch at that point prevents every push. A race or network failure during later pushes may leave a partially published stack; the message and journal retain the known completed branches. Stacker never uses a bare `--force`.
- **Pull requests:** **Check pull requests** reads the live open pull requests and lists what each layer needs — a new pull request into its parent, or a new base when a layer's parent changed. Nothing is written until you press **Publish**. Layers are matched by repository and branch identity only: a fork's pull request is never borrowed, and two open pull requests for one layer are refused rather than guessed at. A new pull request is titled with the layer's newest commit subject. The checked plan is pinned to the repository and GitHub identity and is compared with live PRs before writing. A second successful check reports nothing left to do; an uncertain publication is inspected before retrying.
- Existing pull requests pick up the rewritten commits by themselves. Drafts pinned to the previous commits are flagged by the existing stale-draft handling, and the demo repository disables every remote action.

The Git logs tab records every command these actions run.

## Syntax highlighting

TextMateSharp tokenizes the old and new Git blobs separately, preserving multiline lexical state. Token spans map back to unified diff line numbers; patch markers are not treated as code. Supported initial extensions include C#, JSON, YAML, XML/XAML, JS/TS, HTML/CSS, SQL, shell and Markdown.

The diff appears first. Highlighting runs in the background with cancellation and a blob/language/theme cache. Unknown languages, grammar failures, blobs above 2 MiB / 50,000 lines or tokenization exceeding the time budget fall back to plain text. There is no WebView, Monaco, LSP or go-to-definition.

File patches retain the v0.1 limit of 5 MiB / 50,000 lines. Binary, submodule, rename and mode changes have explicit metadata. Non-UTF-8 path bytes remain outside v0.4 support.

## Storage and tests

`.stackpr.yml` retains the v1 schema: stable IDs, full base ref and ordered branch refs. Saves use atomic replacement, content revision checks and a writer lock. A revision mismatch refuses saving. If an external editor races with replacement, its version is preserved in a unique `.conflict` backup and the error identifies both versions. Formatting/comments are rewritten on save.

Preferences, PR snapshots, repository boundaries, drafts, the recovery journal and generated demos are under the platform ApplicationData/Stacker folder (`v3`; data from the older `v2` layout is imported once and the original is kept). No credentials are written there. Workspace state imports the old monolithic format and saves changed stack documents separately. Journal schema 3 reads schema 2; unreadable or unsupported critical journals preserve the file and block mutations. Syntax caches are memory-only and bounded. Lock files live there too, never in your repository. Only one instance runs at a time: starting a second one exits with a message so no two windows can write the same files.

The layering, the three contract rules and the recipe for adding a feature are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

```sh
dotnet build
dotnet test -c Release
python3 scripts/package.py osx-arm64
```

The solution has four projects: Core, Infrastructure, Desktop and Tests. GitHub Actions builds, tests and packages on macOS ARM64 and Windows x64 runners. Tests exercise real temporary Git repositories, the demo manifest, headless Avalonia windows, recorded/fake gh JSON responses, review failure/reconciliation scenarios and local object retention. A successful contract test is not a claim of live GitHub publishing verification.

See VERIFICATION.md for the actual local verification record and THIRD_PARTY_NOTICES.md for dependency notices.

## Review history and stack analysis

Choose **History** beside the comparison toolbar after selecting a stack or PR. History opens inside the main window, shares its layer/file selection, and pins the selected path in the opened repository’s Git object database. Back/Forward restores the selected version, mode and file. Historical patches use the same diff viewer as current code, with commenting disabled. Open a layer to preserve its version automatically; metadata refresh records versions without downloading objects. History begins when Stacker observes a version—it cannot reconstruct a force-pushed version that was never downloaded.

Select a layer and **Load layer**, then select a file. **Mark reviewed** records an explicit, personal checkpoint; merely reading a file does not mark it. **Since my review** and **Compare versions** show what changed. When the base moved, Stacker replays the old patch onto the new merge-base and compares that expected tree with the current head. Conflicting replay, missing objects or unsupported Git capabilities produce an unavailable result, never a false “unchanged” result. This is textual analysis, not proof of semantic equivalence.

**Needs attention** lists unreviewed files, changed code and changed base context across the captured path. **Check stack** distinguishes stale parents, shared files and actual trial-merge conflicts. The checks use fixed versions and never run project code or change the user's branches, index or files. **File through stack** shows successive patches, renames and original PR threads; **Find line origin** attributes a final-file line to its latest textual addition or rewrite. An ancestry gap stops attribution.

The **Graph and history** section shows earlier dependency snapshots. Explicit parent links override discovered links for that child; cycles are refused. A local history identity can be explicitly rebound before adding the renamed ref to a stack definition. Existing independent histories are not silently merged. Closed/merged PR nodes remain in history; failed state reads are shown as unknown.

In the Review panel, **Reattach…** on an old draft comment proposes a unique exact text/context match. Inspect the old and new locations, then **Confirm new anchor**. Published comments are never moved. Ambiguous matches require manual selection.

History, review checkpoints and revision-checked drafts live in `review-history.db` under the existing application-data directory. Existing draft JSON files are imported on first read and preserved. SQLite transactions refuse competing stale draft writes. Prepared publication payloads are kept independently of later edits, and uncertain writes are reconciled without automatic resend. Historical commits are pinned inside the opened repository. **Remove history pins** requires a second click and waits for active readers; versions, checkpoints and draft text remain. Objects may later be collected by normal Git maintenance if nothing else references them.

The offline demo seeds a previously reviewed Session API version. Open Authorization → History → Session API → Load layer → Auth.cs → Since my review to inspect the subsequent change. Demo publication remains disabled.

## Workspace durability and recovery

History belongs to a repository identity shared by its worktrees; navigation belongs to the individual worktree.
Matching local branches and PRs share review checkpoints after the active GitHub account is verified. Other
accounts retain separate remote histories and drafts. To reconnect a moved repository, use **Settings and help
→ Locate moved repository…**, select its previous location, and choose the new folder.

**Export workspace…** saves a ZIP containing SQLite history/drafts, navigation, settings and journals, plus a Git bundle of the active repository’s pinned history. Other repositories’ code and credentials are excluded. **Restore workspace copy…** verifies the
checksums and database into a separate empty folder. Bundled history is imported when that profile reconnects to its repository. Older backups containing private history repositories remain readable. **Open restored workspace** opens that profile; the original
profile remains available. The source repository and its YAML definitions remain in their Git working directory;
use **Locate moved repository…** if that directory moved. `--data-root /path/to/profile` also opens a separate profile.

If closing cannot save work, Stacker offers Retry, Save a text copy, Continue editing, or explicit Close without
saving. The text copy includes unsent review text and anchor information. **Operations and recovery** shows
operation IDs and recorded effects across restarts. An explicit server rejection permits a later manual send;
an unknown result keeps sending blocked until reconciliation or a manual GitHub check.

Settings show the selected Git storage location and its current object size. Git manages disk storage; active comparisons retain their history pins until they close. **Export diagnostics…** copies bounded local metadata logs containing operation IDs, stages, durations, byte counts and problem codes.
