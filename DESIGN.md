# Stacker desktop design

Stacker is a native workbench for developers creating, inspecting, and reviewing dependent Git branches. Connected branch rows make each stack readable from base to tip while the selected change opens alongside it. The design draws on Graphite’s branch visualization, inspection flow, and explicit checkout action. ENERGY 1 / RHYTHM 2 / MOTION 1. [Graphite extension workflow](https://graphite.com/docs/vs-code-extension-quick-start)

## Workspace

The first view of an open repository pairs the stack graph with the diff. A compact workbench toolbar keeps repository switching, Changes, History, and pane controls in reach. The graph starts at the actual base. Connected nodes show the path to the tip, a filled node and selected row identify the inspected layer, and HEAD labels the checked-out local branch. Show an active stack’s name and layer position in the comparison header so the graph stays reachable when hidden.

The navigator is 240 px wide by default. The optional 220 px Files pane uses the same list and folder tree in Changes and the History file picker. Focus temporarily hides navigation and the review inspector. Saved pane preferences apply after settings migration. In a fresh profile the selected PR's visible diff viewport must occupy at least 75% of window width and height at 1060×640 and 1400×880. Clip the measurement to the scroll container and window. Keep the app toolbar around 44 px, the comparison header around 54 px, and the file header around 36 px. Longer paths and branch names can trim with a tooltip.

The graph’s working-tree summary leads to the changed paths and commit form. Selecting a layer opens its comparison. **Check out selected branch** changes the working tree through the local Git command. **Sync stack** opens the restack and publication preview for the selected local stack. **Stack actions** and **PR tools** collect additional context-specific actions. Native menus carry checked states and keyboard navigation.

Working tree keeps the stack navigator beside the staged or unstaged file list and diff. Stage and unstage sit with the file selection. The commit message and **Commit staged** stay in the footer. GitHub CLI stack commands live in Local stacks when the repository uses that backend. A stopped CLI rebase exposes its recovery actions in the banner and its conflicted files in Working tree.

## Visual hierarchy

Use blue-grey chrome, subdued separators, and an ink canvas in Dark. Light uses cool neutral chrome and paper-like code. A single blue accent marks primary actions and focus. The inspected branch and selected file use a quieter tint. Inter handles labels and actions. Monospace keeps branch refs, timestamps, relative paths, line numbers, and source easy to scan.

Diff gutters align both line numbers and the change marker against a quiet code canvas. Added and removed rows tint both the code and the gutter. Explicit plus and minus markers, outlined Git file letters, and named states keep the meaning readable independently of color. File review progress reflects real checkpoints in the current comparison.

The shared tokens, native theme resources, and component styles live in `src/Stacker.Desktop/Design`. [Design system](docs/DESIGN_SYSTEM.md)

## Review and secondary views

Discussion and Review open on request. Dock the 360 px inspector when the diff can keep at least 520 px. At narrower sizes, the inspector offers **Back to diff**. Inline threads and drafts open through their disclosures. Keep draft text local until the user submits. Escape dismisses the inspector and restores focus to its trigger.

History opens the current comparison and puts the layer, earlier version, and file picker above the diff. Clearing the layer clears its comparison. Search distinguishes unavailable comparisons, invalid patterns, and queries with no matches. Empty workspace, loading, and actionable error states use separate guidance. Diagnostic text stays selectable and scrollable.

Dialogs use a shared heading, a scrollable form or plan, and a persistent action footer. Restack previews keep branch order visible next to step status. Class diagrams use the same controls, semantic Git colors, and selection treatment as Changes. Stack disclosure, keyboard navigation, and resizing respond immediately. The inspector retains its short pointer-triggered entrance to show where it opened. Reduce motion skips it.

## Verification

Render both themes at the minimum and default dimensions. Check the stack graph, checkout marker, files, compare modes, code viewport, inspector, history, class diagram, and dialogs. Exercise keyboard activation, review draft persistence on resize, and checkout in a temporary repository.
