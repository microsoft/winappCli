# Project and Application Migration

Use this reference for project files, application startup, MVVM, resources, installer inputs, and tests.

## Project File Migration

Keep the application's deployment and compatibility requirements while replacing WPF project items.
Check the target framework, minimum Windows version, architectures, and centrally managed package versions before editing.
For scaffolding, SDK prerequisites, analyzer setup, and build/run commands, use `winui-dev-workflow`.

| Property or artifact | Migration action |
|---|---|
| `UseWPF` / `UseWinUI` | Remove WPF compilation from the migrated app and set `<UseWinUI>true</UseWinUI>`. Move remaining WPF callers first. |
| `TargetFramework` | Use the solution's supported Windows TFM. |
| `RuntimeIdentifier` / `RuntimeIdentifiers` | Keep supported architectures and the native dependencies needed by each. |
| `WindowsPackageType=None` | Use only for the intended deployment configuration. Unpackaged and packaged-with-external-location scenarios use it for automatic Windows App SDK initialization. |
| `WindowsAppSDKSelfContained` | Controls whether the application bundles the Windows App SDK runtime. |
| `SelfContained` | Controls .NET runtime deployment separately from Windows App SDK self-containment. |
| `Package.appxmanifest` | Preserve packaged identity, capabilities, assets, and activation extensions. |
| `app.manifest` | Preserve Win32 execution-level and compatibility declarations; it does not provide package identity. |
| `DISABLE_XAML_GENERATED_MAIN` | Add only for a required custom entry point; otherwise keep generated startup. See [Threading and Windowing](threading-and-windowing.md). |

Check `Microsoft.WindowsAppSDK`, the template's build-tool references, and any native/WinRT dependencies against the chosen project.
Remove obsolete WPF-only packages after replacing their callers.
For package mappings and CsWinRT projection changes, see [Namespace and API Mapping](namespace-api-mapping.md).

### XAML and Resource Items

When `App.xaml` has a nonstandard path and needs explicit classification, avoid compiling it as both a page and an application definition:

```xml
<ItemGroup>
  <Page Remove="UI\App.xaml" />
  <ApplicationDefinition Include="UI\App.xaml" />
</ItemGroup>
```

Do not add this item group when the target SDK already classifies the default `App.xaml` correctly.

| Item | Check after moving files |
|---|---|
| XAML `Page` items | Paths, `x:Class`, namespaces, and generated partial classes agree. |
| `PRIResource` | The intended `.resw` files enter the PRI resource index; avoid duplicate explicit/default items. |
| `Content` and assets | Icons and images reach the intended build/publish/package output. |
| `ApplicationIcon` and RC references | All references follow the new asset location, including native resource scripts. |
| Old helpers and manifests | Remove unused observable/command helpers, WPF converters, and replaced manifests after checking all configurations. |

Preserve assembly names, root namespaces, and internal visibility used by tests, native callers, or serialization.
For new SDK-style projects, prefer `InternalsVisibleTo` items in the tested project's `.csproj` to keep assembly configuration together.
When migrating an existing project, follow its assembly-attribute convention; keep valid source declarations unless consolidating assembly configuration.
When using project items, preserve each friend assembly's exact name and any public key:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="ExampleApp.Tests" />
</ItemGroup>
```

Before removing the source attributes, confirm that SDK generation is enabled by `GenerateAssemblyInfo` and `GenerateInternalsVisibleToAttributes` for every required configuration; preserve any existing conditions. Keep source declarations when SDK generation is disabled or cannot preserve those conditions.
Before completing the app conversion, check for remaining `PresentationCore`, `PresentationFramework`, `WindowsBase`, `Microsoft.WindowsDesktop.App.WPF`, and WPF-only packages.

## Shared Logic and Application Startup

When a CLI and GUI share behavior, move that behavior into a library:

```text
ExampleApp.Cli -> ExampleApp.Core
ExampleApp.UI  -> ExampleApp.Core
```

Do not make the CLI reference the GUI executable just to reuse models or file processing.
Executable references can copy unwanted `.exe`, `.deps.json`, or `.runtimeconfig.json` files into consumers' output.
Fix the reference graph and inspect build/publish outputs before updating installer file lists.

Do not overwrite the generated WinUI `App.xaml` or `App.xaml.cs`. Merge the old responsibilities:

| WPF responsibility | WinUI migration action |
|---|---|
| `StartupUri` | Create and activate the intended window and initial page. |
| `Application_Startup` / `OnStartup` | Place existing initialization at the appropriate point in WinUI startup. |
| `Application_Exit` / `OnExit` | Preserve cleanup and service shutdown. |
| `Application.Current.MainWindow` | Track the application's window explicitly. |
| `ShutdownMode` | Preserve exit behavior when windows close or background work remains. |
| Command-line or registered activation | Preserve argument parsing, registered entry points, and the behavior they invoke. |

Merge application resource dictionaries after the template's platform resources; see [XAML Migration](xaml-migration.md).

Obtain application services through dependency injection (DI) in views and view models: inject them or resolve them from the application's configured service provider instead of constructing them with `new`.
Service registration and explicit construction belong in the composition root (the startup code that assembles the application), preserving service lifetimes and cleanup.

## MVVM Migration

Migrate custom `Observable` / `RelayCommand` infrastructure to CommunityToolkit.Mvvm source generators by default.
Preserve behavior as well as public property and command names.

| Existing implementation | Toolkit migration |
|---|---|
| Custom observable base | Derive from `ObservableObject`; make generated types `partial`. |
| Plain field-backed property | Use a public partial property with `[ObservableProperty]`. |
| Stored default value | Keep it in the generated property's initializer. |
| Dependent computed property | Add `[NotifyPropertyChangedFor]` to **each** input that changes its result. |
| Command enablement | Preserve `CanExecute` and its invalidation, including `[NotifyCanExecuteChangedFor]` where appropriate. |
| Custom setter logic | Preserve normalization, validation, side effects, and notification order; do not replace it with a plain generated property. |
| Custom command | Use `[RelayCommand]` only after preserving arguments, cancellation, exception handling, and the generated command's name. |

Use partial properties for generated observable state:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json.Serialization;

public partial class EditorViewModel : ObservableObject
{
    [ObservableProperty]
    [JsonPropertyName("document_name")]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = "Untitled";

    [JsonIgnore]
    public string DisplayName => Name.Trim();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() { /* Persist changes. */ }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);
}
```

`Save` generates `SaveCommand`. Check existing XAML and callers before changing a method name.
Put JSON attributes directly on a partial property, without the field pattern's `property:` target.

For a setter that transforms the incoming value, keep a hand-written property:

```csharp
public partial class FileViewModel : ObservableObject
{
    private string name = "";

    [JsonPropertyName("name")]
    public string Name
    {
        get => name;
        set => SetProperty(ref name, NormalizeName(value));
    }

    private static string NormalizeName(string value) => value.Trim();
}
```

Keep the old transformation and null/error behavior; `Trim` only illustrates the pattern.

Use a compiler, language version, and Toolkit version that support partial-property generation within the application's supported toolchain.
If the target toolchain cannot support it, use equivalent hand-written properties instead of field-based generation.
`MVVMTK0045` warns that field-based `[ObservableProperty]` is not compatible with WinRT AOT; warning-as-error policy determines whether it fails the build.

For binding source, lifetime, and update behavior, see [XAML Migration](xaml-migration.md).

## Localization and Resource Access

Migrate the application's own `.resx` resource sets to `.resw` by default, including strings read only from code:

Move non-string resources such as icons and images to the corresponding WinUI asset/content items and update their callers; do not treat serialized resource objects as localized strings.

1. Put localized `.resw` files in the project's language folders, such as `Strings/en-US/Resources.resw`.
2. Verify the resource build items, resource map, and generated PRI file.
3. Convert localized XAML properties to `x:Uid` and property-suffixed keys.
4. Replace code calls to the migrated `Properties.Resources` accessor.
5. Remove each migrated `.resx` file and its obsolete designer/generator after moving all callers and updating localization tooling.

```xml
<Button x:Uid="SaveButton" />
```

Corresponding `.resw` entries name the target properties:

```xml
<data name="SaveButton.Content" xml:space="preserve">
  <value>Save</value>
</data>
<data name="SaveButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" xml:space="preserve">
  <value>Save document</value>
</data>
```

Preserve stable identifiers or update every consumer of a renamed key.
Shared libraries outside this migration retain their `.resx` resources and generated accessors.

### Named PRI Files and Deferred Lookup

If the application uses a custom PRI filename, keep the build property, deployed file, and runtime lookup in agreement:

```xml
<PropertyGroup>
  <ProjectPriFileName>ExampleApp.pri</ProjectPriFileName>
</PropertyGroup>
```

Use a `ResourceLoader` accessor for the migrated resource sets, updating an existing application accessor where available. A small accessor can defer creation until the resource environment is ready:

```csharp
using System;
using Microsoft.Windows.ApplicationModel.Resources;

internal sealed class AppStrings
{
    private readonly Lazy<ResourceLoader> loader =
        new(() => new ResourceLoader("ExampleApp.pri"));

    public string GetString(string key) => loader.Value.GetString(key);
}
```

Use the constructor and resource map that match the actual PRI layout; a custom map may require its own name.
A delayed constructor does not initialize Windows App SDK for a CLI or test process: establish the required runtime first.

Do not resolve resource-dependent formats or token dictionaries in static field initializers.
Store stable resource keys and resolve them when needed, or use deferred initialization after resources are available.

## CLI Options and Compatibility

A WPF migration does not require a System.CommandLine upgrade.
If version 2.0 is also in scope, its `Option<T>` constructor takes a name and aliases; set the description separately:

```csharp
var destination = new System.CommandLine.Option<string>("--destination", "-d")
{
    Description = strings.GetString("DestinationDescription"),
};
```

Use the initialized application accessor for `strings`.

| Contract | Preserve and verify |
|---|---|
| Settings and serialized data | Existing file paths, JSON/XML names, enum numeric values, defaults, and deserialization of released fixtures. |
| File/image formats | Existing values and supported output behavior; see [Imaging Migration](imaging-migration.md). |
| CLI | Published option names, aliases, defaults, argument meaning, help output, exit codes, and stdout/stderr behavior. |
| IPC | Message names, payloads, and both senders and receivers. |
| Activation and localization | Existing file/protocol registrations, package identity, resource keys, and consumers. |

When removing a WPF type from persisted data, use an application-owned representation that preserves its serialized value.

If IPC changes are necessary, update both ends together.
For bidirectional communication, test both settings/commands reaching the application and application state reaching the controller.

## Installer and Build Pipeline

When the application has installer, packaging, or signing steps, follow the new output through all of them:

| Change or symptom | Required action |
|---|---|
| Old `<App>.resources.dll` is no longer produced; WiX reports `WIX0103` | Remove obsolete satellite-assembly component/file entries and unused locale-directory entries. Keep satellite assemblies still produced by retained `.resx` resources. |
| UI resources moved to PRI/assets | Include the generated PRI and required assets at the paths expected by the installed application. |
| Output directory or binary name changed | Update installer inputs, component-generation scripts, copy steps, signing file lists, and CI artifact paths together. |
| Unexpected GUI host files appear in CLI/root output | Fix executable project references or output-copy rules, then regenerate file components from intended outputs. |
| File components are generated | Use the repository's generator after outputs are available; inspect the XML diff for stale files, duplicate components, and incorrect destinations. |
| Signing fails or misses a file | Match signing inputs to the final binary paths. |

Edit installer XML as XML. Do not strip filenames with text replacement and leave broken component definitions.
Use `winui-packaging` for release packaging commands when packaging is in scope.

### Project-Relative Build Paths

`$(SolutionDir)` may be unavailable when a project builds directly.
Resolve scripts and inputs relative to the file that defines them, and quote paths containing spaces:

```xml
<!-- Inside the existing resource-generation target -->
<Exec Command="powershell -NoProfile -File &quot;$(MSBuildThisFileDirectory)..\tools\GenerateResources.ps1&quot;" />
```

Adapt the relative path to its owning project or imported targets file.

### Packaging Order and Asset References

A `PreBuildEvent` cannot package binaries that the same build has not produced.
Place custom collection, packaging, and signing targets after the build/publish targets whose outputs they consume.
Use the required MSBuild dependencies or `AfterTargets` relationship. If packaging consumes publish output, `PostBuildEvent` is too early.
Keep package output outside the input layout and exclude stale package files from subsequent collections.

After moving an icon, update the project, native RC script, and installer references.
In an RC string, escape backslashes correctly:

```c
IDI_APP_ICON ICON "..\\Assets\\App.ico"
```

## Test Adaptation

| Test surface | Migration action |
|---|---|
| Models and shared processing | Keep ordinary unit tests independent of the XAML runtime. Preserve transformations, validation, property notifications, and command enablement. |
| Old-versus-new comparisons still using WPF | If the old comparison code needs WPF, temporarily set `<UseWPF>true</UseWPF>` in that test project and remove it after porting the tests. Keep it out of the migrated app. |
| Async replacements | Return `Task` from tests and await the API; do not block a UI thread with `.Result` or `.Wait()`. |
| WinRT numeric results | Match the expected type, for example `96u` for a `uint` pixel dimension, and preserve units/tolerances. |
| Resource consumers | Supply a deterministic lookup and test formatting, validation messages, missing-key behavior, and other existing behavior. |
| Real PRI/localization | Exercise the deployed resource file, expected maps/keys, languages, and fallback with the required runtime available. |
| XAML objects | Use a WinUI test application and its UI-thread test mechanism, such as `[UITestMethod]`; an ordinary test process is not a XAML host. |
| Settings and IPC | Use isolated settings locations and released data fixtures; verify both communication directions where supported. |

For a resource consumer that accepts a `Func<string, string>`, a test lookup can be:

```csharp
Func<string, string> getString = key => key switch
{
    "NameRequired" => "Enter a name.",
    _ => throw new KeyNotFoundException(key),
};
```

Supply it through the application's existing constructor/property seam and assert the consumer's behavior.
If an existing static accessor is replaced for tests, restore it afterwards and isolate tests that mutate it.
Keep separate integration tests for actual resource lookup; do not delete behavior tests because the real loader is unavailable.

For tests referencing WinUI projects, align the Windows target framework, architecture, and required runtime with the app.
For pixel reads, metadata assertions, unsafe buffer access, and codec comparisons, see [Imaging Migration](imaging-migration.md).
When installer validation is permitted, verify the installed app finds its runtime, PRI/assets, settings, and existing entry points.

## References

- [InternalsVisibleTo project items](https://learn.microsoft.com/dotnet/core/project-sdk/msbuild-props#internalsvisibleto)
- [Assembly attribute migration](https://learn.microsoft.com/dotnet/standard/assembly/set-attributes-project-file#migrate-from-net-framework)
- [Windows app packaging and deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/deploy-overview)
- [Windows App SDK self-contained deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [ResourceLoader constructors](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.windows.applicationmodel.resources.resourceloader.-ctor)
- [MVVM Toolkit warning MVVMTK0045](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/errors/mvvmtk0045)
- [Testing WinUI applications](https://learn.microsoft.com/windows/apps/winui/winui3/testing/create-winui-unit-test-project)
