# Namespace and API Mapping

Use this reference to classify a WPF dependency before changing it. A matching type name does not guarantee matching behavior.

## Namespace Map

| WPF | WinUI 3 / Windows App SDK | Migration note |
|---|---|---|
| `System.Windows` | `Microsoft.UI.Xaml` | Core XAML types; `Window` is substantially different. |
| `System.Windows.Controls` | `Microsoft.UI.Xaml.Controls` | Many controls retain names, but properties/templates may differ. |
| `System.Windows.Controls.Primitives` | `Microsoft.UI.Xaml.Controls.Primitives` | Verify event and template contracts. |
| `System.Windows.Data` | `Microsoft.UI.Xaml.Data` | Binding and converter APIs differ in details. |
| `System.Windows.Documents` | `Microsoft.UI.Xaml.Documents` | Smaller document model; no `FlowDocument` equivalent. |
| `System.Windows.Input` | Migrate per type; many routed-input types move to `Microsoft.UI.Xaml.Input`, but keep `System.Windows.Input.ICommand` in C# | WinUI's .NET command properties use `System.Windows.Input.ICommand`. WPF routed commands and command bindings require explicit replacements; do not bulk-replace this namespace. |
| `System.Windows.Markup` | `Microsoft.UI.Xaml.Markup` | WPF markup extensions are not all supported. |
| `System.Windows.Media` | `Microsoft.UI.Xaml.Media` | Brushes, transforms, geometries, and effects need per-type review. |
| `System.Windows.Media.Animation` | `Microsoft.UI.Xaml.Media.Animation` | Verify supported animation types and target properties. |
| `System.Windows.Media.Imaging` | `Microsoft.UI.Xaml.Media.Imaging` for XAML image sources; `Windows.Graphics.Imaging` or an existing image library for processing | Do not use the XAML `BitmapImage` type as an image-processing replacement. |
| `System.Windows.Media.Media3D` | No direct WinUI XAML equivalent | Consider Composition, Win2D, DirectX, or an existing renderer. |
| `System.Windows.Navigation` | `Microsoft.UI.Xaml.Navigation` | `Frame` navigation exists; WPF `NavigationService` patterns do not map directly. |
| `System.Windows.Shapes` | `Microsoft.UI.Xaml.Shapes` | Basic shapes map; complex geometry/clip behavior may differ. |
| `System.Windows.Threading` | `Microsoft.UI.Dispatching` | `DispatcherQueue` is queued/asynchronous, not `Dispatcher.Invoke`. |
| `System.Windows.Interop` | `WinRT.Interop`, the `Microsoft.UI.Win32Interop` type, `Microsoft.UI.Xaml.Hosting`, and platform-specific interop APIs | Choose by HWND, `WindowId`, XAML Islands, or native API need. `Win32Interop` is a type in the `Microsoft.UI` namespace, not a namespace itself. |
| WPF automation peers/providers | `Microsoft.UI.Xaml.Automation.Peers` / `.Provider` and `AutomationProperties` | Preserve automation names, IDs, peers, patterns, and keyboard access. |
| UI Automation client code such as `System.Windows.Automation.AutomationElement` | Continue using/evaluate the Windows UI Automation client or COM API | This is not replaced by WinUI attached properties or peers. |

## Core Type Map

| WPF type | WinUI 3 type or approach | Important difference |
|---|---|---|
| `Application` | `Microsoft.UI.Xaml.Application` | Activation and shutdown lifecycle differ. |
| `Window` | `Microsoft.UI.Xaml.Window` + `AppWindow` | WinUI `Window` is not a `DependencyObject`/`FrameworkElement`. |
| `DependencyObject` / `DependencyProperty` | Same names under `Microsoft.UI.Xaml` | Recheck metadata callbacks and owner types. |
| `FrameworkElement` / `UIElement` | Same names under `Microsoft.UI.Xaml` | Event set and layout behavior are not identical. |
| `Thickness`, `CornerRadius`, `Visibility` | Same names under `Microsoft.UI.Xaml` | `Visibility.Hidden` has no WinUI equivalent. |
| `System.Windows.Media.Color` | `Windows.UI.Color` | `Microsoft.UI.Colors` provides named colors. |
| `Point`, `Rect`, `Size` | `Windows.Foundation.Point`, `Rect`, `Size` | Check logical units versus physical pixels at Win32/AppWindow boundaries. |
| `Brush`, `SolidColorBrush`, transforms | `Microsoft.UI.Xaml.Media` equivalents | Effects and freezing/cloning patterns differ; there is no WPF `Freezable` model. |
| `BitmapSource` | `SoftwareBitmap` for pixel data or an app-specific abstraction | See [Imaging Migration](imaging-migration.md). |
| `IValueConverter` | `Microsoft.UI.Xaml.Data.IValueConverter` | The final parameter is a BCP-47 language `string`, not `CultureInfo`. |
| `System.Windows.Input.ICommand` | Keep `System.Windows.Input.ICommand` in C# WinUI code | `Microsoft.UI.Xaml.Input.ICommand` is used from C++; its C#/.NET projection is `System.Windows.Input.ICommand`. |

## Controls That Usually Keep Their Name

Examples include `Button`, `TextBox`, `TextBlock`, `PasswordBox`, `ComboBox`, `CheckBox`, `RadioButton`, `ListBox`, `ListView`, `GridView`, `Image`, `StackPanel`, `Grid`, `Border`, `ScrollViewer`, `ContentControl`, `UserControl`, `Page`, `Frame`, `Slider`, `ProgressBar`, `ToolTip`, `ToggleButton`, `TreeView`, and `Expander`.

Still compare:

- Missing or renamed dependency properties.
- Default styles and visual-state names.
- Selection and virtualization behavior.
- Routed events and event argument types.
- Keyboard/focus and UI Automation behavior.
- Binding defaults and template restrictions.

`ListBox` is available in current WinUI 3; it is not categorically deprecated. Choose `ListBox`, `ListView`, `GridView`, `ItemsView`, or `ItemsRepeater` from the required interaction and layout rather than a blanket rule.

## Controls Requiring a Design Decision

Search current samples with `winui-design` before choosing a dependency or writing a custom control.

| WPF surface | Candidate WinUI approach | Decide from |
|---|---|---|
| `MessageBox` | `ContentDialog`, in-page confirmation, `AppNotification`, or a Win32 dialog | Owner, modality, process/window scope, accessibility. |
| `ContextMenu` | `MenuFlyout` assigned through `ContextFlyout` | Command routing and target context. |
| `Menu` / `MenuItem` | `MenuBar`, `MenuBarItem`, `MenuFlyout` | Persistent application menu versus contextual actions. |
| `ToolBar` / `ToolBarTray` | `CommandBar` and app bar buttons | Overflow, labels, keyboard accelerators, responsive layout. |
| `StatusBar` | App-specific `Grid`/`StackPanel`, `InfoBar`, or inline status | Persistent status versus actionable notification. |
| `TabControl` | `TabView`, top `NavigationView`, or a selector plus content | Close/reorder semantics, document tabs, navigation history. |
| `DataGrid` | Simple `ListView`/`ItemsRepeater`/`ItemsView` composition, or a maintained third-party grid | Editing, sorting, grouping, virtualization, clipboard, keyboard, automation, licensing. WinUI 3 has no first-party `DataGrid`; do not hard-code one community project as the universal replacement. |
| `WrapPanel` | `ItemsRepeater` with a suitable layout, `GridView`, or a maintained Toolkit/community panel | Variable item size, selection, virtualization, wrapping rules. |
| `UniformGrid` | `UniformGridLayout` or a maintained Toolkit/community control | Items versus arbitrary child elements, virtualization, fixed rows/columns. |
| `DockPanel` | `Grid` or a maintained Toolkit/community panel | Whether dock behavior is truly needed or a fixed grid is clearer. |
| `GridSplitter` | A maintained Toolkit/community splitter or app-specific pointer/keyboard behavior | Accessibility, min/max sizing, pane persistence. |
| `GroupBox` | `Expander`, header + border, or a headered community control | Collapsible behavior, semantics, keyboard navigation. |
| `Ribbon` | `CommandBar`, `NavigationView`, or an evaluated third-party/Labs control | Feature completeness and accessibility. |
| `RichTextBox` | `RichEditBox` | Document format, clipboard, spellcheck, printing. |
| `FlowDocument` / `DocumentViewer` | `RichTextBlock`, `WebView2` for supported web/HTML/PDF content, a dedicated PDF/XPS/document component, or architectural redesign | Pagination, printing, annotations, and the actual document format. Do not assume WebView2 directly replaces XPS/document semantics. |
| `WebBrowser` | `WebView2` | Initialization, profile/data folder, navigation security. |
| `MediaElement` | `MediaPlayerElement` | Playback lifecycle and media transport controls. |
| `Label` / `AccessText` | `TextBlock` or control header/content plus `AccessKey` | Label association, mnemonics, automation name. |
| `Adorner` / `AdornerLayer` | `Popup`, `TeachingTip`, `InfoBar`, validation template, drag UI, or overlay layer | The original adorner's purpose. |

Archived or legacy controls can be valid for compatibility, but confirm maintenance status, supported Windows App SDK versions, AOT/trimming behavior, and license before adding them.

For a WPF `DataGrid`, inventory these behaviors before selecting or implementing a replacement:

- Cell/row edit start, commit, cancel, and validation timing.
- Explicit versus auto-generated columns, templates, formatting, and column persistence.
- Sorting, grouping, filtering, selection units, current cell, and row details.
- Clipboard copy/paste, delete, undo, keyboard navigation, focus, and accessibility patterns.
- Virtualization, incremental loading, large-data performance, and horizontal scrolling.
- Frozen columns, resizing/reordering, headers, context menus, and automation used by tests.

A simple read-only table and a spreadsheet-like editor are different migration problems; do not choose the same replacement by control name alone.

## Input and Routed Events

| WPF event/pattern | WinUI approach | Notes |
|---|---|---|
| `MouseLeftButtonDown` / `MouseLeftButtonUp` | `PointerPressed` / `PointerReleased` | Inspect `GetCurrentPoint(...).Properties` for the button state. |
| `MouseMove` | `PointerMoved` | Pointer devices include mouse, pen, and touch. |
| `MouseWheel` | `PointerWheelChanged` | Read wheel delta from pointer point properties. |
| `MouseEnter` / `MouseLeave` | `PointerEntered` / `PointerExited` | Recheck capture and child-element behavior. |
| `MouseDoubleClick` | `DoubleTapped` or click-count handling | Gesture semantics may differ. |
| `MouseRightButtonDown` | `RightTapped` or `PointerPressed` with right-button inspection | Choose gesture versus raw pointer semantics. |
| WPF `PreviewMouse*` | No mechanical mapping | Redesign with pointer events, capture, handled-events-too registration, or parent/child ownership. |
| `PreviewKeyDown` / `PreviewKeyUp` | Same event names on WinUI `UIElement` | These tunneling key events exist; do not delete them based on a blanket “no preview events” rule. |
| `KeyDown` / `KeyUp` | Same names with `KeyRoutedEventArgs` | Recheck accelerator, focus, and handled behavior. |

Do not bulk rename handler methods without reviewing their event arguments and behavioral assumptions.

## Commands and Binding APIs

| WPF | WinUI option |
|---|---|
| `RoutedUICommand` / `CommandBinding` | Bind a C# `System.Windows.Input.ICommand`, use an existing MVVM command, or use `StandardUICommand`/`XamlUICommand` where platform semantics fit. |
| `ApplicationCommands.*` | Platform command, keyboard accelerator, or application-owned command. |
| `IValueConverter` with `CultureInfo` | WinUI converter with `string language`; first check whether `x:Bind`, a visual state, or an existing converter removes the need. |
| `MultiBinding` | `x:Bind` function binding, a computed view-model property, or an application-owned aggregate object. |

## Platform API Replacements

| WPF API | WinUI / Windows approach |
|---|---|
| `Dispatcher.Invoke` / `BeginInvoke` | `DispatcherQueue`, with explicit handling for queued execution and enqueue failure. |
| `Dispatcher.CheckAccess()` | `DispatcherQueue.HasThreadAccess`. |
| `Application.Current.Dispatcher` | Capture the dispatcher queue from the owning UI object/thread and pass it to code that needs it. |
| `Window.Current` / `Application.Current.MainWindow` | Track application windows explicitly. |
| `WindowInteropHelper` / `HwndSource` | `WinRT.Interop.WindowNative`, `WindowId`, `AppWindow`, or a specific HWND interop API. |
| `System.Windows.Clipboard` | `Windows.ApplicationModel.DataTransfer.Clipboard` or another API chosen for the deployment context. |
| WPF file dialogs | Current Windows App SDK picker APIs or initialized legacy WinRT/Win32 pickers; see [Threading and Windowing](threading-and-windowing.md). |
| `SystemParameters` / `SystemColors` | Theme/system resources, accessibility settings, `DisplayInformation`, or Win32 APIs depending on the value. |
| Pack URI | Package/application URI appropriate to the asset and deployment model; commonly `ms-appx:///` for packaged content. |

## Optional Package Migration

Only add packages required by the selected design:

| WPF package or feature | Candidate |
|---|---|
| `Microsoft.Xaml.Behaviors.Wpf` | `Microsoft.Xaml.Behaviors.WinUI.Managed`, if behaviors remain the chosen approach. |
| WPF-UI/Lepo controls | Native WinUI controls, current Toolkit controls, or app styles selected per behavior. |
| Custom observable/command base | Existing MVVM framework or CommunityToolkit.Mvvm if an MVVM migration is explicitly in scope. |
| WPF Toolkit controls | Current Windows Community Toolkit packages or another maintained component after verification. |

Do not specify a stale package version or assume the NuGet package ID matches its XAML namespace. Confirm the current sample and package metadata before editing the project.

## Official API References

- [ListBox class](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.listbox)
- [ItemsView](https://learn.microsoft.com/windows/apps/develop/ui/controls/itemsview)
- [Archived Windows Community Toolkit DataGrid](https://learn.microsoft.com/dotnet/communitytoolkit/archive/windows/datagrid)
- [Commanding in Windows apps](https://learn.microsoft.com/windows/apps/develop/ui/controls/commanding)
- [`System.Windows.Input.ICommand`](https://learn.microsoft.com/dotnet/api/system.windows.input.icommand)
