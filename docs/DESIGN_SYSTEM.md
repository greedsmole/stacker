# Stacker design system

## Decision

Use Microsoft Fluent 2 design guidance through the native `Avalonia.Themes.Fluent` implementation, with `DensityStyle="Compact"`. Stacker already targets Avalonia 11.3.22 and includes that exact theme package. The framework documents compact density, light/dark variants and palette customization, so this foundation works with the existing controls and desktop targets. [Avalonia themes](https://docs.avaloniaui.net/docs/styling/themes)

The implementation adopts Fluent 2's semantic tokens, command hierarchy, menu behavior and form patterns. Microsoft publishes Fluent 2 guidance and component implementations for several platforms; Stacker applies those interaction patterns using native Avalonia controls. [Fluent 2](https://fluent2.microsoft.design/), [design tokens](https://fluent2.microsoft.design/design-tokens)

FluentAvaloniaUI was also evaluated. Its 2.5.1 release supports Avalonia 11.3.12 or later, while its 3.1.0 release requires Avalonia 12.1.0 or later. The additional controls do not address a missing Stacker requirement, so replacing the installed theme would add migration work without improving this workflow. [FluentAvaloniaUI 2.5.1 dependencies](https://www.nuget.org/packages/FluentAvaloniaUI/2.5.1), [3.1.0 dependencies](https://www.nuget.org/packages/FluentAvaloniaUI/3.1.0)

## Implementation

- `src/Stacker.Desktop/App.axaml` configures the native Compact theme and imports the system.
- `src/Stacker.Desktop/Design/FluentTokens.axaml` owns the spacing/type/shape ramp, semantic colors and bridges to Avalonia's native resource keys.
- `src/Stacker.Desktop/Design/FluentControls.axaml` owns Stacker's component appearances and compact workbench exceptions.
- `DESIGN.md` defines the product-specific layout and code-space acceptance criteria.

The native resource names are verified against the pinned framework's [base resources](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/11.3.22/src/Avalonia.Themes.Fluent/Accents/BaseResources.xaml) and [control resources](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/11.3.22/src/Avalonia.Themes.Fluent/Accents/FluentControlResources.xaml).

| Role | Resources | Purpose |
| --- | --- | --- |
| Spacing | `StackerSpace4/8/12/16/24` | A consistent 4 px ramp connects fields and separates tasks |
| Type | `StackerBodyFontSize`, `StackerCaptionFontSize`, `StackerTitleFontSize` | 13 px compact body, 12 px supporting copy, 20 px dialog titles |
| Shape | `ControlCornerRadius`, `OverlayCornerRadius` | 4 px controls, 8 px transient surfaces |
| Surfaces | `CanvasBrush`, `PanelBrush`, `ChromeBrush`, `HeaderBrush`, `InputBrush` | Code, supporting navigation, application frame and inputs have clear roles |
| Content | `TextPrimaryBrush`, `TextSecondaryBrush`, `OnAccentBrush` | Theme-aware text and readable primary actions |
| Interaction | `Primary*Brush`, `HoverBrush`, `PressedBrush`, `Selection*Brush` | One accent for the primary task and separate selection feedback |
| Code semantics | `DiffAdded*Brush`, `DiffRemoved*Brush`, `DiffHunkBrush`, `File*Brush` | Git information keeps its own semantic colors and textual markers |

Fluent's spacing guidance uses a 4 px base with smaller optical-alignment exceptions. Native control padding can retain those exceptions; section and field relationships use the shared ramp. [Fluent layout](https://fluent2.microsoft.design/layout)

## UX rules

The default task is reading code. Keep the measured viewport limits in `DESIGN.md`; supporting panels open on request. `PRs & stacks` exposes both navigation sources, and the empty repository offers a direct Browse pull requests action.

Toolbars remain one line. Group commands by the object they affect: comparison tools in Stack actions or PR tools, file navigation and display commands in File actions. Frequent review and comparison-mode controls stay visible. [Fluent toolbar guidance](https://fluent2.microsoft.design/components/web/react/core/toolbar/usage)

Command lists use native `MenuFlyout` and `MenuItem`, including a checked menu item for Wrap code. Native menu navigation, activation and Escape dismissal belong to the framework. Inputs and repository selection remain form flyouts because they collect information. [Fluent menu guidance](https://fluent2.microsoft.design/components/web/react/core/menu/usage), [Avalonia MenuFlyout](https://docs.avaloniaui.net/controls/menus/menuflyout)

Give the current task one primary action. Opening Review changes its toolbar trigger to a neutral state while the submission surface provides the primary action. Escape closes Review and restores focus to the control that opened it. Closing a menu with Escape leaves its underlying review surface open. [Fluent button guidance](https://fluent2.microsoft.design/components/web/react/core/button/usage)

Review tabs state their scope: Discussion, File comments, All threads and Submit. Help text distinguishes saved local comments from immediate publication. Publishing controls reflect required text and review-decision inputs; publication services still perform their complete safety and snapshot validation.

Forms use permanent labels and helper text. Dialog actions stay visible while the body scrolls. Keep at most three footer choices; place alternative recovery paths or destructive definition edits in clearly labeled body sections. Git operations require explicit activation. [Fluent fields](https://fluent2.microsoft.design/components/web/react/core/field/usage), [Fluent dialogs](https://fluent2.microsoft.design/components/web/react/core/dialog/usage)

## Verification

Verify Dark and Light, 1060×640 and 1400×880, actual visible code bounds, pointer/keyboard command activation, checked-menu persistence, Escape/focus restoration, local draft preservation, minimum-size forms and recovery controls. Use focused UI/workflow tests and rendered captures. A build alone does not establish accessibility or layout correctness.
