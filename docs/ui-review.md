# Desktop UI review

## Scope and direction

A compact Git review workbench, with ENERGY 1 / RHYTHM 2 / MOTION 1. Charcoal and warm paper surfaces, restrained green selection/actions, readable branch and file context, and no decorative dashboard content. See `DESIGN.md`.

The previous workspace, diff, and compact-review changes remain in place. This pass completes the secondary screens: history, search, settings, GitHub settings, comparison help, repository relocation, unsaved-work recovery, add layer, stack editing, commit, and sync preview.

## Findings and fixes

| Screen/state | Impact | Implemented change |
| --- | --- | --- |
| Small settings/help/recovery dialogs | Long forms and diagnostics competed with actions | Shared heading, scrollable body, and persistent wrapping action footer |
| Commit, clean and invalid message | Clean state had no useful explanation; content could crowd validation and actions | Explicit clean-state message, disabled staging/commit, visible message label, bounded file/error areas |
| Stack editor | Symbol-only reorder controls and excessive minimum height | Named Move up/Move down/Remove actions, wrapped subtitle, smaller minimum height |
| Sync preview | Blockers and long messages could consume the window | Bounded scroll areas for blockers and diagnostics, smaller default/minimum heights |
| History document | Dense maintenance controls and duplicate file navigation consumed code width | Layer/version controls first, one file navigator, secondary tools in named expanders, larger document area |
| History selection cleared | Previous comparison status remained and review actions looked available | Clear current version/file/document/status; command availability follows layer, file, checkpoint, and version prerequisites |
| Search | Scope and query were weakly labelled; zero results did not suggest a next action | Named fields, explicit no-comparison guidance, useful no-match guidance, retained regex validation |
| First run/loading | An empty comparison could look like finished loading | Separate loading and empty states; loading identifies the work; empty state offers repository/demo actions |
| Errors | Long diagnostics displaced useful content | Bounded, selectable diagnostics and explicit Dismiss action |
| Keyboard dismissal | Dialog exit behavior was inconsistent | Escape invokes Cancel/Close; unsaved-work Escape explicitly returns Continue editing |
| Light warning text | Warning text on notice surface measured 4.43:1 | Darkened warning token to #875816; now 5.18:1 |

## Visual evidence

`artifacts/ui-review` contains **52 PNG captures**, 26 per theme. These are rendered from actual Avalonia controls using Skia, real view models, and an isolated offline demo repository. They are not design mockups. Diagnostic and invalid-input examples are explicit test fixtures.

Main-window captures use **1060 × 640 logical pixels**. Each dialog uses its declared minimum size:

| Surface | Minimum | Captured states in both themes |
| --- | --- | --- |
| Workspace | 1060 × 640 | Empty, loading, long error |
| Search | Main minimum | Results, no match, invalid regex, no comparison |
| History | Main minimum | Loaded layer, current diff, expanded tools, cleared selection |
| Settings | 480 × 420 | Top, scrolled bottom, keyboard focus |
| GitHub settings | 480 × 400 | Top and scrolled bottom |
| Comparison help | 460 × 360 | Top and scrolled bottom |
| Repository relocation | 480 × 280 | Form and footer |
| Unsaved work | 500 × 340 | Diagnostic and all recovery choices |
| Add layer | 500 × 360 | Empty-name validation |
| Stack editor | 520 × 560 | Existing stack with named reorder actions |
| Commit | 520 × 420 | Clean working tree and empty-message validation |
| Sync preview | 700 × 560 | Offline plan and disabled/unavailable actions |

Visual inspection checked hierarchy, theme consistency, wrapping, scroll boundaries, visible actions, selected/disabled states, and keyboard focus. Text clipped at a scroll viewport can be reached by scrolling; dialog actions remain outside those viewports.

Representative screenshots are committed for review: [dark history](ui-review/Dark-history-document.png), [light history](ui-review/Light-history-document.png), [dark commit validation](ui-review/Dark-commit-validation.png), and [light commit validation](ui-review/Light-commit-validation.png). The remaining captures and TRX output stay in the local, ignored artifacts directory and can be regenerated with the command below.

## Validation

- Release desktop build succeeded with zero warnings/errors.
- Self-contained `osx-arm64` publish succeeded; current app bundle is `/tmp/StackerComplete.app`.
- **9 targeted tests passed, 0 failed**, including both UI themes, `DesktopTests`, and `HistoryWorkflowTests`. Result: `artifacts/ui-review-results/ui-review.trx`.
- Tests exercise demo selection, search outcomes, error dismissal, populated history documents, selection reset and command availability, dialog opening, Tab focus, Escape dismissal, safe unsaved-work Escape, clean commit disablement, and form validation.
- The final test run used Skia capture mode. Normal test runs retain the lightweight headless renderer.
- `git diff --check` passed.

Reproduce the capture and regression pass:

```sh
STACKER_UI_CAPTURE_DIR="$PWD/artifacts/ui-review" dotnet test tests/Stacker.Tests -c Release --no-restore \
  --filter 'FullyQualifiedName~UiReviewTests|FullyQualifiedName~DesktopTests|FullyQualifiedName~HistoryWorkflowTests' \
  --logger 'trx;LogFileName=ui-review.trx' --results-directory artifacts/ui-review-results
```

Earlier native checks covered main workspace and compact review in both themes, file-pane collapse, settings theme switching, and preservation of a local review summary. The final bundle was launched with `--demo --data-root /tmp/stacker-complete-profile`; its process was verified running. **The final native click-through could not be repeated because macOS was locked and computer-use automatic unlock failed.** The final secondary-screen evidence is the Skia-rendered test matrix above.

## Antislop Delivery Gate

Statuses below are scoped to the changed desktop surfaces. This report does not claim an application-wide accessibility certification or a click-through of every existing Git operation. The user's requested evidence-and-limitations report takes precedence over treating unverified platform behavior as a pass.

### Hard gate

- R-02 PASS: new headings, descriptions, and state guidance use plain punctuation. Existing repository/diagnostic content is not rewritten.
- R-03 PASS: both themes inspected at the supported minimum dimensions in the capture matrix; code retains intentional horizontal scrolling.
- R-17 PASS: no new product statistics or invented metrics.
- R-18 PASS: no testimonials or avatars introduced.
- R-23 PASS: no new visual assets or navigation structure introduced.
- R-24 PASS: existing navigation targets retained; new controls call existing settings, search, and dismissal handlers.
- R-25 PASS for measured shared tokens: primary text 6.72:1; dark selection 9.39:1; light selection 9.62:1; dark warning 6.74:1; light warning 5.18:1. Exhaustive syntax-token and platform-rendered contrast was not measured.
- R-26 PASS for changed controls: error dismissal, search, history loading/current document, dialog validation and cancellation exercised; unavailable history actions disabled.
- R-27 PASS: empty, loading, no-result, invalid-input, diagnostic, and disabled states captured in both themes.
- R-28 N/A: no FAQ surface.
- R-32 PASS for exercised dialog paths: Tab assigns focus; focus ring rendered in both themes; Escape closes settings, GitHub settings, help and relocation; unsaved-work Escape returns to editing.
- R-33 PASS: behavior implemented in compiled application source; no runtime source/CSS patching.
- R-34 PASS: full secondary-screen capture matrix in both themes.
- R-35 PARTIAL: Release build, publish, 9 targeted tests and rendered click/keyboard paths passed. Final native input inspection was blocked by the locked desktop; destructive Git/network operations were not triggered for visual testing.
- R-36 PASS: measurements and test results are recorded without unsupported performance or compliance claims.
- R-37 PASS: approved direction is recorded in DESIGN.md.
- R-38 PASS: screenshots use the application's visibly labelled offline demo; diagnostics are test fixtures.
- R-39 PASS for added/changed forms: native controls, visible field labels, accessible names, native hover/focus/disabled states, safe dismissal. Exhaustive assistive-technology traversal remains unverified.
- R-40 N/A: no web media, scripts, or field-performance claims introduced.

### Purpose gate

- R-01 PASS: solid theme surfaces; no new gradients or glow.
- R-04 PASS: explicit action text; reorder glyphs replaced with names.
- R-06 PASS: Inter for compact UI and monospace for code, as documented.
- R-07 PASS: no decorative background patterns.
- R-08 PASS: arrows only indicate branch comparison or navigation relationships.
- R-09 PASS: no decorative headline badges.
- R-10 PASS: no glass surfaces added.
- R-12 PASS: no ornamental shadows added.
- R-13 PASS: no glow added.
- R-14 PASS: dialogs and history derive structure from their actual fields/actions, not feature cards.
- R-19 PASS: no decorative animation added; existing indeterminate progress only represents active work.
- R-22 PASS: no generic illustrations added.

### Liveliness

- Dials PASS: ENERGY 1 / RHYTHM 2 / MOTION 1 are explicit.
- Dial consistency PASS: restrained palette, differing workspace/form hierarchy, no decorative motion.
- Focal point PASS: comparison document, search query/results, or dialog heading and task action.
- Whitespace PASS: heading/body/footer separation and history sidebar/document separation.
- Accent PASS: deliberate primary actions and selected state use green.
- Identity PASS: stacked branch order, real file paths, branch ranges and code retain the product's identity.
- Design Read PASS: compact developer-review workbench established before this pass.

### Craftsmanship and quality locks

- C-1 PASS: each changed layout and color decision has a concrete finding above.
- C-2 PASS for changed controls: behavior exercised or command disabled when its prerequisites are absent.
- C-3 PASS: every region serves selecting, comparing, reviewing, configuring or recovering work.
- C-4 PASS for the rendered matrix and exercised keyboard paths; other platforms and exhaustive keyboard traversal remain unverified.
- C-5 PASS: no fabricated customer, security or performance claims.
- R-05 PASS: document workspace, search form and operation dialogs follow distinct tasks.
- R-11 PASS: small control radii, no pill-shaped component system.
- R-15 PASS: task-specific actions such as Save settings, Create layer, Mark reviewed and Locate new folder.
- R-16 PASS: new copy is concrete, without marketing filler.
- R-20 PASS: stack/layer/file context remains the organizing structure.
- R-21 PASS: working light and dark appearances; both rendered.
- R-29 PASS: neutral surfaces, green action/selection accent, semantic diff and warning colors.
- R-30 PASS: no borrowed product layouts or new visual assets.
- R-31 PASS: purpose is documented in DESIGN.md and the findings table.

### UI skill supplement

- Palette PASS: DESIGN.md and theme resources agree.
- Accent PASS: selected state and deliberate primary actions.
- Emoji PASS: none added to interface copy.
- Rhythm PASS: compact navigation, readable document, separated form footer.
- Template shapes PASS: no bento cards, fake terminal, pricing, or decorative stripes.
- Headline badges PASS: none added.
- Destinations/behavior PASS for changed controls: existing handlers plus tested search/dismissal/state behavior.
- Motion PASS: no new decorative motion.
- Glass/glow/shadows/radii PASS: restrained native controls and solid surfaces.
- Status markers PASS: selected/reviewed/disabled/error states correspond to real view-model state.
- Decision-based composition PASS: compare a layer, select a file, inspect code, then mark reviewed.
- Data honesty PASS: labelled demo and explicit diagnostic fixtures.
- Placeholders PASS: field-specific instructions; no invented people or business data.
- Empty/loading/error PASS: reasons and next actions captured.
- Themes/minimum/keyboard PASS within the matrix and exercised paths; final native repeat blocked as noted.
- Media/performance N/A: no new media or performance claims.
