# Project and Application Migration

Use this reference when changing project files, application lifecycle, MVVM, localization, shared libraries, or compatibility contracts.

## Select the Target Before Editing the Project

Record the current and intended values for:

- Packaged, packaged with external location, or unpackaged deployment.
- Windows App SDK framework-dependent or self-contained deployment.
- .NET framework-dependent or self-contained publishing. This is separate from Windows App SDK self-containment.
- Target framework, minimum Windows version, processor architectures, and Windows App SDK version.
- Single-project MSIX, a separate packaging project, or no package.
- Existing activation extensions, capabilities, file/protocol associations, COM registration, installer behavior, and update identity.

These are independent decisions. Do not copy a property group from another application and assume it expresses the same deployment model.

## Migration Shape

### Parallel application project

Prefer a parallel WinUI project when the WPF application is large, must remain runnable during the migration, or has substantial non-UI logic that should move into shared libraries.

```text
WPF app -----\
              -> UI-neutral libraries -> services / models / contracts
WinUI app ---/
```

Keep the two application projects as composition roots. Avoid referencing one executable project from the other; put reusable behavior in a library with no WPF or WinUI dependency where practical.

### In-place conversion

An in-place conversion can work for a small application with shallow WPF coupling. Before changing the SDK properties, make a recoverable checkpoint and inventory every WPF-only asset, resource, build target, and generated file.

### Temporary hybrid boundary

If a staged migration intentionally retains WPF in a separate process, library, or XAML Island boundary, document:

- Which project owns the WPF dependency.
- How data crosses the boundary.
- Threading and lifetime ownership.
- Deployment implications.
- The condition for keeping or removing the boundary.

Do not let an accidental `<UseWPF>true</UseWPF>` in the WinUI application become an undocumented architecture.

## Project File Decisions

A WinUI application project normally includes `UseWinUI`, but the target framework and package versions should follow the solution rather than a hard-coded sample:

```xml
<PropertyGroup>
  <UseWinUI>true</UseWinUI>
</PropertyGroup>
```

Keep the repository-supported `TargetFramework` value or change it as a separately justified compatibility decision.

Review these properties semantically:

| Property or artifact | Use it when |
|---|---|
| `<UseWinUI>true</UseWinUI>` | The project compiles WinUI XAML. |
| `<WindowsPackageType>None</WindowsPackageType>` | The official configuration calls for it. It is used by unpackaged and packaged-with-external-location scenarios for automatic Windows App SDK runtime initialization; the mechanism differs between framework-dependent and self-contained deployment. Do not add it as an incidental launch workaround. |
| `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` | The selected deployment bundles the Windows App SDK runtime. It is not required for framework-dependent deployment. |
| `<SelfContained>true</SelfContained>` / publish settings | The selected .NET deployment is self-contained. This is a separate decision from `WindowsAppSDKSelfContained`. |
| `Package.appxmanifest` | The packaged application identity, capabilities, assets, and activation extensions. Preserve it for a packaged target. |
| `app.manifest` | Win32 process metadata such as requested execution level and compatibility declarations. It is not a replacement for package identity. |
| `ApplicationDefinition` | The SDK's default item classification is insufficient because `App.xaml` uses a nonstandard path or has an item conflict. Current templates usually do not require a hand-written item; if one is added, avoid also compiling the file as `Page`. |
| `DISABLE_XAML_GENERATED_MAIN` | A custom entry point is genuinely required for pre-XAML initialization or a nonstandard process mode. Do not enable it just because the WPF app had `Main`. |

Also check:

- Central package/version management before adding versioned references.
- `RuntimeIdentifier`/`RuntimeIdentifiers` and supported architectures.
- XAML `Page`, `ApplicationDefinition`, `PRIResource`, `Content`, and asset items.
- Root namespace, assembly name, default namespace, `x:Class`, and generated-code assumptions.
- Native/WinRT references, CsWinRT projections, COM registration, and architecture-specific binaries.
- Custom build targets that still expect WPF-generated files or output paths.

Remove WPF references only after their callers have moved. Conversely, do not keep `PresentationCore`, `PresentationFramework`, `WindowsBase`, `Microsoft.WindowsDesktop.App.WPF`, or WPF-only packages in the final WinUI application without a documented reason.

## Application Lifecycle

Do not overwrite the target template's `App.xaml` or `App.xaml.cs`. Merge behavior intentionally:

| WPF responsibility | WinUI migration question |
|---|---|
| `StartupUri` | Which window/page is created during activation? |
| `Application_Startup` / `OnStartup` | Which initialization belongs before or after XAML application startup? |
| `Application_Exit` / `OnExit` | Which cleanup is process-wide, window-scoped, or service-owned? |
| `Application.Current.MainWindow` | How will the app track one or more top-level windows explicitly? |
| `ShutdownMode` | What closes the process when multiple windows, tray behavior, or background work exists? |
| WPF command-line startup | How do launch arguments interact with package/protocol/file activation and app-instance redirection? |

Preserve the template's COM wrappers, dispatcher synchronization context, activation plumbing, and generated entry point unless the application has a demonstrated need to replace them.

## Package Identity, Activation, and Parallel Development

For a released packaged application, package identity is part of the upgrade contract. Preserve the production identity, publisher relationship, capabilities, aliases, and manifest extensions in the final target unless the product intentionally creates a new app.

When WPF and WinUI packaged projects must coexist during development:

- Give development builds a temporary identity/display name or avoid installing both packages at once; two independently installed packages cannot safely claim the same production identity.
- Keep production identity values in a controlled final-packaging path rather than casually copying them into every local project.
- Test a real upgrade from the last released package to the WinUI package. A clean install does not prove settings, file associations, aliases, or app data survive.
- Validate signing/publisher compatibility and architecture transitions used by the actual update channel.

For file, protocol, toast, startup-task, command-line, or other activation:

1. Inventory every manifest extension and the data shape delivered for cold activation.
2. Decide whether the app is single-instance, keyed multi-instance, or multi-window.
3. If using `AppInstance`, register/find the correct key and redirect secondary activation before creating duplicate UI.
4. Route both cold and redirected activation through one application-owned handler.
5. Select or create the correct window, then marshal the activation to that window's UI queue.
6. Test cold activation, activation while running, multiple files/URIs, invalid input, and activation during startup/shutdown.

Do not stop at preserving the manifest row. The runtime activation handler and window-routing behavior are equally part of the contract.

## Legacy Settings and App Data

WPF settings may live in `user.config`, an application-owned file under a legacy path, the registry, or another store. Packaged WinUI app data can resolve to a different identity-scoped location. Determine the actual released path and schema before choosing a migration.

A robust one-time migration is:

1. On first compatible WinUI launch, check whether the new store already contains a completed schema.
2. Read the legacy store through a narrowly scoped compatibility reader; do not require the WPF UI framework merely to parse simple data.
3. Map old names, enum values, paths, and defaults into the new schema.
4. Write the new store atomically, validate it, then record an idempotent migration marker.
5. Leave the old data intact unless product policy explicitly allows deletion and rollback is no longer needed.
6. Log/report recoverable parse errors without overwriting readable legacy data with defaults.

Test with fixtures copied from each supported released schema and with the real old-version-to-new-version package upgrade path.

## Shared Logic and Executable Boundaries

- Put models, serialization, file processing, protocol contracts, and other reusable behavior in UI-neutral libraries.
- Avoid executable-to-executable project references. If a CLI and GUI share logic, have both reference a library.
- Keep UI-thread concepts out of domain services. Pass an abstraction or dispatch at the view/view-model boundary.
- Preserve assembly names and `InternalsVisibleTo` relationships when tests, native code, reflection, or external callers depend on them.
- Check trimming, AOT, reflection, source generators, and COM activation when moving code between projects.

## MVVM Is an Explicit Architecture Choice

WinUI 3 does not require CommunityToolkit.Mvvm. Preserve the existing MVVM framework if it remains supported and the user did not ask for an architectural rewrite.

If replacing custom observable/command infrastructure is in scope, migrate behavior rather than only syntax. First confirm the repository's C# language version, compiler/IDE, and CommunityToolkit.Mvvm version. When they support the current partial-property generator syntax, prefer it over the older field-backed pattern:

```csharp
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
    private void Save()
    {
        // Persist changes.
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);
}
```

Put `[JsonPropertyName]` directly on the partial property; do not carry over `[property: JsonPropertyName(...)]` from the field-backed form. Move the old default value into the property initializer and keep dependent-property and command notifications, such as `[NotifyPropertyChangedFor]` and `[NotifyCanExecuteChangedFor]`.

If the existing toolchain does not support partial properties, keep the compatible pattern temporarily or make the compiler/Toolkit upgrade an explicit, separately validated part of the migration. Do not silently raise the language or package baseline just to copy this example. On WinUI/WinRT targets, field-backed `[ObservableProperty]` can report `MVVMTK0045` for AOT compatibility. Respect the repository's warning-as-error settings and validate the intended publish path rather than suppressing the diagnostic.

During conversion, preserve:

- Property names used by classic binding or serialization.
- Property-change dependencies and command `CanExecute` invalidation.
- Validation interfaces and error presentation.
- Async command cancellation and exception behavior.
- Design-time/test construction paths.

Do not rewrite every `{Binding}` to `{x:Bind}` merely because the view model changed. Choose the binding form based on the view's source and lifetime; see [XAML Migration](xaml-migration.md).

## Localization and Resources

WPF `.resx` and WinUI `.resw` serve overlapping but not identical roles.

- Use `.resw` plus `x:Uid` for WinUI XAML localization.
- A managed library may keep `.resx` resources when they are not XAML resources and callers already depend on the generated strongly typed API.
- Do not mechanically rename a `.resx` file to `.resw`; verify the resource build action, folder layout, key format, and generated PRI resource map.
- Preserve stable resource identifiers where localization tooling or external automation depends on them.

Example:

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

For strings requested in code, use the resource API and map appropriate to the project. Avoid resolving UI resources in static field initializers because tests, CLI paths, or early process startup may run before the resource context is ready. Delay lookup until the application/resource environment exists.

## CLI Package Upgrades

A WPF-to-WinUI migration does not require upgrading System.CommandLine. If upgrading to System.CommandLine 2.0 is also in scope, pass the option name and aliases to `Option<T>` and set `Description` separately. A second string argument is an alias, not a description:

```csharp
var destination = new System.CommandLine.Option<string>("--destination", "-d")
{
    Description = resourceLoader.GetString("DestinationDescription"),
};
```

Use the application's initialized resource accessor for the description. Also audit parsing, handlers, and invocation APIs against the chosen package version. Preserve published option names, aliases, defaults, and exit codes; compare help output and representative invocations after the package upgrade.

## Preserve Compatibility Contracts

Audit more than public C# APIs:

- JSON/XML property names, enum numeric values, defaults, and migration logic.
- Settings paths and upgrade behavior.
- File formats, image codec identifiers, and metadata policies.
- Command-line option names, exit codes, and stdout/stderr behavior.
- Named pipes, sockets, app services, RPC, IPC messages, and version negotiation.
- COM CLSIDs/IIDs, registration, threading model, and bitness.
- Protocol/file activation identifiers and package family identity.
- Resource keys used by tests, automation, or localization pipelines.

When a WPF type leaked into a stored schema or protocol, introduce an application-owned compatibility representation with the same serialized value instead of requiring the WPF assembly solely for deserialization.

## Test Adaptation

- Keep behavior tests around UI-neutral libraries independent of either UI framework.
- Convert tests to async when the replacement API is async; do not block the UI thread with `.Result` or `.Wait()`.
- Update numeric assertions where WinRT uses unsigned dimensions or different units.
- Initialize the required package/resource/window environment only for tests that actually need it.
- Compare semantic output, tolerances, metadata, and error behavior rather than expecting different rendering or codec implementations to be byte-identical.
- Add upgrade tests for persisted settings and activation tests for packaged/unpackaged entry paths when those contracts exist.

For a released packaged app, include an acceptance matrix:

| Scenario | What to verify |
|---|---|
| Clean install | Startup, defaults, resources, capabilities, and activation. |
| Last released version → WinUI upgrade | Package identity, app data/settings import, shortcuts/aliases, file/protocol associations, and user-visible state. |
| Cold activation | Each file/protocol/command entry creates the correct state/window. |
| Activation while running | App-instance redirection, deduplication, window selection, focus, and error handling. |
| Multi-window activation | Input routes to the intended existing/new window without cross-window dispatcher or `XamlRoot` mistakes. |
| Failed/cancelled migration | Legacy data remains recoverable and a retry is safe. |

## Official Deployment References

- [Windows app packaging and deployment overview](https://learn.microsoft.com/windows/apps/package-and-deploy/deploy-overview)
- [Deploy Windows App SDK apps that are unpackaged](https://learn.microsoft.com/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
- [Windows App SDK self-contained deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
