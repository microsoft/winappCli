# Source Generator Patterns — Detailed Reference

Patterns for Native AOT and trimming in WinUI 3. See [SKILL.md](../SKILL.md) for project packaging and `winui-dev-workflow` for analyzer setup and publish runs.

---

## Native AOT Intent and Diagnostics

Prefer persistent project intent, not just a publish command override:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <SuppressTrimAnalysisWarnings>false</SuppressTrimAnalysisWarnings>
  <TrimmerSingleWarn>false</TrimmerSingleWarn>
  <CsWinRTAotWarningLevel>2</CsWinRTAotWarningLevel>
</PropertyGroup>
```

Native AOT enables trimming. Keep IL and CsWinRT warnings enabled and fix the offending reflection, ABI, or binding pattern; do not disable analysis to obtain a clean publish. A successful JIT build/run is not evidence that the published AOT app works.

`CsWinRTAotOptimizerEnabled` defaults to `True` (selecting **Auto** for WinUI). Do not turn it off. Types implementing projected interfaces, extending projected classes, or implementing mapped .NET interfaces and crossing the WinRT ABI must be **partial** so CsWinRT can generate their vtables. Warning level 2 also covers mapped built-in interfaces: inspect each warning and make the relevant source types partial rather than applying blanket suppressions. Dependencies must be AOT-compatible too.

---

## Reflection and Trimming

Prefer static references/source generation over runtime type discovery. Replace `Assembly.LoadFrom()` with compile-time dependencies and `Type.GetType("…")` with known types where possible. When reflection is necessary, express the member requirements and resolve the resulting diagnostics:
```csharp
using System.Diagnostics.CodeAnalysis;

public object? CreateService(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    Type serviceType) => Activator.CreateInstance(serviceType);
```
Annotations preserve required members; they do not make arbitrary dynamic loading or runtime code generation AOT-compatible. Test the actual published artifact and exercise reflection-dependent paths.

---

## JSON Source Generator Setup

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSerializable(typeof(UserProfile))]
[JsonSerializable(typeof(List<UserProfile>))]
internal partial class AppJsonContext : JsonSerializerContext { }

// No reflection at runtime
var json = JsonSerializer.Serialize(profile, AppJsonContext.Default.UserProfile);
var obj = JsonSerializer.Deserialize(json, AppJsonContext.Default.UserProfile);
```

---

## Regex Source Generator

```csharp
public partial class InputValidator
{
    [GeneratedRegex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$")]
    private static partial Regex EmailRegex();
    public bool IsValidEmail(string input) => EmailRegex().IsMatch(input);
}
```

---

## Compiled and Runtime XAML Binding

Prefer `x:Bind` for known source types. In a page/window, paths resolve against the code-behind instance (for example, its `ViewModel` property), **not** `DataContext`. Do not set `x:DataType` on a `Page` to declare its VM.

```xml
<TextBlock Text="{x:Bind ViewModel.Title, Mode=OneWay}" />

<!-- Type the template's item, not the page's ViewModel -->
<DataTemplate x:DataType="models:Document">
    <TextBlock Text="{x:Bind Title, Mode=OneWay}" />
</DataTemplate>
```

`x:Bind` defaults to `OneTime` unless an inherited `x:DefaultBindMode` changes it. Dynamic data needs an effective `OneWay`/`TwoWay` mode plus change notifications; stable command/event bindings do not need a blanket mode rewrite.

Runtime `{Binding}` and `DisplayMemberPath` are **not categorically unsupported**. For their `ICustomPropertyProvider` path, make each source class partial and request generated bindable properties:
```csharp
[WinRT.GeneratedBindableCustomProperty]
public partial class Person
{
    public string Name { get; set; } = string.Empty;
}
```
This generates an AOT-safe property provider for public properties; it does not supply change notifications. Set the correct `DataContext`/item source, add notifications for mutable data, and exercise the runtime binding in the published app. See the [CsWinRT guidance](https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md) for scoped property generation and ABI cases the generator cannot infer.

---

## CsWin32 Setup

Generates P/Invoke wrappers at compile time. Add `Microsoft.Windows.CsWin32` NuGet package. List needed APIs in `NativeMethods.txt`:

```
GetDpiForWindow
SetWindowPos
ShowWindow
```

---

## CommunityToolkit.Mvvm Source Generators

Use partial properties (CommunityToolkit.Mvvm 8.4+ and a compiler supporting partial properties). The field form triggers **MVVMTK0045** because CsWinRT cannot generate the required WinRT marshalling support for those generated properties. Make the containing types partial; do not suppress the warning to preserve field syntax.

```csharp
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] public partial string UserName { get; set; }
    [RelayCommand] private async Task SaveAsync() => await _service.SaveAsync(UserName);
}
```

## Official References

- [C#/WinRT AOT, trimming, and generated bindable properties](https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md)
- [MVVMTK0045 and partial properties](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/generators/errors/mvvmtk0045)
- [.NET Native AOT prerequisites and publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
