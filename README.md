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

Build a macOS ARM64 package with `python3 scripts/package.py osx-arm64`, or a Windows x64 package with `python scripts/package.py win-x64`. On Windows, extract the whole ZIP and run `Stacker/Stacker.exe`; keep its adjacent files. No separate .NET runtime is required. Git and optional GitHub CLI are installed separately. The archive is written to `artifacts/Stacker-0.4.1-osx-arm64.tar.gz`. The `.app` is not Developer ID signed or notarized. Public distribution signing requires the publisher's Apple credentials.

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

**Git logs:** the **Git logs** tab shows each Git command while it runs, then its duration, exit status and up to 2 KiB of error output. Copy or clear the log there. Credentials embedded in HTTPS URLs are redacted. The tab includes reads and cache downloads so a slow GHES fetch is visible.

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

If an invalid `GH_TOKEN` / `GITHUB_TOKEN` (or Enterprise equivalent) overrides a valid saved account, enable **Settings → Use saved gh credentials**, save, then **Refresh**. This removes the four token environment variables only from Stacker child processes, including the Git cache credential helper. It does not change shell variables or switch the global gh account. The setting is off by default so explicit environment credentials retain their normal precedence.

Open PRs (including drafts) are fetched with pagination. A stack edge exists only when the child's base repository/branch matches another PR's head repository/branch. Names and commit history are not used as guesses. Fork repository identity matters. Ambiguous parents and cycles produce warnings.

**Stack boundaries** are exact branch names configured per repository. Defaults include the repository's default branch and existing main/master/develop/test branches. A PR into a boundary can start a stack; a link cannot pass through that boundary. Single PRs appear under Other pull requests. Branching paths show a shared-foundation label.

Discovery does not rewrite `.stackpr.yml`. Save as local stack is an explicit action and requires matching local refs. Local stack definitions keep v1 YAML compatibility.

## Isolated Git cache

Selecting a remote PR or stack fetches objects into an application-owned bare repository, partitioned by host, repository ID and account. Stacker does not clone the user repository or run pull on it. A single PR fetches only its own base/head refs; stack comparisons request just the PRs needed for that mode, and missing refs are combined into one fetch. Existing objects are reused. The fetch uses gh's credential helper only for that child Git invocation. Reading, reviewing and downloading never touch user branches, refs, index, configuration or the working tree. The explicit actions described below do: **Restack**, **Push**, **Publish**, **Add layer**, **Commit**, **Stash and restack**, **Switch** and the conflict actions each change exactly what they say, and every rewrite is backed up and reversible.

On Windows, cache downloads use Git's **Schannel** backend, which uses the Windows certificate store by default. This supports corporate GHES certificates already trusted by Windows. The backend is selected only for the download process; Stacker does not disable certificate/revocation checks or change global Git configuration. Existing explicit Schannel CA-bundle settings remain effective. Use Git for Windows with Schannel support.

If a download still reports an untrusted certificate chain, ask your administrator to check that GHES serves its intermediate certificates and that the corporate root/intermediate CA is installed in the appropriate Windows certificate stores. Successful `gh` authentication does not establish trust for Git downloads. The cache also does not inherit repository-local Git configuration from your working repository. Do not work around trust errors by disabling `sslVerify`.

The cache verifies downloaded head/base SHA against the PR snapshot and rejects a moving snapshot. Existing objects work offline; cached metadata is labeled with its timestamp. Configure → Clear downloaded Git objects removes only the selected connection's object cache. PR metadata and drafts are kept separately. Downloads can take longer than local diff commands and are cancelled when the selected comparison is superseded.

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
- **Continue** resumes the rebase with your editor for the commit message; **Skip commit** drops the conflicting commit instead. When a layer is finished the app tells you which layers are still behind — press **Restack** again to finish them.
- **Abort and restore** aborts the rebase and puts every branch the interrupted run moved back where it was, including the branch you started on.

The banner survives closing the app, and it also notices a rebase, merge, cherry-pick or revert that was started in a terminal.

**Everyday local commands.** **Commit…** in a stack's menu stages the working tree (optionally including new files) and commits it with your message — your hooks and signing configuration run exactly as in a terminal. **Stash and restack** takes your uncommitted *tracked* changes out of the way, restacks, and leaves the stash for **Restore stashed changes**. A conflicting restore simply shows up as conflicts to resolve. Untracked files never block anything. The sync window can also check out another branch (`Switch`), and **the working tree** line always shows what the repository currently holds.

## Restacking and publishing

Local stacks can be rewritten in place. **Restack…** appears in a stack's `···` menu and in the comparison header whenever a local stack is selected. It opens a preview first: every layer, its parent, the commits it will replay, and any blocker. Nothing is written until you press **Restack (local)**, and the preview re-reads the repository so it cannot describe a stale snapshot.

- **What moves:** each layer is replayed onto its parent with `git rebase --onto`, starting at the lowest diverged layer and cascading upward. Only the branches of that stack move; other refs, the index and unrelated files are left alone.
- **Before anything runs:** the tracked working tree must be clean (untracked files are ignored, and *Stash and restack* clears the rest), no rebase, merge or cherry-pick may be in progress, no stack branch may be checked out in another worktree, and a layer with merge commits is refused until you tick **Preserve merge commits** (`--rebase-merges`). Every rewritten branch is backed up under `refs/stacker/backup/<timestamp>/<branch>` first; the ten most recent runs are kept.
- **Conflicts:** the restack stops and leaves the rebase for you — see **Working on the stack locally** above. Only an unexpected failure rolls everything back; a conflict never throws your resolution away.
- **Undo:** a successful restack is journaled outside the repository. **Undo last restack** in the stack menu restores the previous commits and refuses when the working tree is dirty or a branch has moved since. Undo never changes remote branches.
- **Pushing:** **Push the stack** publishes every layer — a plain push when the remote is an ancestor, a `--force-with-lease` only when it is not. Each branch is fetched first and pushed only when the remote still holds exactly the commit the preview recorded; if someone pushed it in the meantime, nothing is pushed and the window says so. Stacker never uses a bare `--force`.
- **Pull requests:** **Check pull requests** reads the live open pull requests and lists what each layer needs — a new pull request into its parent, or a new base when a layer's parent changed. Nothing is written until you press **Publish**. Layers are matched by repository and branch identity only: a fork's pull request is never borrowed, and two open pull requests for one layer are refused rather than guessed at. A new pull request is titled with the layer's newest commit subject. Publishing is idempotent, so a second check reports nothing left to do.
- Existing pull requests pick up the rewritten commits by themselves. Drafts pinned to the previous commits are flagged by the existing stale-draft handling, and the demo repository disables every remote action.

The Git logs tab records every command these actions run.

## Syntax highlighting

TextMateSharp tokenizes the old and new Git blobs separately, preserving multiline lexical state. Token spans map back to unified diff line numbers; patch markers are not treated as code. Supported initial extensions include C#, JSON, YAML, XML/XAML, JS/TS, HTML/CSS, SQL, shell and Markdown.

The diff appears first. Highlighting runs in the background with cancellation and a blob/language/theme cache. Unknown languages, grammar failures, blobs above 2 MiB / 50,000 lines or tokenization exceeding the time budget fall back to plain text. There is no WebView, Monaco, LSP or go-to-definition.

File patches retain the v0.1 limit of 5 MiB / 50,000 lines. Binary, submodule, rename and mode changes have explicit metadata. Non-UTF-8 path bytes remain outside v0.4 support.

## Storage and tests

`.stackpr.yml` retains the v1 schema: stable IDs, full base ref and ordered branch refs. Saves use atomic replacement, content revision checks and a writer lock. Invalid/externally changed configuration is not overwritten. Formatting/comments are rewritten on save.

Preferences, PR snapshots, repository boundaries, drafts, Git objects, the recovery journal and generated demos are under the platform ApplicationData/Stacker folder (`v3`; data from the older `v2` layout is imported once and the original is kept). No credentials are written there. Syntax caches are memory-only. Lock files live there too, never in your repository. Only one instance runs at a time: starting a second one exits with a message so no two windows can write the same files.

The layering, the three contract rules and the recipe for adding a feature are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

```sh
dotnet build
dotnet test -c Release
python3 scripts/package.py osx-arm64
```

The solution has four projects: Core, Infrastructure, Desktop and Tests. GitHub Actions builds, tests and packages on macOS ARM64 and Windows x64 runners. Tests exercise real temporary Git repositories, the demo manifest, headless Avalonia windows, recorded/fake gh JSON responses, review failure/reconciliation scenarios and isolated object caches. A successful contract test is not a claim of live GitHub publishing verification.

See VERIFICATION.md for the actual local verification record and THIRD_PARTY_NOTICES.md for dependency notices.
