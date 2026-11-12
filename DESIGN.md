# Stacker desktop design

A compact workbench for reviewing stacked Git changes. ENERGY 1 / RHYTHM 2 / MOTION 1.

The comparison is the focal point: readable title, explicit branch range, then comparison modes and actions. Stack order and numbered layers provide the product's identity. Keep real branch names, file paths, line numbers and review state visible; do not introduce dashboard metrics or decorative cards.

Use charcoal and neutral light surfaces. Use VS Code Modern blue for primary actions and focus, with a lighter or darker blue tint for selected rows. File statuses use outlined Git letters so they remain readable without color. Diff additions and removals retain their semantic colors and textual markers. Use Inter for interface text and monospace for code. Controls have small radii and no ornamental shadows, gradients or glow.

File navigation can collapse for focused code reading. The file pane offers a flat list and a folder tree with the same filter and selected file. History offers a folder picker above the diff. At widths below 1320 logical pixels, review occupies the main content area. Wider windows dock review beside the diff. Preserve the stack navigator and explicit Close action in both modes. Minimum content size is 1060 by 640.

Settings and Refresh are named controls. Rare maintenance actions remain under More. New controls must invoke existing actions or implement useful behavior; no decorative controls. Keep pending review text local until explicit submission.

Dialogs use a clear heading, scrollable form or diagnostic body, and persistent actions. At their minimum dimensions, content scrolls before actions disappear. Escape cancels; the unsaved-work warning returns to editing. Labels describe fields and actions directly.

History opens the current diff in the available workspace. Compact controls above it select the layer, version, and file. The file picker identifies the section for comparisons that include base changes or multiple layers. Less frequent comparison, blocker, and graph actions open from Tools. Disabled actions reflect missing layer, file, checkpoint, or version prerequisites. Clearing the layer also clears its comparison.

Search distinguishes an unavailable comparison, an invalid pattern and a valid query with no matches. Empty workspace and loading are separate states. Error diagnostics remain selectable and scrollable with an explicit dismiss action. Warning text uses semantic color with sufficient contrast on the notice surface in each theme.

Verify both themes, minimum window size, file collapse/restore, comparison selection and review layout after changes. Do not claim a full accessibility audit from source inspection or a successful build alone.
