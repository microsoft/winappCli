# XAML Migration

Use this reference when porting WPF XAML, resources, bindings, templates, styles, or localization. Preserve behavior and accessibility; do not translate syntax without understanding what the original construct did.

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

<!-- WinUI 3 -->
<Window
    x:Class="MyApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:MyApp"
    xmlns:models="using:MyApp.Models">
```

Use `using:` for application CLR namespaces. Remove WPF-only library namespaces only after replacing their elements, markup extensions, and styles.

## Classify WPF-Only Markup

| WPF construct | WinUI migration |
|---|---|
| `{DynamicResource Key}` | `{ThemeResource Key}` only for theme-reactive resources; `{StaticResource}` for fixed resources; binding or code for other dynamic values. |
| `{x:Static ...}` | `x:Uid` for localized properties, `x:Bind`/function binding for accessible members, or code/resource lookup. |
| `{x:Type ...}` | `x:DataType` on typed templates, or an application-owned type reference in code. |
| `{x:Array}` / object-data providers | Create or expose the collection from code/view model. |
| `MultiBinding` | `x:Bind` function, computed view-model property, or aggregate value object. |
| `Style.Triggers` / `DataTrigger` | Visual states, direct binding, a computed property, a supported WinUI data-trigger behavior when the project already uses behaviors, or a custom control whose states own the behavior. |
| WPF `EventTrigger` actions | Visual-state transitions, behaviors that support WinUI, or explicit event/command handling. |
| `Window.Resources` / `Window.DataContext` | Put the view in a `Page`, `UserControl`, or root `FrameworkElement` and place resources/context there. |
| `pack://` URI | Use the asset URI appropriate to the deployment model; packaged content commonly uses `ms-appx:///`. |

Do not use a find-and-replace for these constructs. One WPF `DynamicResource`, trigger, or `MultiBinding` can encode application behavior that requires a new view-model property or control state.

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

Do not claim that every custom project is broken merely because the entry is absent; compare with the target Windows App SDK template and understand how its default control resources are supplied.

### Theme dictionaries

Preserve Light, Dark, and High Contrast behavior. Use semantic resource names rather than copying fixed WPF colors:

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

For substantial theming or accessibility work, load `winui-design` rather than duplicating its rules here.

## Localization: x:Static to x:Uid

WPF often binds generated `.resx` properties:

```xml
<Button Content="{x:Static properties:Resources.Save}" />
```

WinUI XAML localization commonly uses `x:Uid` and `.resw` keys that name the target property:

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

Do not blindly rename `.resx` to `.resw`. Keep managed `.resx` resources where they remain appropriate for shared/non-XAML code, and preserve stable identifiers used by localization tooling or automation.

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

## Binding Strategy

Both classic `{Binding}` and `{x:Bind}` are valid in WinUI 3.

| Feature | `{Binding}` | `{x:Bind}` |
|---|---|---|
| Default source | `DataContext` | The containing XAML root object, such as a `Window`, `Page`, or `UserControl`; inside a `DataTemplate`, the data object being templated; inside a `ControlTemplate`, the templated control identified by `TargetType` |
| Default mode | `OneWay` | `OneTime` |
| Resolution | Runtime | Generated/compiled code |
| Function binding | No | Yes |
| Best migration use | Existing DataContext-driven views and dynamic sources | Typed views with stable compile-time sources |

Do not rewrite every binding form. Preserve the view architecture unless changing it is part of the migration.

An `{x:Bind}` path changes context at a template boundary. `x:DataType` declares the data-object type inside a `DataTemplate`, while `ControlTemplate.TargetType` declares the templated-parent type. Do not expect the outer view model to remain the implicit source inside either template.

Do not assume that preserving `{Binding}` preserves its effective mode. In WPF, an omitted `Mode` uses `BindingMode.Default`, whose effective value comes from the target dependency property's metadata; user-editable properties such as `TextBox.Text` and `ToggleButton.IsChecked` bind two-way by default. WinUI `{Binding}` defaults to `OneWay`. Audit WPF bindings that omit `Mode`, and set `Mode=TwoWay` explicitly in WinUI wherever edits must update the source.

### Explicit modes and TextBox updates

```xml
<TextBlock Text="{x:Bind ViewModel.Status, Mode=OneWay}" />

<TextBox Text="{x:Bind ViewModel.Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
```

`x:Bind` defaults to `OneTime`. For two-way `TextBox.Text`, the source normally updates on lost focus; specify `UpdateSourceTrigger=PropertyChanged` when validation, commands, or other UI must react to each edit.

### MultiBinding

```xml
<!-- WinUI function binding -->
<TextBlock Text="{x:Bind local:Formatters.FullName(ViewModel.FirstName, ViewModel.LastName), Mode=OneWay}" />
```

Alternatively expose a `FullName` property and raise its change notification when either input changes. Prefer that when the value belongs to application semantics rather than presentation formatting.

### Boolean to Visibility

Compiled binding can convert `bool` to `Visibility` in supported scenarios:

```xml
<ProgressRing
    IsActive="{x:Bind ViewModel.IsBusy, Mode=OneWay}"
    Visibility="{x:Bind ViewModel.IsBusy, Mode=OneWay}" />
```

For classic binding, template bindings, or other conversions, use an existing converter or visual state before creating a new converter. A WinUI `IValueConverter` receives `string language` as its last parameter.

### Relative sources and style setters

Review WPF `RelativeSource FindAncestor` and template bindings individually. Support and source context differ from WPF. Prefer typed `x:Bind`, `ElementName`, `TemplatedParent`, or moving the binding to the element/template when the original form is not valid.

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

For a standard control's pointer-over, pressed, disabled, selected, or focused behavior, start from its existing visual states. Do not replace the full control template merely to reproduce a small WPF trigger unless a lightweight style or existing state cannot express the requirement.

## Styles and Templates

- When an implicit style is intended to extend the platform control style, base it on the current default style key instead of accidentally replacing the whole style/template.
- Verify the correct default key from current samples; do not assume every control uses `DefaultButtonStyle`-like naming.
- Keep app-specific resources after `XamlControlsResources` so overrides are deliberate.
- Recheck visual-state names; WPF `MouseOver` commonly becomes WinUI `PointerOver`.
- Recheck `TemplateBinding`, `ContentPresenter`, focus visuals, automation, and High Contrast behavior after copying a template.
- Prefer a built-in control plus small style changes over a copied platform template that will drift across Windows App SDK versions.

## Window Content

WinUI `Window` is not a `FrameworkElement`. A common structure is a thin window that hosts a page or user control:

```xml
<Window
    x:Class="MyApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:views="using:MyApp.Views">
    <views:MainPage />
</Window>
```

Place `DataContext`, local resources, visual states, and responsive layout on the hosted view or its root element. Track window-level state in the window/application layer; see [Threading and Windowing](threading-and-windowing.md).

## WPF Property and Event Differences

| WPF | WinUI migration |
|---|---|
| `Visibility.Hidden` | `Collapsed` if space should disappear; `Opacity="0"` plus intentional hit-testing/layout behavior if space must remain. |
| `Focusable` | Usually `IsTabStop`; also verify programmatic focus and automation semantics. |
| `ContextMenu` | `ContextFlyout`. |
| `TextWrapping.WrapWithOverflow` | Use WinUI `TextWrapping.WrapWholeWords` on a control that supports it, such as `TextBlock`; it preserves overflow for an unbreakable word. `TextBox` and `RichEditBox` reject `WrapWholeWords`, so use `Wrap` only after accepting and testing the changed long-word behavior, or redesign width/scrolling behavior. |
| `IsDefault` / `IsCancel` | Design primary action and handle Enter/Escape/keyboard accelerators where the containing surface does not provide them. |
| `PreviewMouse*` | Redesign with pointer events; there is no blanket name substitution. |
| `PreviewKeyDown` / `PreviewKeyUp` | Available on WinUI `UIElement`; keep when behavior still fits. |
| `Mouse*` handlers | Use pointer/gesture events and update event argument types. |

If `Opacity="0"` replaces `Visibility.Hidden`, decide whether the element should remain hit-testable and accessible. Hiding pixels while retaining interaction is often a bug.

## Geometry and Clip Differences

WPF `CombinedGeometry` and complex `UIElement.Clip` expressions do not map mechanically. For rendered shapes, a `Path` can often use a `GeometryGroup` with `FillRule="EvenOdd"` to create union/hole effects. WinUI `UIElement.Clip` is more restricted (commonly `RectangleGeometry`), so complex clipping may require a `Path` overlay, mask/composition clip, or redesigned visual. Verify the exact target property/API instead of applying one workaround to every geometry.

## WPF-UI and Other WPF Control Libraries

When the source uses a WPF styling/control library:

1. List every library control and the behavior it contributes.
2. Search current native WinUI and Windows Community Toolkit samples.
3. Prefer native controls when they meet the requirement.
4. Add a replacement package only after checking maintenance, Windows App SDK compatibility, AOT/trimming, accessibility, and license.
5. Port application styles separately from control behavior; a similarly named control is not necessarily equivalent.

Examples such as WPF-UI `FluentWindow`, `NumberBox`, `InfoBar`, `ProgressRing`, and `SymbolIcon` may have native WinUI counterparts, but custom title-bar, windowing, and style behavior still require review.

## XAML Validation Pass

- Search again for `clr-namespace:`, WPF-only namespaces, `DynamicResource`, `x:Static`, `x:Type`, `MultiBinding`, triggers, `pack://`, WPF event names, and `Window.Resources`.
- Validate bindings that compile but have the wrong source or default mode.
- Exercise empty, loading, error, disabled, selected, keyboard-focus, Light, Dark, and High Contrast states.
- Confirm localized text, access keys, automation names, and right-to-left layout where supported.
- Use the repository's XAML formatter; do not copy another repository's formatting command or BOM policy.

## Official References

- [XAML resource dictionaries](https://learn.microsoft.com/windows/apps/develop/platform/xaml/xaml-resource-dictionary)
- [Data binding in depth](https://learn.microsoft.com/windows/apps/develop/data-binding/data-binding-in-depth)
- [`{x:Bind}` markup extension](https://learn.microsoft.com/windows/apps/develop/platform/xaml/x-bind-markup-extension)
- [WPF binding declarations overview](https://learn.microsoft.com/dotnet/desktop/wpf/data/binding-declarations-overview)
- [WPF data templating overview](https://learn.microsoft.com/dotnet/desktop/wpf/data/data-templating-overview)
- [WinUI `DataTemplate`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.datatemplate)
- [WinUI data template selector](https://learn.microsoft.com/windows/apps/develop/ui/controls/data-template-selector)
- [WinUI `TextWrapping`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.textwrapping)
