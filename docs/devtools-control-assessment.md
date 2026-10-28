# DevTools custom controls and WinUI alternatives

Assessment for [#1177](https://github.com/microsoft/winappCli/issues/1177), dated October 27, 2026.
Source revision: `7abfef22dc039746be023a493fd38bcd58e76705`.

## Migration in this branch

The visual tree now uses `TreeView`. Both view selectors use `SelectorBar`,
the Size & spacing and authored-XAML sections use `Expander`, and resource-key
suggestions use `AutoSuggestBox`. The toolbar uses `MenuFlyout` with standard
check/radio items and a `ToggleSplitButton` for Comments.

The other conditional changes remain out of scope. See the
[DevTools guide](guides/devtools.md) for the controls' current interaction.
The inventory below describes the revision above, before migration. Its source
links are pinned to that revision, including the removed custom tree peers.

## Summary

**DevTools already uses WinUI for most buttons, text fields, property expanders, and collection rendering. The main opportunities are places where it assembles those pieces into behavior that WinUI already provides.** For example, the visual tree is a custom `StackPanel` with button rows, expansion logic, keyboard navigation, and accessibility providers, rather than a `TreeView`.

The best replacement candidates are:

| What the user sees | WinUI alternative | Recommendation |
|---|---|---|
| Visual tree with expandable, selectable nodes | `TreeView` and `TreeViewItem` | Replace the custom tree presentation, preserving DevTools' filtering and connection to app elements. This is the largest change. |
| Visual tree / Comments tabs and Computed / Set switch | `SelectorBar` | Replace the hand-built selectors, subject to the target app's WinUI version. |
| Size & spacing and source-XAML disclosures | `Expander` | Replace the custom show/hide headers. Property rows already demonstrate this approach. |
| Resource-key field with suggested keys underneath | `AutoSuggestBox` | Replace the suggestion UI, retaining DevTools' key discovery and explicit apply behavior. |
| Comments and More menus on the floating toolbar | `MenuFlyout` and its standard item types | Appropriate controls, but prove overlay placement, input, and focus behavior before migrating. |

**Do not replace every custom visual.** Element outlines, margin and padding guides, comment pins, toolbar docking targets, and the accessibility bridge serve inspection-specific purposes. There is no equivalent built-in control that removes their core logic.

These are maintainability and control-choice recommendations, not demonstrated runtime defects or merge blockers.

## Scope and terminology

This is a source-based inventory of the production UI under `src/winapp-devtools`: the inspector window, the in-app toolbar, the comment panel, and inspection overlays. It covers all eight XAML files, the C++ code that creates UI dynamically, and the custom control subclasses and automation peers. The managed agent contains inspection and binding support, not another DevTools UI. Test fixture apps are not product controls.

Here, **custom** includes a standard control with a replacement template, an assembled widget with its own interaction logic, or a derived control with custom accessibility behavior. A `Grid` used to arrange ordinary labels is not, by itself, a custom control.

An **automation peer** exposes a UI element to UI Automation, the Windows accessibility interface used by screen readers and testing tools.

**WinUI** below means controls shipped in `Microsoft.UI.Xaml`. The **Windows Community Toolkit** is a separate dependency. Its `GridSplitter` is not a built-in WinUI control.

The tables distinguish:

- **Replace:** a standard control matches the existing interaction.
- **Conditional:** a control fits part of the requirement, but migration needs a behavior or hosting decision.
- **Keep:** the existing composition or custom behavior is appropriate.
- **Already WinUI:** custom content or data wiring, not a replacement control implementation.

## Inspector window inventory

| Place and source | What DevTools defines today | Appropriate WinUI alternative | Assessment |
|---|---|---|---|
| Visual tree: [row creation][tree-rows], [keyboard and expansion][tree-view], [accessibility][tree-automation] | `DevToolsTreePanel : StackPanelT` and `DevToolsTreeRow : ButtonT`, with separate chevron toggles. DevTools computes indentation, visible parents, selection, expansion, focus movement, and scroll reveal. `DevToolsTreePeer` and `DevToolsTreeItemPeer` supply tree accessibility patterns. | `TreeView`, `TreeViewNode`, and `TreeViewItem`, with custom item content for type, name, and preview. | **Replace.** This is an actual custom tree, not merely a styled standard tree. Keep the app-element handles, filtering, and live-update model; move standard tree interaction to WinUI. See migration constraints below. |
| Visual tree / Comments tabs: [shell, lines 39-126][shell] and [view switching][comments-view] | Two `RadioButton`s with the `DevToolsViewTab` replacement template. The template draws hover states, selected text, and an underline. Code switches the two panes. | `SelectorBar` with two `SelectorBarItem`s. | **Replace, version permitting.** These are two fixed views, not documents that can be closed or rearranged, so `TabView` would add the wrong interaction. Preserve the open-comment count and accessible names. |
| Computed / Set property views: [property header, lines 296-312][pane-rows] and [selection handler, lines 381-396][pane-sinks] | Two adjacent `ToggleButton`s with opposite corner radii. Click handlers enforce one active view by rebuilding the pane. | `SelectorBar`. | **Replace, version permitting.** One view is selected at a time. WinUI should own selection state; DevTools still owns which properties each view shows. |
| Size & spacing disclosure: [markup, lines 596-613][props-layout] and [handler, lines 418-430][pane-sinks] | `HyperlinkButton`, chevron text, and a separately hidden body. The handler changes visibility, glyph, and saved expanded state. | `Expander`. | **Replace.** Put the existing layout content inside it and preserve the saved state. Do not replace the diagram inside the section. |
| Authored XAML disclosure: [property header, lines 193-224][pane-rows] and [handler, lines 433-445][pane-sinks] | `HyperlinkButton` with a drawn chevron, a one-line preview, and a full selectable source block. Code swaps visibility and glyphs. | `Expander`, with the preview in its header and full XAML in its content. | **Replace.** Retain the source-confidence notice and selectable source text. The one-line preview can remain custom header content. |
| Ancestor path: [property header, lines 133-160][pane-rows], [path construction][source-resources], and [handler, lines 402-414][pane-sinks] | Elided and full `TextBlock`s plus a Show all ancestors / Show less link. The individual ancestors are not navigation targets. | `Expander` for the current disclosure; `BreadcrumbBar` if ancestors become navigable. | **Conditional.** `BreadcrumbBar` fits a clickable navigation path, not an exact replacement for today's read-only, wrapped full path. Do not add ancestor navigation solely to justify changing controls. |
| Resource-key suggestions: [shell, lines 379-387][shell] and [editor, lines 459-535][editors] | `PResKey` is a `TextBox`; `PResSug` is a `StackPanel` populated with up to six `Button`s. `ResSuggestSink` copies the chosen key into the field. | `AutoSuggestBox`. | **Replace.** It supplies suggestion-list interaction and keyboard selection. DevTools must still choose compatible keys and resolve resources. Selecting a suggestion must fill the field without applying a resource to the app. |
| Scalar numeric values: [shared text editor][shell] and [editor, line 880 onward][editors] | The generic `PEdit` `TextBox` also handles rows whose editor kind is `number`. DevTools owns parsing and commit behavior. | `NumberBox` for values that are strictly numeric. | **Conditional.** It can provide stepping and numeric entry, but must not silently change the meaning of empty, unset, non-finite, or rejected values. Keep `TextBox` for values that require literal text rather than a numeric value. |
| Structured values such as Thickness, Point, and Vector3: [editor, lines 804-877][editors] | Dynamically generated labeled `TextBox` columns, one per field, using names and values from the inspection model. | A composition of `NumberBox` controls where the fields are numeric. | **Conditional, partial replacement only.** WinUI has no single property editor for these structures. Retain the field layout, per-field names, and whole-value commit logic. |
| Tree/properties pane divider: [shell, lines 221-229][shell], [resize handlers, lines 308-376][pane-sinks] | A WinUI `Thumb` plus custom width clamping, drag handling, and Left/Right keyboard resizing. | No built-in WinUI `GridSplitter`. The Community Toolkit provides one. | **Keep under the current dependency model.** This already uses WinUI's drag primitive. Adding Toolkit controls to an injected UI requires a separate dependency and loading assessment. |
| Focus-tracking notice: [shell, lines 172-178][shell] and [notice update][tree-view] | A hidden `Border` containing wrapped status text. | `InfoBar` for a persistent notice requiring attention. | **Conditional, low priority.** Use it if the notice represents an ongoing unavailable state. A short explanatory status line does not automatically need an `InfoBar`. |
| Property staging, confirmation, errors, and binding details: [shell, lines 436-529][shell] and [write outcomes][pane-sinks] | Custom layouts for binding status, pending edits, Apply/Cancel, Replace binding/resource confirmation, write errors, and value provenance. Their inputs and actions are standard controls. | Existing `Grid`, `StackPanel`, `TextBlock`, and `Button` compositions. | **Keep.** These are domain-specific editor content. A generic dialog or `InfoBar` is not a replacement for the editing state machine, and a modal confirmation would change the workflow. |
| Compiled-binding explanation: [shell, lines 405-431][shell] and [focus reveal handler][editors] | An information `Button` has a standard tooltip, plus inline explanatory text revealed on focus. | `Flyout` could present the explanation on invocation. | **Conditional, low priority.** This is already a standard button and tooltip. A flyout would change how the explanation opens and closes; it is not needed merely to remove custom layout. Preserve keyboard access to the explanation. |
| Open-comment count on the Comments tab: [shell, lines 116-123][shell] | Rounded `Border` plus `TextBlock`, collapsed at zero. | `InfoBadge`. | **Conditional, small cleanup.** It matches a numeric count badge. Preserve zero-hiding and avoid a duplicate screen-reader announcement. Account for the target runtime and the selector's content slots. |
| Property collection and expandable rows: [property repeater][pane-rows], [row template][shell], [element factory][expansion] | `ItemsRepeater` with `StackLayout`; `PropRowFactory` creates data-driven rows from `DataTemplate`s. Each expandable property header is a WinUI `Expander`. | Already uses the appropriate WinUI building blocks. | **Already WinUI.** Custom row factories and editor contents do not make this a hand-built list or expander. There is no reason to introduce a grid control to replace this property sheet. |
| Comments collection: [comment row and list, lines 173-331][comments] and [element factory, line 362 onward][comments] | `ItemsRepeater` with `StackLayout`, file-group captions, a standard `Button` for each comment, and separate `HyperlinkButton` actions. Dots and labels communicate element availability. | Existing repeater composition; `ListView` if list selection becomes a requirement. | **Already WinUI; keep for the current action-based design.** A custom item layout is expected. `ListView` is not an automatic improvement when each row invokes an action and contains more actions rather than maintaining list selection. |

### Why the visual tree is the largest replacement

The current implementation provides behavior that a platform tree normally owns:

```text
TreeScroll (ScrollViewer)
  DevToolsTreePanel (derived StackPanel)
    Grid: indentation and columns
      ToggleButton: expansion chevron
      DevToolsTreeRow (derived Button): node content and selection
```

Its automation peers separately implement selection, item selection, expansion/collapse, and invocation. A `TreeView` can own the normal tree roles and interaction while its item content still shows DevTools-specific labels.

This is not a one-tag substitution. Preserve these existing behaviors:

- Picking an app element selects and reveals its corresponding node; hovering a row highlights the app element.
- Just my XAML changes the visible hierarchy, not the underlying inspected app.
- Filtering shows matches and disables expansion until the filter is cleared.
- Live tree changes retain the selected element where possible.
- The renderer has a 1,000-node population limit and creates rows lazily as they become visible. Replacing the control must not be presented as removing that limit or guaranteeing better performance without measurement.
- Accessible names distinguish otherwise identical nodes, and stale automation objects are rejected.

The existing [tree layout tests][tree-layout-tests] and [tree automation UI tests][tree-ui-tests] provide starting points for preserving these behaviors. Some assertions about the custom classes and peers would need to change with the implementation.

## Floating toolbar and comment panel inventory

| Place and source | What DevTools defines today | Appropriate WinUI alternative | Assessment |
|---|---|---|---|
| Collapsed DevTools chip: [toolbar, lines 50-92][toolbar] | A `Button` with a replacement template containing a rounded border, dot/halo, and label. It expands the toolbar and can also start a drag. | Standard `Button` with content, `CornerRadius`, padding, and lightweight resource overrides. | **Conditional.** Try removing the replacement template while keeping the chip content. The custom docking and drag behavior remains necessary; there is no stock DevTools launcher control. |
| Expanded command strip: [toolbar, lines 95-215][toolbar] | Rounded `Border` and horizontal `StackPanel` with standard buttons and toggle buttons. DevTools manages reveal, auto-collapse, pinning, and focus return. | `CommandBar` or `CommandBarFlyout` for a conventional command surface. | **Conditional, not a first replacement.** Neither automatically supplies this docked, hover-expanding, non-modal toolbar. Preserve compact sizing and avoid stealing focus from the inspected app. Keeping the container is reasonable even if its menus become standard. |
| Pick, Comments, and Layout toggle visuals: [toolbar, lines 119-190][toolbar]; inspector Pick/Focus indicators: [shell, lines 135-156][shell] | Standard `ToggleButton`s with extra named on/off icon layers or an accent underline. Code updates both `IsChecked` and the extra visual parts. | Standard `ToggleButton` checked visuals with lightweight styling. | **Conditional, worthwhile simplification.** The controls are already WinUI. Remove duplicated visual-state bookkeeping where the standard selected appearance meets the design; do not describe this as replacing custom input controls. |
| Comment mode plus adjacent arrow: [toolbar, lines 139-169][toolbar] | A `ToggleButton` and a separate `Button`, with matching corner radii and negative spacing so they look joined. | `ToggleSplitButton`, with a `MenuFlyout` on its secondary action. | **Conditional, strong semantic match.** Its primary action is a mode toggle and its secondary action opens options. Prove overlay hosting, compact layout, count display, and focus behavior along with the menu migration. |
| Comments and More menu containers: [toolbar, lines 255-380][toolbar] and [menu behavior, lines 500-592][toolbar-code] | Hidden `Border`/`StackPanel` surfaces in the toolbar canvas. Code measures and positions them, tracks hover, closes them after a timer, focuses the first action, and handles Escape. | `MenuFlyout` with `MenuFlyoutItem` and `MenuFlyoutSeparator`. | **Conditional, recommended after a host proof.** These are command menus. Standard menus should replace the manually assembled interaction, but standard flyout dismissal differs from today's pointer-leave timer. Retain the visible instruction for turning DevTools back on after choosing Don't start DevTools. |
| Show comment markers and Keep toolbar open menu rows: [toolbar, lines 268-282 and 340-355][toolbar] | `ToggleButton`s with separately shown check glyphs. | `ToggleMenuFlyoutItem`. | **Replace as part of the menu migration.** It owns the check state and menu-item interaction. The underlying settings and marker logic remain DevTools code. |
| Startup-mode radio menu: [toolbar, lines 291-376][toolbar] | `DevToolsMenuRadio` replacement template draws a radio dot and menu-like row. Three `RadioButton`s share a group. | Grouped `RadioMenuFlyoutItem`s. | **Replace as part of the menu migration.** Show toolbar, Hide toolbar, and Don't start DevTools are mutually exclusive menu choices. |
| Toolbar comment-count badge: [toolbar, lines 154-161][toolbar] | Rounded `Border` and a small numeric `TextBlock` layered over the comment toggle. | `InfoBadge`. | **Conditional, small cleanup.** A count badge is a direct visual match, but it does not solve the surrounding toggle/split-button layout. |
| Toolbar drag handle: [toolbar, lines 107-118][toolbar] and [drag behavior, lines 751-856][toolbar-code] | A `Border` with pointer handlers and dot/halo feedback. A separate full-surface catcher tracks the drag; code chooses and saves a docking position. | `Thumb` for the drag gesture. | **Conditional, partial replacement.** A `Thumb` may remove gesture plumbing, but cannot replace snap-position selection or persistence. The current catcher is deliberate; verify pointer capture and coordinate behavior before removing it. |
| Comment editor panel: [panel XAML][selection-panel], [markup and content][selection-anchor], and [selection tracking][selection-tracking] | `Popup` hosting a custom card with title, source, multiline `TextBox`, Save/Delete/Close buttons, and other comments. Custom code positions it beside the selected element and handles moving or disappearing elements and unsaved drafts. | `Flyout` for a conventional transient editor. | **Conditional; keep the host unless a prototype preserves the workflow.** Standard inputs are already used. A flyout does not replace element tracking or draft handling, and normal light-dismiss behavior is not equivalent to this inspection panel. `ContentDialog` would make it modal and is not recommended. |
| Element-disappeared warning in the editor: [panel, lines 41-53][selection-panel] | Custom icon-and-text warning band tells users to copy unsaved changes before closing. | Non-closable warning `InfoBar`. | **Conditional, reasonable fit.** This is a persistent state change that needs attention. Check the narrow card layout and preserve the unsaved-content warning. |
| Save/delete progress and errors: [panel, lines 83-85 and 117-119][selection-panel] and [writer status][comment-writer] | Wrapped status `TextBlock`s with polite live-region announcements. | Existing text for short progress; `InfoBar` only for an ongoing problem requiring action. | **Keep the compact status by default.** Do not turn every result line into a banner. The async save state and error recovery remain custom logic either way. |
| Other comments and likely-source confirmation: [generated panel content, lines 106-137][selection-anchor] | Read-only text cards and a standard `CheckBox` next to source text. | Existing WinUI controls and layout. | **Already WinUI.** No specialized replacement is needed for these content blocks. |
| Open in DevTools action: [panel, lines 122-133][selection-panel] | Standard `Button` styled as an icon-and-text link. | `HyperlinkButton` could provide the link-like presentation. | **Conditional, cosmetic only.** It opens a tool window rather than a URL. Keeping a regular command button is also appropriate. |
| Comment saved/deleted notice: [toast, lines 499-572][comment-writer] | A custom, non-hit-testable `Border` with icon and text, positioned beside the toolbar and removed after 1.8 or 4 seconds. It supplies a polite live-region name. | No exact built-in WinUI snackbar/toast equivalent. | **Keep the small overlay notice.** `InfoBar` is intended for inline, persistent status, not a floating short-lived confirmation. `TeachingTip` adds teaching/targeting behavior, and a Windows app notification changes the scope to the system notification surface. |

### Why standard menus need a hosting check

DevTools is injected into the app being inspected. It uses that app's loaded WinUI runtime and XAML reader, rather than bringing up an independent UI framework instance. See [runtime activation][overlay-build].

The preferred overlay host is the XAML diagnostics UI layer, a surface outside the app's normal visual tree. When that layer is unavailable, DevTools uses popups. The [host implementation][overlay-host] also handles a specific ordering problem: windowed popups draw above the diagnostics layer, but input can reach the layer first. `HostYieldToWindowedPopups` disables input on covered DevTools chrome.

The current toolbar menus deliberately stay in the same canvas layer. Moving them to `MenuFlyout` should therefore be a contained prototype before deleting the existing implementation. Check all docking positions, both host paths, keyboard focus return, Escape, app dialogs and dropdowns, and screenshots that exclude DevTools chrome.

The inspector's existing `ColorPicker` in a `Flyout` shows that standard flyouts are already used in the product. It does not, by itself, prove that a flyout opened from the diagnostics-layer toolbar will behave the same way.

## Inspection-specific visuals and infrastructure

These are custom UI, but they are not substitutes for ordinary WinUI widgets.

| Place and source | Custom behavior | Appropriate standard replacement? | Assessment |
|---|---|---|---|
| Comment markers on app elements: [pin template and positioning, lines 153-228][comment-pins] | A circular `Button` replacement template draws an exact pixel-aligned disc and glyph. Markers follow element bounds and can sit side by side. They are excluded from normal keyboard and screen-reader navigation; the Comments pane is the accessible route. | No dedicated comment-pin control. `InfoBadge` is a status indicator, not an equivalent interactive marker. | **Keep.** A normal styled button could be evaluated for appearance, but the anchoring, pixel geometry, and accessible alternate route remain custom. |
| Hover/selection outline: [adorner XAML][adorner] and [overlay picking][pick-code] | A non-interactive border at the inspected element's bounds. | No built-in element-inspector outline. | **Keep.** `Border` is already the appropriate WinUI drawing primitive. |
| Selected-element tag and leader line: [selection XAML][selection] and [panel XAML][selection-panel] | Custom label and connecting line positioned relative to the selected element and comment card. | No stock label-and-leader control. | **Keep.** `ToolTip` or `TeachingTip` would introduce different visibility and input behavior. |
| Live margin, padding, alignment guides, and size label: [layout-adorner XAML][layout-adorner] and [tracking code][overlay-tracking] | Rectangles, colored bands, guide lines, and a size chip follow actual app geometry. | No built-in WinUI layout-inspection control. | **Keep.** These are diagnostic drawings, not ordinary controls being recreated. |
| Inspector box-model diagram and parent-fit thumbnail: [diagram builders, lines 198-433][props-layout] | Nested rings show margin, border, padding, and content, with labels and a small diagram of placement in the parent. | No stock WinUI box-model visualizer. | **Keep.** Continue composing `Grid`, `Border`, shapes, and text. `BuildFitThumb` is a thumbnail builder, not a custom replacement for the WinUI `Thumb` control. |
| Toolbar docking targets and glow: [toolbar, lines 216-250][toolbar] and [snap logic, lines 714-749][toolbar-code] | Six outlined target areas and a nearest-target highlight appear during a drag. | No stock six-position toolbar docking control. | **Keep.** Even if `Thumb` handles dragging, the docking rules and feedback remain custom. |
| Element-pick input catcher: [pick-catcher XAML][pick-catcher] and [pick behavior][pick-code] | Transparent full-surface border receives pointer input while the user chooses an element. | No ordinary button, list, or picker has this role. | **Keep.** It intentionally intercepts app clicks in pick mode. |
| Toolbar-drag input catcher: [drag-catcher XAML][drag-catcher] and [drag behavior][toolbar-code] | Transparent full-surface border receives move/release input during toolbar movement. | Potentially pointer capture through `Thumb`, not another full-surface control. | **Conditional, coupled to the drag-handle assessment.** Do not remove it independently of the drag implementation. |
| Diagnostics-layer accessibility bridge: [host, lines 20-62][overlay-host] | `DevToolsLayerBridge : GridT` and `DevToolsLayerBridgePeer` expose the layer content's automation peers through a zero-size popup, so assistive tools can reach it. | No stock control bridges the diagnostics layer into the normal accessibility tree. | **Keep.** This solves a hosting boundary, not the appearance of a widget. Replacing menus or toolbar buttons does not remove that requirement. |

## Controls that are already standard

Do not count the following as custom control implementations merely because C++ creates or wires them:

| UI | Existing WinUI implementation |
|---|---|
| Inspector window and shell | A WinUI `Window`, with `Grid`, `ScrollViewer`, and panels. See [window creation][window-lifecycle] and [shell XAML][shell]. |
| Property row expansion | `Expander` with lightweight resource overrides for compact headers, not a replacement `ControlTemplate`. |
| Boolean values | `ToggleSwitch`. |
| Enum values, binding kind/mode, and color presets | `ComboBox`. |
| Color editing | Swatch `Button`, hex `TextBox`, and a standard `ColorPicker` inside a `Flyout`; see [editor, lines 730-781][editors]. |
| Text values, binding paths, filters, and comments | `TextBox`, including multiline editing where needed. A plain filter does not need `AutoSuggestBox` unless it actually offers suggestions. |
| Property and comment collections | `ItemsRepeater` with `StackLayout`, data-driven item creation, and standard child controls. |
| Actions and help | `Button`, `ToggleButton`, `HyperlinkButton`, `CheckBox`, and `ToolTipService`. |

Likewise, WinUI has no built-in general-purpose property grid or `DataGrid` that should replace the inspector's entire property pane. Third-party or Toolkit grids would introduce another dependency and would still need the binding, resource, provenance, and edit-confirmation logic.

## Suggested order of work

1. **Use standard disclosure and selection controls in the inspector.** Replace the Size & spacing and authored-XAML disclosure plumbing with `Expander`. Replace the two fixed-view selectors with `SelectorBar` once runtime support is established.
2. **Replace the resource suggestion UI with `AutoSuggestBox`.** Keep key discovery and assignment separate. Choosing a key should not unexpectedly write to the app.
3. **Migrate the visual tree as a focused change.** Keep the inspection model and app-element mapping. Compare behavior and responsiveness against the existing tree before removing its custom controls and automation providers.
4. **Prove toolbar flyouts, then migrate the menu family together.** Use `MenuFlyout`, standard check/radio items, and, if its layout fits, `ToggleSplitButton`. Do not combine this with a wholesale toolbar-host rewrite.
5. **Leave conditional cosmetic changes until there is a concrete benefit.** Badges, numeric fields, command-strip styling, and drag plumbing are smaller or behavior-sensitive opportunities, not prerequisites for the main replacements.

### Runtime and behavior limits

`SelectorBar` is documented for Windows App SDK **1.5 and later**. DevTools parses its UI through the target app's runtime. The SDK used to compile DevTools is therefore not enough to establish that a replacement is available in every app it can inspect. Confirm the supported target-runtime baseline before using it unconditionally. If older runtimes are supported, standard grouped radio buttons are an alternative to the replacement tab template; this assessment does not establish a need for a compatibility shim.

This report did not launch DevTools, measure performance, or prototype replacements. Statements about current implementation are based on the cited source. Conditional choices require checking keyboard navigation, UI Automation, Light/Dark/High Contrast themes, unsaved edits, and overlay input behavior before implementation is considered complete.

## WinUI references

These Microsoft documentation pages establish the control matches above:

- [TreeView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/tree-view): hierarchical content, expansion, selection, and custom item content.
- [SelectorBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/selector-bar) and [API availability](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.selectorbar?view=windows-app-sdk-1.5): switching among a small set of views.
- [Expander](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/expander): a persistent header with expandable content.
- [AutoSuggestBox](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/auto-suggest-box): text input and suggestion selection.
- [NumberBox](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/number-box): numeric values, stepping, and validation; not arbitrary literal text.
- [Menus](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/menus), [RadioMenuFlyoutItem](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.radiomenuflyoutitem), and [ToggleSplitButton](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.togglesplitbutton): command, check/radio, and split-toggle interactions.
- [BreadcrumbBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/breadcrumbbar): navigable ancestor paths and overflow.
- [InfoBadge](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/info-badge): numeric, icon, or dot indicators that can be placed inside other UI.
- [InfoBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar): persistent inline status, not floating short-lived action confirmations.
- [Community Toolkit GridSplitter](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/windows/sizers/gridsplitter): a separate Toolkit control for resizing grid rows and columns.

[shell]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/xaml-window/window-shell.xaml
[tree-rows]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.TreeRows.inc#L125
[tree-view]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.TreeView.inc
[tree-automation]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.TreeAutomation.inc#L36
[comments-view]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.Comments.inc#L524
[pane-rows]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.PaneRows.inc
[pane-sinks]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.PaneSinks.inc
[props-layout]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.PropsLayout.inc
[source-resources]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.SourceAndResources.inc#L224
[editors]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.Editors.inc
[expansion]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.Expansion.inc#L645
[comments]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.Comments.inc
[toolbar]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/xaml/toolbar.xaml
[toolbar-code]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsOverlay.Toolbar.inc
[selection-panel]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/xaml/selection-panel.xaml
[selection-anchor]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsOverlay.SelectionAnchor.inc
[selection-tracking]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsOverlay.SelectionPanel.inc
[comment-writer]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsOverlay.CommentWriter.inc
[comment-pins]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsOverlay.CommentMode.inc
[overlay-build]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsOverlay.Build.inc#L32
[overlay-host]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsOverlay.Host.inc
[adorner]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/xaml/adorner.xaml
[selection]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/xaml/selection.xaml
[layout-adorner]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/xaml/layout-adorner.xaml
[overlay-tracking]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsOverlay.Tracking.inc
[pick-catcher]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/xaml/pick-catcher.xaml
[drag-catcher]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/xaml/drag-catcher.xaml
[pick-code]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsOverlay.Pick.inc
[window-lifecycle]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/native/WinApp.DevTools.Native/DevToolsWindow.Lifecycle.inc
[tree-layout-tests]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/test/treelayout-tests.cpp
[tree-ui-tests]: https://github.com/microsoft/winappCli/blob/7abfef22dc039746be023a493fd38bcd58e76705/src/winapp-devtools/test/tree-automation-ui-tests.cpp
