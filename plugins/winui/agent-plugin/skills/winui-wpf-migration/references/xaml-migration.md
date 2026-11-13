# XAML Migration

Use this reference when porting WPF XAML, resources, bindings, templates, styles, or localization.

## XML Namespace Declarations

Typical namespace change:

```xml
<!-- WPF -->
<Window
    x:Class="MyApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="clr-namespace:MyApp"
    xmlns:models="clr-namespace:MyApp.Models;assembly=MyApp.Core">

<!-- WinUI 3: WindowEx for a top-level window -->
<winuiex:WindowEx
    x:Class="MyApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:MyApp"
    xmlns:models="using:MyApp.Models"
    xmlns:winuiex="using:WinUIEx">
```

Use `using:` for application CLR namespaces. Remove WPF-only library namespaces only after replacing their elements, markup extensions, and styles.

Replace WPF resource declarations that use `xmlns:sys="clr-namespace:System;assembly=mscorlib"` with XAML intrinsic types:

| WPF | WinUI 3 |
|---|---|
| `sys:String` | `x:String` |
| `sys:Int32` | `x:Int32` |
| `sys:Boolean` | `x:Boolean` |
| `sys:Double` | `x:Double` |

## Classify WPF-Only Markup

| WPF construct | WinUI migration |
|---|---|
| `{DynamicResource Key}` | `{ThemeResource Key}` only for theme-reactive resources; `{StaticResource}` for fixed resources; binding or code for other dynamic values. |
| `{x:Static ...}` | `x:Uid` for localized properties, `x:Bind`/function binding for accessible members, or code/resource lookup. |
| `{x:Type ...}` | `x:DataType` on typed templates, or an application-owned type reference in code. |
| `{x:Array}` | Create or expose the collection from code/view model. |
| `<x:Code>` | Not supported; move inline code to the XAML class's code-behind file. |
| `MultiBinding` | `x:Bind` function or computed view-model property. |
| `Style.Triggers` / `DataTrigger` | `VisualStateManager` with `StateTrigger` for data-driven states, or the control's visual states for control-state triggers. |
| WPF `EventTrigger` actions | Visual-state transitions, behaviors that support WinUI, or explicit event/command handling. |
| `Window.Resources` / `Window.DataContext` | Put the view in a `Page`, `UserControl`, or root `FrameworkElement` and place resources/context there. |
| `pack://` URI | Use the asset URI appropriate to the deployment model; packaged content commonly uses `ms-appx:///`. |

## Resource Semantics

### DynamicResource

```xml
<!-- WPF: resolves again when the resource changes -->
<TextBlock Foreground="{DynamicResource StatusBrush}" />

<!-- WinUI: appropriate when StatusBrush varies by theme -->
<TextBlock Foreground="{ThemeResource StatusBrush}" />
```

If `StatusBrush` changes because of application state rather than theme, expose that state through binding or update the property in code. `ThemeResource` is not a general-purpose dynamic-resource system.

### Resource dictionary order

Current WinUI project templates place `XamlControlsResources` first in `Application.Resources` merged dictionaries. Preserve that entry and keep application dictionaries after it so application resources can override platform defaults intentionally:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            <ResourceDictionary Source="ms-appx:///Styles/Colors.xaml" />
            <ResourceDictionary Source="ms-appx:///Styles/Controls.xaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

### Theme dictionaries

Use Light, Dark, and High Contrast theme dictionaries:

```xml
<ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
        <SolidColorBrush x:Key="StatusBrush" Color="#FF1A1A1A" />
    </ResourceDictionary>
    <ResourceDictionary x:Key="Dark">
        <SolidColorBrush x:Key="StatusBrush" Color="#FFF5F5F5" />
    </ResourceDictionary>
    <ResourceDictionary x:Key="HighContrast">
        <SolidColorBrush x:Key="StatusBrush" Color="{ThemeResource SystemColorWindowTextColor}" />
    </ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>
```

## Localization: x:Static to x:Uid

WPF often binds generated `.resx` properties:

```xml
<Button Content="{x:Static properties:Resources.Save}" />
```

Migrate the application's localized `.resx` strings to `.resw`. In XAML, use `x:Uid` and keys that name the target property:

```xml
<Button x:Uid="SaveButton" />
```

```xml
<data name="SaveButton.Content" xml:space="preserve">
  <value>Save</value>
</data>
<data name="SaveButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" xml:space="preserve">
  <value>Save document</value>
</data>
```

Keep `.resx` resources owned by shared libraries outside the application migration. XAML `.resw` keys name target properties, so renaming the file alone is insufficient.

## Typed Templates

WPF `DataTemplate.DataType` has two roles: it describes the data type and, for a template resource without an explicit `x:Key`, supplies an implicit key that lets WPF select the template for matching data objects. WinUI `x:DataType` instead supplies the data-object type used to compile `{x:Bind}`; it is not a template-selection rule.

```xml
<!-- WPF: a resource selected implicitly for Document objects -->
<DataTemplate DataType="{x:Type models:Document}">
    <TextBlock Text="{Binding Title}" />
</DataTemplate>

<!-- WinUI 3: assign the typed template explicitly -->
<ListView ItemsSource="{x:Bind ViewModel.Documents, Mode=OneWay}">
    <ListView.ItemTemplate>
        <DataTemplate x:DataType="models:Document">
            <TextBlock Text="{x:Bind Title, Mode=OneWay}" />
        </DataTemplate>
    </ListView.ItemTemplate>
</ListView>
```

For a reusable WinUI template, give it an `x:Key` and assign it to `ItemTemplate` or `ContentTemplate`. If the WPF view relied on implicit templates for different runtime types, preserve that selection behavior with a `DataTemplateSelector` or another explicit selection mechanism; `x:DataType` alone does not preserve it.

A separate `ResourceDictionary` containing `{x:Bind}` requires `x:Class` and a code-behind constructor that calls `InitializeComponent()`. Merge an instance of that type (for example, `<local:Templates />`) rather than setting `ResourceDictionary.Source`; see [the resource dictionary guidance](https://learn.microsoft.com/windows/apps/develop/data-binding/data-binding-in-depth#resource-dictionaries-with-xbind).

## Binding Strategy

Prefer `{x:Bind}` for new and migrated code for compile-time validation and generated bindings. Use classic `{Binding}` when the view must resolve its source through a runtime `DataContext`, or a reusable template cannot expose a typed source to compiled binding. Both are valid in WinUI 3.

| Feature | `{Binding}` | `{x:Bind}` |
|---|---|---|
| Default source | `DataContext` | The containing XAML root object, such as a `Window`, `Page`, or `UserControl`; inside a `DataTemplate`, the data object being templated; inside a `ControlTemplate`, the templated control identified by `TargetType` |
| Default mode | `OneWay` | `OneTime` |
| Resolution | Runtime | Generated/compiled code |
| Function binding | No | Yes |

An empty `{Binding}` path binds to the whole `DataContext`. Pathless `{x:Bind}` is also valid, but uses its own default source from the table above; the two expressions are not interchangeable.

WPF bindings with an omitted `Mode` can be two-way through the target property's metadata, as with `TextBox.Text` and `ToggleButton.IsChecked`. WinUI `{Binding}` defaults to `OneWay`; set `Mode=TwoWay` wherever edits must update the source.

### Explicit modes and TextBox updates

```xml
<TextBlock Text="{x:Bind ViewModel.Status, Mode=OneWay}" />

<TextBox Text="{x:Bind ViewModel.Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
```

For two-way `TextBox.Text`, the source normally updates on lost focus; specify `UpdateSourceTrigger=PropertyChanged` when validation, commands, or other UI must react to each edit.

### MultiBinding

```xml
<!-- WinUI function binding -->
<TextBlock Text="{x:Bind local:Formatters.FullName(ViewModel.FirstName, ViewModel.LastName), Mode=OneWay}" />
```

Alternatively expose a `FullName` property and raise its change notification when either input changes.

### Boolean to Visibility

Compiled binding can convert `bool` to `Visibility` in supported scenarios:

```xml
<ProgressRing
    IsActive="{x:Bind ViewModel.IsBusy, Mode=OneWay}"
    Visibility="{x:Bind ViewModel.IsBusy, Mode=OneWay}" />
```

### Value Converter Decision Guide

Use the first matching option instead of porting WPF converters one-for-one.

| Conversion need | WinUI migration |
|---|---|
| Selected, pointer-over, pressed, or disabled state inside a `ControlTemplate` | Use the control's visual states and setters; keep the inactive value on the element instead of converting a state property. |
| `bool` → `Visibility` with `x:Bind` property binding | Bind the Boolean directly as above. This compiler conversion does not apply to classic `Binding` or function binding. |
| `bool` → `Visibility` with classic `Binding` | Use a shared `CommunityToolkit.WinUI.Converters.BoolToVisibilityConverter`. |
| Numeric value or collection count → `Visibility` | `DoubleToVisibilityConverter` with `GreaterThan` / `LessThan`; for nonempty collections use `GreaterThan="0"`. |
| Formatted text with classic `Binding` | Use Toolkit's [`StringFormatConverter`](https://learn.microsoft.com/dotnet/api/communitytoolkit.winui.converters.stringformatconverter). |
| Inverse of a Toolkit Boolean or numeric conversion | Reuse the converter with `ConverterParameter=True`. The `BoolToObjectConverter` and `DoubleToObjectConverter` families support this; it is not a convention for every converter. |
| Framework `CornerRadius` → `Rectangle.RadiusX` / `RadiusY` | Check the target SDK's `XamlControlsResources` for `TopLeftCornerRadiusDoubleValueConverter` and `BottomRightCornerRadiusDoubleValueConverter`; preserve available resources instead of reimplementing them. |
| App-specific conversion without an existing equivalent | Implement WinUI `IValueConverter`; both `Convert` and `ConvertBack` take `string language` instead of `CultureInfo culture`. |

For Toolkit converters, reference `CommunityToolkit.WinUI.Converters` using the project's package/version conventions and declare `xmlns:converters="using:CommunityToolkit.WinUI.Converters"` on the view. Visibility converters default to `Visible` / `Collapsed`. One resource can show either the populated or empty state:

```xml
<Page.Resources>
    <converters:DoubleToVisibilityConverter x:Key="CountToVisibility" GreaterThan="0" />
</Page.Resources>
<StackPanel>
    <ItemsControl ItemsSource="{Binding Items}"
                  Visibility="{Binding Items.Count, Converter={StaticResource CountToVisibility}}" />
    <TextBlock Text="No items"
               Visibility="{Binding Items.Count, Converter={StaticResource CountToVisibility}, ConverterParameter=True}" />
</StackPanel>
```

### Relative sources and style setters

`RelativeSource Self` and `RelativeSource TemplatedParent` are supported in WinUI; use `TemplatedParent` within a control template. WPF `FindAncestor` is not supported. Use `ElementName`, typed `x:Bind`, or expose the required value to the template.

When the project uses `CommunityToolkit.WinUI.Extensions`, its `FrameworkElementExtensions.AncestorType` / `Ancestor` attached properties provide another option. With `xmlns:extensions="using:CommunityToolkit.WinUI"` on the view:

```xml
<StackPanel Spacing="12">
    <TextBlock extensions:FrameworkElementExtensions.AncestorType="StackPanel"
               Text="{Binding (extensions:FrameworkElementExtensions.Ancestor).Spacing, RelativeSource={RelativeSource Self}}" />
</StackPanel>
```

Current WinUI styles do not support the WPF pattern of putting a `Binding` directly in `Setter.Value`. Move the binding to the target element/control template, use a visual state, or expose a computed property instead of copying the setter.

## Triggers to Visual States

Use visual states when the behavior belongs to the visual surface:

```xml
<Border x:Name="StatusBorder">
    <VisualStateManager.VisualStateGroups>
        <VisualStateGroup x:Name="ActivityStates">
            <VisualState x:Name="Inactive" />
            <VisualState x:Name="Active">
                <VisualState.StateTriggers>
                    <StateTrigger IsActive="{x:Bind ViewModel.IsActive, Mode=OneWay}" />
                </VisualState.StateTriggers>
                <VisualState.Setters>
                    <Setter Target="StatusBorder.Background" Value="{ThemeResource AccentFillColorDefaultBrush}" />
                </VisualState.Setters>
            </VisualState>
        </VisualStateGroup>
    </VisualStateManager.VisualStateGroups>
</Border>
```

For a standard control's pointer-over, pressed, disabled, selected, or focused behavior, use its existing visual states.

## Styles and Templates

- When extending a platform control style, use `BasedOn` with its default style key; verify the actual key in the target SDK.
- Recheck visual-state names; WPF `MouseOver` commonly becomes WinUI `PointerOver`.

## Window Content

For top-level windows, prefer `WinUIEx.WindowEx` or an existing base derived from it, with a `Page` or `UserControl` as the content. Declare supported window settings in XAML; use raw `AppWindow`/presenter code only for behavior the window base does not expose. WinUI `Window`, including `WindowEx`, is not a `FrameworkElement`.

```xml
<winuiex:WindowEx
    x:Class="MyApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:views="using:MyApp.Views"
    xmlns:winuiex="using:WinUIEx"
    MinWidth="480"
    MinHeight="320"
    IsResizable="True">
    <views:MainPage />
</winuiex:WindowEx>
```

Match the code-behind base class to `WindowEx`. Place `DataContext`, local resources, and visual states on the hosted view or its root element; see [Threading and Windowing](threading-and-windowing.md).

## WPF Property and Event Differences

| WPF | WinUI migration |
|---|---|
| `Visibility.Hidden` | `Collapsed` if space should disappear; `Opacity="0"` plus intentional hit-testing/layout behavior if space must remain. |
| `Focusable` | Usually `IsTabStop`; also verify programmatic focus and automation semantics. |
| `ContextMenu` | `ContextFlyout`. |
| `SnapsToDevicePixels` | Remove the WPF property; review WinUI layout rounding and the resulting pixel alignment. |
| `TextBox.VerticalScrollBarVisibility` | Use the attached property, for example `<TextBox ScrollViewer.VerticalScrollBarVisibility="Auto" />`. |
| `TextWrapping.WrapWithOverflow` | Use WinUI `TextWrapping.WrapWholeWords` on a control that supports it, such as `TextBlock`; it preserves overflow for an unbreakable word. `TextBox` and `RichEditBox` reject `WrapWholeWords`, so use `Wrap` only after accepting and testing the changed long-word behavior, or redesign width/scrolling behavior. |
| `IsDefault` / `IsCancel` | `AccentButtonStyle` supplies primary-action styling only; handle Enter/Escape/keyboard accelerators where the containing surface does not provide them. |
| `PreviewMouse*` | Redesign with pointer events; there is no blanket name substitution. |
| `PreviewKeyDown` / `PreviewKeyUp` | Available on WinUI `UIElement`; keep when behavior still fits. |
| `Mouse*` handlers | Use pointer/gesture events and update event argument types; see the [input event map](namespace-api-mapping.md#input-and-routed-events) for signatures, right-button release, and handled events. |

If `Opacity="0"` replaces `Visibility.Hidden`, decide whether the element should remain hit-testable and accessible. Hiding pixels while retaining interaction is often a bug.

## Geometry and Clip Differences

WPF `CombinedGeometry` and complex `UIElement.Clip` expressions do not map mechanically. For rendered shapes, a `Path` with a `GeometryGroup` and `FillRule="EvenOdd"` can create hole effects: regions covered an even number of times are left unfilled. For example, the overlap of two rectangles is unfilled under `EvenOdd`, while a `Union` must keep it filled. Preserve the intended `Union`, `Intersect`, `Exclude`, or `Xor` operation with a geometry API that supports that boolean operation when needed. WinUI `UIElement.Clip` is more restricted (commonly `RectangleGeometry`), so complex clipping may require a `Path` overlay, mask/composition clip, or redesigned visual. Verify the exact target property/API instead of applying one workaround to every geometry.

## WPF-UI and Other WPF Control Libraries

Map each library control's behavior before removing its namespace and package. Common WPF-UI replacements:

| WPF-UI item | WinUI migration |
|---|---|
| `ui:FluentWindow` / `ui:TitleBar` | `WinUIEx.WindowEx` with the target SDK's title-bar APIs; prefer its XAML properties and preserve custom drag regions and caption behavior. See [Threading and Windowing](threading-and-windowing.md). |
| `ui:ThemesDictionary` / `ui:ControlsDictionary` | Replace the WPF dictionaries with `XamlControlsResources` and application theme resources, as shown above. |
| `ui:NumberBox` | Native `NumberBox`; set `Mode=TwoWay` when user edits must update the source. |
| `ui:InfoBar` / `ui:SymbolIcon` / `ui:Button` | Native controls with the same names; recheck enum values and custom styles. |
| `ui:ProgressRing` | Native `ProgressRing`; `IsActive` controls activity, while `IsIndeterminate` selects indeterminate progress. Preserve both behaviors rather than treating these properties as synonyms. |

For missing layout/header controls, use the [Toolkit package map](namespace-api-mapping.md#toolkit-controls-and-package-names).

## XAML Formatting

Apply the repository's XAML formatter after migration.

## Official References

- [XAML resource dictionaries](https://learn.microsoft.com/windows/apps/develop/platform/xaml/xaml-resource-dictionary)
- [Data binding in depth](https://learn.microsoft.com/windows/apps/develop/data-binding/data-binding-in-depth)
- [`{x:Bind}` markup extension](https://learn.microsoft.com/windows/apps/develop/platform/xaml/x-bind-markup-extension)
- [WPF binding declarations overview](https://learn.microsoft.com/dotnet/desktop/wpf/data/binding-declarations-overview)
- [WPF data templating overview](https://learn.microsoft.com/dotnet/desktop/wpf/data/data-templating-overview)
- [WinUI `DataTemplate`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.datatemplate)
- [WinUI data template selector](https://learn.microsoft.com/windows/apps/develop/ui/controls/data-template-selector)
- [WinUI `TextWrapping`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.textwrapping)
- [Toolkit FrameworkElement extensions and ancestor binding](https://learn.microsoft.com/dotnet/communitytoolkit/windows/extensions/frameworkelementextensions)
