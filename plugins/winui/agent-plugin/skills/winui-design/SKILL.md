---
name: winui-design
description: "Use when designing, reviewing, or fixing WinUI 3: sample/control discovery with winapp find-ui, project-aware API checks with winapp find-api, layout planning, Fluent Design, theming, accessibility, and XAML binding. Requires WinApp CLI 0.7+. Load before authoring new XAML, reviewing UI PRs, migrating desktop UI, or choosing WinUI controls/patterns. Also use to find WinUI Gallery or Community Toolkit samples."
---



## Search samples before writing XAML

This workflow requires WinApp CLI **0.7+**. Use `winapp find-ui` for grounded control and sample discovery. **Front-load lookups, then code**:

```powershell
winapp find-ui "<focused feature>"                    # compact matches + scenario IDs
winapp find-ui --id <scenario-id>                    # full XAML/C# + prerequisite notes
winapp find-ui --id <id-1> --id <id-2> --json        # batch, structured output
winapp find-ui --list                                # browse all default-source scenarios
winapp find-ui "<feature>" --refresh                 # force a corpus refresh
```

Default search covers the WinUI Gallery, Windows Community Toolkit, and curated core patterns. Reactor's C#-only/MVU samples are opt-in with `--source reactor`; use them only for Reactor projects. The Gallery/Toolkit/Reactor corpus is fetched and cached by WinApp CLI, while core patterns work offline.

## Verify APIs against the restored project

Before coding API assumptions, run from the **restored app project directory** so `winapp find-api` uses its exact references, not a sample's possibly newer package versions:
```powershell
winapp find-api InfoBar --json --project-dir .
winapp find-api check-property InfoBar Severity IsOpen Message Title --json --project-dir .
winapp find-api members NavigationView --filter selected --json --project-dir .
```
Use **either** `--project-dir <app-directory>` (defaults to cwd) **or** `--project <name>` to select among multiple projects, never both. An unscoped query from a repo root or sibling project may target the wrong API surface. `--project sdk` searches only the installed machine SDK, not the app's NuGet references. If results look stale, check restore/project selection before refreshing. Use `winapp find-api --help` for discovery/index commands rather than guessing an API from memory; use `find-ui` for the usage pattern.

## App-shape anchors

Pick the closest shipping app silhouette before laying out a page:

| App type | Anchor controls | Reference apps |
|----------|-----------------|----------------|
| Settings / config tool | `NavigationView` Left + `SettingsCard` / `SettingsExpander` | Windows Settings, Slack |
| Document / session editor | `TabView` + full-bleed content, light chrome | Windows Terminal, VS Code, Notepad |
| Hierarchical browser | `TreeView` + `ListView` + `BreadcrumbBar` | File Explorer, Outlook |
| Developer tool / dashboard | `NavigationView` + card layout | Dev Home, GitHub Desktop |
| Single-purpose utility | Mode switcher + compact grid | Calculator, Snipping Tool |
| Media / canvas / hero | `Grid` with hero surface, floating commands, **no** `NavigationView` | Photos, Spotify, Clipchamp |

## Reach-for-this control map

Before writing XAML, map the requirement to a platform control. These mappings exist to short-circuit cross-framework instincts (WPF `DataGrid`, web `<select>`, HTML `<input type=date>`):

- **Navigation:** 2–7 sections → `NavigationView`; document/session tabs → `TabView`; breadcrumb trail → `BreadcrumbBar`; 2–3 modes → `SelectorBar`.
- **Data display:** Vertical list → `ListView`; tiles/grid → `GridView` or `ItemsRepeater` + `UniformGridLayout`; hierarchy → `TreeView`; **tabular → `ListView` with a `Grid`-based `ItemTemplate` and a header `Grid` above** (WinUI has no `DataGrid`; don't default to `CommunityToolkit.WinUI.Controls.DataGrid` — its columns can't use `x:Bind`); master-detail → `ListView` + detail `Grid`.
- **Input:** Text → `TextBox`; number → `NumberBox`; search → `AutoSuggestBox`; date → `CalendarDatePicker`; boolean → `ToggleSwitch`; pick one from 2–3 → `RadioButtons`; pick one from 4+ → `ComboBox`.
- **Feedback:** Blocking decision → `ContentDialog`; contextual action → `Flyout` / `MenuFlyout`; onboarding / hint → `TeachingTip`; inline status / async progress → `InfoBar`; system notification → `AppNotification`.

If the mapping above doesn't fit, run `winapp find-ui "<intent>"` before improvising.

## Window sizing (WinUI 3 specifics)

> **WinUI 3 has no `SizeToContent`.** Without an explicit size, Windows defaults the main window to ~1024×768 — oversized for most utilities. Size it in `MainWindow`'s constructor.

**Rubric.** Width = widest row + 48 padding, rounded up to nearest 20. Height = 32 (titlebar) + Σ(row heights) + Σ(spacing) + 48 padding, rounded up to 20. Round up — clipped content is a worse failure than a slightly-wide window. Sanity ranges (derive yours from the rubric):

- Single-purpose utility → ~440–560 wide
- Form / single-page tool → ~600–800 wide, ~640–800 tall
- Multi-pane (nav + content) → ~1100–1300 wide, ~720–840 tall
- Document / canvas / media editor → 1280+ wide

`AppWindow.Resize` takes **physical pixels**, not DIPs — multiply by the monitor's DPI scale. `XamlRoot.RasterizationScale` is null in the constructor and stale after `AppWindow.Move`, so `[DllImport] GetDpiForWindow` is the cleanest path:

```csharp
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Windows.Graphics;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();
        var hwnd  = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        // widthDip / heightDip come from the rubric above — derive, don't copy.
        AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }
}
```

Don't size the window by setting `Width`/`Height` on the root `Grid` — that clips content, not the window.

## XAML landmines (the things you'll otherwise ship broken)

### Check the effective `x:Bind` mode

`x:Bind` defaults to `OneTime` **unless an inherited `x:DefaultBindMode` changes it**. Use `OneWay`/`TwoWay` plus change notifications for dynamic data; explicit `Mode` overrides the inherited default. Stable command/event bindings need no blanket mode rewrite.

```xml
<!-- Without an inherited default, this is OneTime -->
<TextBlock Text="{x:Bind Vm.Status}" />
<!-- Updates when Vm raises the relevant change notifications -->
<TextBlock Text="{x:Bind Vm.Status, Mode=OneWay}" />
```

In a page/window, `x:Bind` resolves against code-behind (e.g., its `Vm` property), not `DataContext`. Use `x:DataType` on typed **DataTemplates**, not on `Page` to set a VM. Runtime `{Binding}`/`DisplayMemberPath` can be appropriate; for AOT, their source classes may need `partial` plus `[WinRT.GeneratedBindableCustomProperty]`. See `winui-packaging`'s `references/sourcegen-patterns.md` instead of treating all runtime binding as unsupported.

### `TextBox` two-way needs `UpdateSourceTrigger=PropertyChanged`

```xml
<TextBox Text="{x:Bind Vm.Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
```

Default trigger resolves to `LostFocus` specifically for `TextBox.Text` (most other properties default to `PropertyChanged`). The VM is not updated per keystroke, and UIA keyboard-simulation tests (WinAppDriver `SendKeys`, etc.) that assert immediately after typing will see stale VM state until focus moves.

### Attached properties from C# use static setters, not initializers

```csharp
using Microsoft.UI.Xaml.Automation;

// ❌ WRONG — does not compile. CS0117: 'Button' does not contain a definition for 'AutomationProperties'.
// AutomationProperties is a static class of attached-property accessors, not an instance member.
var btn = new Button { AutomationProperties = { AutomationId = "BtnSave" } };

// ✅ CORRECT
var btn = new Button { Content = "Save" };
AutomationProperties.SetAutomationId(btn, "BtnSave");
AutomationProperties.SetName(btn, "Save button");
Grid.SetRow(btn, 1);
ToolTipService.SetToolTip(btn, "Save the current document");
```

### `Converter={x:Null}` crashes `x:Bind` at runtime

`{x:Bind}` requires `Converter` to be a `{StaticResource}` lookup. `Converter={x:Null}` compiles but the generated code calls `LookupConverter("")`, which returns null, then dereferences it — you get `Resource Dictionary Key can only be String-typed` / `NullReferenceException` on first activation of the binding. If you don't want a converter, omit the property entirely.

### Prefer `x:Bind` static functions over `IValueConverter`

```csharp
// MainPage.xaml.cs
public static Visibility BoolToVisibility(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
public static Visibility InvertBoolToVisibility(bool v) => v ? Visibility.Collapsed : Visibility.Visible;
public static bool Not(bool v) => !v;
```
```xml
<TextBlock Visibility="{x:Bind local:MainPage.BoolToVisibility(Vm.IsLoading), Mode=OneWay}" />
<Button   IsEnabled="{x:Bind local:MainPage.Not(Vm.IsLoading), Mode=OneWay}" />
```

### Acrylic and `ThemeShadow` rendering rules

- `BackgroundSizing` defaults to `InnerBorderEdge` on both `Border` and `Control`, which correctly clips acrylic to the inner stroke. The hazard is the opposite of intuition: don't *change* it to `OuterBorderEdge` on a bordered acrylic surface — that's what makes the material bleed past the stroke.
- `ThemeShadow` casts a shadow from the caster's `Translation` Z. Microsoft's recommended elevations are `16` for tooltips, `32` for popup/flyout UI, `128` for dialogs — pick by surface type. For non-popup casters, add the surfaces it should land on to `ThemeShadow.Receivers`; otherwise the shadow has nothing to fall on and looks clipped.

## Theming rules (short version)

- `{ThemeResource ...}` at usage sites (updates on theme switch). `{StaticResource}` inside `ThemeDictionaries` for theme-local definitions; `SystemAccentColor` / `SystemColor*` are the exceptions and stay `{ThemeResource}`.
- Custom theme dictionaries cover `Light`, `Dark`, **and** `HighContrast` explicitly — never `Default`.
- Name resources by purpose (`CardBackgroundBrush`, `DangerTextBrush`), not hue.
- Light/Dark working ≠ High Contrast working. Test in a Contrast theme separately.
- Never set `HighContrastAdjustment="None"` unless your app already supplies system-aware brushes throughout.

## Anti-patterns

| ❌ Don't | ✅ Do instead |
|---------|--------------|
| Reflexively build every app as `NavigationView` Left | Pick the closest row in the silhouette table; hero / document / utility shapes are equally valid |
| Treat brand colour or tinted backdrop as off-pattern | Overriding `SystemAccentColor` or using a tinted `DesktopAcrylicBackdrop` is how Microsoft's own first-party apps differentiate |
| Tiny content island on an oversized window | Either size the window to the content (see *Window sizing*) or let content fill the available space |
| Custom pill / segmented tab switcher built by hand | `NavigationView` Top or `SelectorBar` |
| Equal-width 50/50 column split where one pane is structural | Stable size for the structural pane, flexible for content — only if a structural pane is part of the silhouette at all |
| Hard-coded color literals (`#RRGGBB`, `White`) | `{ThemeResource}` brushes by semantic name |
| `ScrollViewer` wrapped around a `ListView` / `GridView` | The collection control already scrolls — give it a constrained height |
| Custom `ControlTemplate` for a standard control | Built-in control + lightweight style overrides |
| Placeholder text used as the only field label | Always provide a visible label |
| Required commands hidden at small widths with no route | Overflow menu, secondary surface, or a responsive promotion rule |
| Modal `ContentDialog` for non-blocking hints | `TeachingTip`, `InfoBar`, or inline status |
| Destructive action (Delete / Discard / Reset) fired without confirmation | `ContentDialog` with verb-labelled primary action and `Cancel` secondary; surface item identity (name, count) in the body |
| Custom list control when `ListView` / `GridView` fits | Use the platform collection + virtualisation |

Build custom UI **only when all are true**: no platform/Gallery/Toolkit control fits; you'll implement keyboard, focus, UI Automation, theme resources, High Contrast, and responsive behaviour; you have specs for default/hover/pressed/disabled/selected/focused/error states; you've tested with keyboard and a contrast theme.

## References (load on demand)

| File | Load when… |
|------|-----------|
| `references/brushes-and-icons.md` | Looking up a brush key by purpose, picking between `Icon` / `IconSource` slots, choosing among `FontIcon` / `SymbolIcon` / `PathIcon` / etc. |
| `references/theme-accessibility.md` | Authoring theme dictionaries, custom brushes/styles/templates, or High Contrast support. |
| `references/layout-review.md` | Reviewing responsive behaviour, breakpoints, or empty/loading/error coverage on a data-driven page. |
