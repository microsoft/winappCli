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
| `Window` | Prefer `WinUIEx.WindowEx` or an existing base derived from it | Declare supported window settings in XAML. The underlying WinUI `Window` is not a `DependencyObject`/`FrameworkElement`; use `AppWindow` for behavior the base does not expose. |
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

Examples include `Button`, `TextBox`, `TextBlock`, `PasswordBox`, `ComboBox`, `CheckBox`, `RadioButton`, `ListBox`, `ListView`, `Image`, `StackPanel`, `Grid`, `Border`, `ScrollViewer`, `ContentControl`, `UserControl`, `Page`, `Frame`, `Slider`, `ProgressBar`, `ToolTip`, `ToggleButton`, and `Expander`.

`ListBox` is available in WinUI 3; it is not deprecated. Prefer `ListView`, or `ItemsView` for suitable collection scenarios, when choosing a replacement.

## Controls With Different Names or Behavior

| WPF surface | WinUI replacement | Notes |
|---|---|---|
| `MessageBox` | `ContentDialog` | Set `XamlRoot` before `ShowAsync()`. |
| `ContextMenu` | `MenuFlyout` | Assign through `ContextFlyout`. |
| `Menu` / `MenuItem` | `MenuBar`, `MenuBarItem`, `MenuFlyout` | Persistent application menu versus contextual actions. |
| `ToolBar` / `ToolBarTray` | `CommandBar` + `AppBarButton` | No `ToolBarTray` counterpart. |
| `StatusBar` | Custom `Grid`/`StackPanel` or `InfoBar` | No built-in `StatusBar`. |
| `TabControl` | `TabView` or `NavigationView` in top mode | `TabView` supports closeable tabs. |
| `DataGrid` | Prefer [WinUI.TableView](https://github.com/w-ahmad/WinUI.TableView) for new replacement code | No first-party `DataGrid`; the old Toolkit grid is archived. |
| `ListView.View` with `GridView` / `GridViewColumn` | A column-based control such as `WinUI.TableView` | WPF `GridView` is a `ListView` view mode for columns. WinUI `GridView` is a separate control for tiled items, not that view mode; remap column bindings, selection, and sorting behavior. |
| `TreeView` | Native `TreeView` | Its hierarchy and data-binding model differ from WPF; port hierarchical templates and selection/expansion bindings explicitly. |
| `WrapPanel` | Toolkit `WrapPanel` | See the package map below. |
| `UniformGrid` | Toolkit `UniformGrid` | Lays out child elements in uniform rows/columns. |
| `DockPanel` | Toolkit `DockPanel` | See the package map below. |
| `GridSplitter` | Toolkit `GridSplitter` | See the package map below. |
| `GroupBox` | `Expander` or Toolkit `HeaderedContentControl` | Use `HeaderedContentControl` when the group should not collapse. |
| `Ribbon` | `CommandBar`, `NavigationView`, or [Toolkit Labs Ribbon](https://github.com/CommunityToolkit/Labs-Windows/tree/main/components/Ribbon) | No first-party Ribbon; the Labs component is experimental and partial. |
| `RichTextBox` | `RichEditBox` | Rich-text editing with a different API. |
| `FlowDocument` | `RichTextBlock` | Partial replacement; no WPF `FlowDocument` model. |
| `DocumentViewer` | `WebView2` for supported HTML/PDF content | XPS requires a separate renderer; WebView2 does not render it directly. |
| `WebBrowser` | `WebView2` | Different initialization API. |
| `MediaElement` | `MediaPlayerElement` | Different playback API. |
| `Label` / `AccessText` | `TextBlock` plus `AccessKey` on the target control | `TextBlock` is not a WPF `Label` content control. |
| `Adorner` / `AdornerLayer` | Use the purpose-specific mapping below | Validation, resize handles, drag preview, overlay, or watermark. |

### Adorners by purpose

| WPF adorner use | WinUI migration |
|---|---|
| Validation indicator | `TeachingTip`, `InfoBar`, or validation visuals in the input control's template. |
| Resize handles | A `Popup` positioned relative to the target. |
| Drag preview | `DragStarting` exposes `DragUI` for the visual; `ListView`/`GridView` also expose `DragItemsStarting` for the item data payload. |
| Overlay decoration | A `Canvas` overlay or `Popup` layer positioned relative to the target. |
| Watermark / placeholder | `TextBox.PlaceholderText` when the requirement is an empty-field hint. |

## Input and Routed Events

| WPF event/pattern | WinUI approach | Notes |
|---|---|---|
| `MouseLeftButtonDown` / `MouseLeftButtonUp` | `PointerPressed` / `PointerReleased` | Inspect `GetCurrentPoint(...).Properties` for the button state. |
| `MouseMove` | `PointerMoved` | Pointer devices include mouse, pen, and touch. |
| `MouseWheel` | `PointerWheelChanged` | Read wheel delta from pointer point properties. |
| `MouseEnter` / `MouseLeave` | `PointerEntered` / `PointerExited` | Recheck capture and child-element behavior. |
| `MouseDoubleClick` | `DoubleTapped` or click-count handling | Gesture semantics may differ. |
| `MouseRightButtonDown` | `RightTapped` or `PointerPressed` with right-button inspection | Choose gesture versus raw pointer semantics. |
| `MouseRightButtonUp` | Inspect `PointerReleased` and, for other button transitions, `PointerMoved` | Identify `PointerUpdateKind.RightButtonReleased`; `IsRightButtonPressed` is false after release. Use `RightTapped` for a context-menu gesture, not a raw button-up handler. |
| WPF `PreviewMouse*` | No mechanical mapping | Redesign with pointer events, capture, handled-events-too registration, or parent/child ownership. |
| `PreviewKeyDown` / `PreviewKeyUp` | Same event names on WinUI `UIElement` | These tunneling key events exist; do not delete them based on a blanket “no preview events” rule. |
| `KeyDown` / `KeyUp` | Same names with `KeyRoutedEventArgs` | Recheck accelerator, focus, and handled behavior. |
| `MouseEventArgs` | `Microsoft.UI.Xaml.Input.PointerRoutedEventArgs` | Update the handler signature as well as the event name. |

While a mouse button owns the pointer, other mouse-button transitions arrive through `PointerMoved`. To preserve an individual right-button release, inspect `e.GetCurrentPoint(element).Properties.PointerUpdateKind` in both events for `Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased`.

When existing behavior must observe a routed event that a control has already marked handled, use `AddHandler`:

```csharp
myElement.AddHandler(UIElement.PointerPressedEvent,
    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPointerPressed), handledEventsToo: true);
```

`OnPointerPressed` takes `(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)`. This observes handled events in their normal route; it does not recreate WPF mouse tunneling.

## Commands and Binding APIs

| WPF | WinUI option |
|---|---|
| `RoutedUICommand` / `CommandBinding` | Bind a C# `System.Windows.Input.ICommand` implemented with CommunityToolkit.Mvvm's `RelayCommand` / `[RelayCommand]`; use `StandardUICommand`/`XamlUICommand` where platform semantics fit. |
| `ApplicationCommands.*` | Platform command, keyboard accelerator, or application-owned command. |
| `IValueConverter` with `CultureInfo` | WinUI converter with `string language`; use the [converter decision guide](xaml-migration.md#value-converter-decision-guide) for visual states, built-in conversions, and Toolkit choices. |
| `MultiBinding` | `x:Bind` function binding or a computed view-model property. |

## Platform API Replacements

| WPF API | WinUI / Windows approach |
|---|---|
| `Dispatcher.Invoke` / `BeginInvoke` | `DispatcherQueue`, with explicit handling for queued execution and enqueue failure. |
| `Dispatcher.CheckAccess()` | `DispatcherQueue.HasThreadAccess`. |
| `Application.Current.Dispatcher` | Capture the dispatcher queue from the owning UI object/thread and pass it to code that needs it. |
| `Window.Current` / `Application.Current.MainWindow` | Track application windows explicitly. |
| `WindowInteropHelper` / `HwndSource` | `WinRT.Interop.WindowNative`, `WindowId`, `AppWindow`, or a specific HWND interop API. |
| `System.Windows.Clipboard` | `Windows.ApplicationModel.DataTransfer.Clipboard`. |
| WPF file dialogs | Current Windows App SDK picker APIs or initialized legacy WinRT/Win32 pickers; see [Threading and Windowing](threading-and-windowing.md). |
| `SystemParameters` / `SystemColors` | Theme/system resources, `DisplayInformation`, or Win32 APIs depending on the value. |
| Pack URI | Package/application URI appropriate to the asset and deployment model; commonly `ms-appx:///` for packaged content. |

## Package Migration

| WPF package or feature | Candidate |
|---|---|
| `Microsoft.Xaml.Behaviors.Wpf` | `Microsoft.Xaml.Behaviors.WinUI.Managed`. |
| Additional XAML interactions | `CommunityToolkit.WinUI.Behaviors`, with `using:CommunityToolkit.WinUI.Behaviors`. |
| WPF-UI/Lepo controls | Native WinUI controls; see [XAML Migration](xaml-migration.md#wpf-ui-and-other-wpf-control-libraries). |
| Custom `Observable` / `RelayCommand` base | Replace with CommunityToolkit.Mvvm's `ObservableObject`, `[ObservableProperty]`, and `[RelayCommand]`; preserve behavior as described in [Project and Application Migration](project-and-application-migration.md#mvvm-migration). |
| `CommunityToolkit.Mvvm` | Keep the same package. |
| Top-level windows | `WinUIEx`; prefer `WindowEx` and its XAML properties. See [Threading and Windowing](threading-and-windowing.md). |
| `Microsoft.Toolkit.Wpf.*` | `CommunityToolkit.WinUI.*` packages listed below. |

### Toolkit controls and package names

These optional `Controls.*` packages share a XAML namespace that differs from their package IDs:

| WPF / UI requirement | Toolkit control | NuGet package | XAML namespace |
|---|---|---|---|
| `WrapPanel` | `WrapPanel` | `CommunityToolkit.WinUI.Controls.Primitives` | `using:CommunityToolkit.WinUI.Controls` |
| `UniformGrid` | `UniformGrid` | `CommunityToolkit.WinUI.Controls.Primitives` | `using:CommunityToolkit.WinUI.Controls` |
| `DockPanel` | `DockPanel` | `CommunityToolkit.WinUI.Controls.Primitives` | `using:CommunityToolkit.WinUI.Controls` |
| `GroupBox` / headered content | `HeaderedContentControl`, `HeaderedItemsControl`, `HeaderedTreeView` | `CommunityToolkit.WinUI.Controls.HeaderedControls` | `using:CommunityToolkit.WinUI.Controls` |
| Settings rows / sections | `SettingsCard`, `SettingsExpander` | `CommunityToolkit.WinUI.Controls.SettingsControls` | `using:CommunityToolkit.WinUI.Controls` |
| Resizable panes / content | `GridSplitter`, `PropertySizer`, `ContentSizer` | `CommunityToolkit.WinUI.Controls.Sizers` | `using:CommunityToolkit.WinUI.Controls` |

`Primitives` also supplies `ConstrainedBox`, `SwitchPresenter`, [`WrapLayout`](https://learn.microsoft.com/dotnet/api/communitytoolkit.winui.controls.wraplayout) for virtualized wrapping, and [`StaggeredPanel`](https://learn.microsoft.com/dotnet/api/communitytoolkit.winui.controls.staggeredpanel) for columns of uneven-height items. They use the same `CommunityToolkit.WinUI.Controls` namespace. For converters and ancestor binding, see [XAML Migration](xaml-migration.md#value-converter-decision-guide): `CommunityToolkit.WinUI.Converters` uses `using:CommunityToolkit.WinUI.Converters`, while `CommunityToolkit.WinUI.Extensions` exposes `FrameworkElementExtensions` through `using:CommunityToolkit.WinUI`.

The legacy `CommunityToolkit.WinUI.UI.Controls.DataGrid` package is an archived option for existing grid code, not the current `Controls.*` family. Use package versions compatible with the target Windows App SDK.

## Native WinRT Component References

When the app consumes an existing native WinRT component, check its `.winmd` input, C# projection, project/package reference, and deployed native binary for each target architecture. Preserve the existing projection strategy unless it must change for the target SDK.

If the project filters app-owned metadata with CsWinRT, use the component's actual namespace. For a component in `ExampleComponent`:

```xml
<PropertyGroup>
  <CsWinRTIncludes>ExampleComponent</CsWinRTIncludes>
</PropertyGroup>
```

Do not regenerate Windows or WinUI types already supplied by the SDK projections. Check any existing `CsWinRTGeneratedFilesDir` override and generated compile items so moved files are neither omitted nor compiled twice. Keep the SDK's default generated-output location when no override is needed. Replace a separate projection-project reference only after verifying that the chosen project/package setup supplies the same managed types and native runtime files.

See the [CsWinRT usage guide](https://github.com/microsoft/CsWinRT/blob/master/docs/usage.md) for the selected tool version's metadata and projection options.

## Official API References

- [ListBox class](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.listbox)
- [ItemsView](https://learn.microsoft.com/windows/apps/develop/ui/controls/itemsview)
- [Archived Windows Community Toolkit DataGrid](https://learn.microsoft.com/dotnet/communitytoolkit/archive/windows/datagrid)
- [Commanding in Windows apps](https://learn.microsoft.com/windows/apps/develop/ui/controls/commanding)
- [`System.Windows.Input.ICommand`](https://learn.microsoft.com/dotnet/api/system.windows.input.icommand)
- [WPF `GridView`](https://learn.microsoft.com/dotnet/api/system.windows.controls.gridview) and [WinUI `GridView`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.gridview)
- [`PointerReleased` and mouse-button routing](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.pointerreleased), [`PointerUpdateKind`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerupdatekind), and [`AddHandler`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.addhandler)
- [Toolkit behaviors](https://learn.microsoft.com/dotnet/communitytoolkit/windows/behaviors/) and [`KeyDownTriggerBehavior` package/namespace](https://learn.microsoft.com/dotnet/api/communitytoolkit.winui.behaviors.keydowntriggerbehavior)
