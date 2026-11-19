---
name: winui-wpf-migration
description: "Migrate WPF desktop apps to WinUI 3: project files, XAML, MVVM, resources, windowing, imaging, installers, and tests. Use for WPF ports and migration regressions. Preserve existing behavior and deployment; not for unrelated Windows App SDK work."
---

# WPF to WinUI 3 Migration

Port WPF code to WinUI 3 while preserving the application's behavior, deployment, stored data, and external contracts.

## When to Use

- Convert WPF XAML, controls, bindings, or `System.Windows.*` APIs to WinUI 3.
- Port dispatcher calls, windows, dialogs, file pickers, or imaging code.
- Adapt observable properties, commands, resources, and tests during a WPF migration.
- Fix project, runtime, installer, or signing failures caused by that migration.

Use `winui-design` for control samples, `winui-dev-workflow` for project setup and build/run commands, and `winui-packaging` when packaging is in scope. Keep the target project's versions, deployment, and required behavior when using those workflows.

## Migration Sequence

Port bounded groups of files in dependency order:

1. **Project files** — prepare the WinUI target, package references, XAML/resource items, native references, and assets. Keep assembly names and deployment settings required by existing callers.
2. **Models and shared logic** — remove UI-framework dependencies from reusable logic. Share CLI/UI behavior through a library when needed, without executable-to-executable project references.
3. **MVVM** — replace custom observable/command infrastructure with CommunityToolkit.Mvvm. Preserve setter transformations, defaults, serialization attributes, notifications, and command behavior.
4. **Resource strings** — migrate the application's `.resx` strings and their code/XAML consumers to `.resw`, connect the PRI resource index and accessor, and keep stable keys.
5. **Services and imaging** — replace WPF types and propagate asynchronous APIs through callers. Preserve codec, metadata, cancellation, and error behavior.
6. **View models** — move UI updates onto their owning dispatcher queue and adapt binding/command notifications.
7. **Views and pages** — start with leaf views. Port markup, styles, converters, templates, keyboard behavior, and accessibility.
8. **Main view and shell** — integrate the converted views, navigation, windows, and owner-dependent dialogs/pickers.
9. **Application startup** — merge initialization, resources, activation, and cleanup into the WinUI lifecycle.
10. **Existing installer, packaging, or signing steps** — [update only the affected steps](references/project-and-application-migration.md#installer-and-build-pipeline) if the port changes the files they consume or when those files are ready.
11. **Tests** — adapt async calls, resource access, pixel/metadata assertions, settings fixtures, and integration scenarios.

Compile each migrated group and run its relevant tests when the repository permits those checks. Follow restrictions that defer builds to final acceptance, and keep failures attributable to the changed group.

## Migration Rules

### Projects and application code

Check uncertain replacement APIs against the restored WinUI app, not the machine SDK. From the target app's project directory, search for the type, then inspect the member on the intended type (see `winui-design`):

```powershell
winapp find-api FileOpenPicker --json --project-dir .
winapp find-api members Microsoft.Windows.Storage.Pickers.FileOpenPicker --filter PickSingleFileAsync --json --project-dir .
```

- Merge into the target's `App.xaml` and `App.xaml.cs`; preserve generated startup and initialization that the target SDK needs.
- Remove WPF-only dependencies and project items once their callers have been ported. Do not add `UseWPF` or a WPF framework reference to the WinUI application to conceal an incomplete conversion.
- Keep the existing `Package.appxmanifest` for packaged apps. Change `WindowsPackageType`, `WindowsAppSDKSelfContained`, `SelfContained`, or package identity only as part of the chosen deployment configuration.
- Keep reusable logic out of executable project references; check unexpected copied executables, dependency files, and runtime configuration files in the output.
- Use CommunityToolkit.Mvvm for migrated observable properties and commands. Prefer source-generated partial properties with a supported compiler and Toolkit version.
- Keep the way the app creates services. If it already uses dependency injection, register migrated services there; do not add a container for the port.
- Use a plain `Window` for top-level windows. Set its size with `AppWindow`; use `OverlappedPresenter` to control whether users can resize it or it stays on top. If the project already uses WinUIEx, keep its `WindowEx` base.
- Use `ContentDialog` for in-app dialogs and `Frame`/`Page` for sub-views. Reserve separate windows for distinct top-level surfaces.
- Preserve JSON names, enum values, defaults, resource keys, CLI options, and IPC/COM contracts.

### XAML and resources

- Replace `clr-namespace:` with `using:`, and map unsupported markup individually. `x:DataType` enables typed templates but does not reproduce WPF's implicit template selection.
- Use `ThemeResource` for theme-dependent resources, `StaticResource` for fixed resources, and binding/code for other dynamic values.
- Replace WPF triggers with the appropriate visual states, bindings, or WinUI behaviors. Put state groups on a `Page`/`UserControl` content root or a control-template root.
- Use direct Boolean `x:Bind`, existing control states, or suitable Toolkit converters before copying a WPF converter. Preserve the intended conversion and its inverse.
- Prefer `x:Bind` for new and migrated views with known types. Use classic `Binding` where the source must be resolved through runtime `DataContext`.
- Audit binding source, effective mode, and update timing. WPF editable properties that bind two-way by default need explicit `Mode=TwoWay` in WinUI.
- Put `DataContext` and element resources on the hosted view/root element. A WinUI `Window` does not expose the WPF equivalents.
- Preserve valid `PreviewKeyDown`/`PreviewKeyUp` handlers; redesign WPF mouse preview events according to their required behavior.
- Keep resource lookup out of early static initialization paths used by models, CLI entry points, or tests.

### Threading and imaging

- Map `Dispatcher.Invoke` by its completion and exception semantics. `DispatcherQueue.TryEnqueue` only reports whether work was accepted.
- Track the window/queue that owns UI state. Use that window's loaded `XamlRoot` for dialogs and its HWND or `WindowId` for the selected picker API.
- Keep image processing separate from XAML image sources. Validate pixel format, alpha, dimensions, orientation, codec options, and stream ownership.
- Preserve all required frames and metadata when replacing WPF encoders; a successful encode alone does not establish equivalent output.

## Common Migration Failures

| Symptom | What to check |
|---|---|
| A WinUI project still needs WPF assemblies | Check callers, resource items, generated files, and shared-library dependencies for WPF-only types. A `System.Windows` name alone is not a failure: `System.Windows.Input.ICommand` is valid in WinUI. |
| Startup reports a Windows App SDK runtime/COM activation error | Launch the project with `winapp run .\MyApp.csproj --detach --json` first (see `winui-dev-workflow`). Starting a packaged app's `.exe` directly skips package activation. If project launch still fails, check package identity, runtime deployment, architecture, and initialization. |
| A window's bindings or resources do not resolve | Move view state and resources to the hosted view/root and check the binding source. |
| Edited text does not reach the view model | Check explicit `Mode=TwoWay`, update timing, and notifications. |
| A dialog or picker opens without the correct owner | Supply the owning loaded `XamlRoot`, HWND, or `WindowId` required by that API. |
| Localized strings fail during startup or tests | Check PRI generation/name, resource-map lookup, initialization timing, and the test resource accessor. |
| A build script works from the solution but not from the project | Check the [project-relative build paths](references/project-and-application-migration.md#project-relative-build-paths). |
| Image quality, dimensions, or tests change | Check codec options and types, pixel/alpha representation, integer widths, and frame/metadata handling. |

## Detailed References

Read the reference that matches the current phase:

- [Project and Application Migration](references/project-and-application-migration.md) — project items, MVVM, localization, installer/build changes, tests, and compatibility.
- [Namespace and API Mapping](references/namespace-api-mapping.md) — namespaces, types, controls, properties, packages, and interop.
- [XAML Migration](references/xaml-migration.md) — markup, binding, converters, templates, styles, and resource syntax.
- [Threading and Windowing](references/threading-and-windowing.md) — queue semantics, window sizing/lifecycle, dialogs, pickers, and custom entry points.
- [Imaging Migration](references/imaging-migration.md) — streams, codecs, options, multi-frame operations, metadata, and image tests.

## Completion Checklist

- [ ] The WinUI application's project items and dependencies reflect the completed conversion.
- [ ] Build and launch the app with project-mode `winapp run` by default. If activation depends on a separate packaging project (`.wapproj`), use that project's existing deployment and launch steps instead. Confirm the expected window opens and responds to input.
- [ ] Startup, shutdown, window ownership, dialogs, and pickers preserve the required behavior.
- [ ] Binding updates, commands, themes, localization, keyboard access, and automation remain correct.
- [ ] Settings fixtures, CLI behavior, and both directions of any existing IPC integration remain compatible.
- [ ] If the port affected an existing installer, package, or signing step, its inputs match the actual build/publish output.
- [ ] Tests exercise the same image, resource, and application behavior as before the migration.
- [ ] The intended published output is validated; use `winui-dev-workflow` for Native AOT publish/run steps and `winui-packaging`'s `references/sourcegen-patterns.md` for trimming/binding/serialization guidance.
