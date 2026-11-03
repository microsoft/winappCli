---
name: winui-wpf-migration
description: "Migrate WPF apps and controls to WinUI 3 while preserving deployment and behavior: XAML, MVVM, threading, windows, resources, imaging, and project files. Use for WPF-to-WinUI porting or migration regressions. Not for apps that only reference the Windows App SDK."
---

# WPF to WinUI 3 Migration

Treat this as an application-platform migration, not a namespace replacement. Preserve user-visible behavior, persisted data, activation contracts, and the existing deployment model unless the user explicitly asks to change them.

## When to Use

- Port a WPF application, library, window, page, or control to WinUI 3.
- Replace WPF XAML constructs or `System.Windows.*` APIs.
- Migrate windowing, dispatcher, dialogs, pickers, localization, or imaging code.
- Diagnose a partially migrated application that builds, launches, or behaves incorrectly.
- Plan a staged migration where WPF and WinUI projects temporarily coexist.

For control selection and visual design, use `winui-design` and `winapp find-ui` before choosing a replacement. For project scaffolding, target SDK and per-app analyzer setup, and build/launch verification, use `winui-dev-workflow` (WinApp CLI 0.7+). Keep the audited deployment and compatibility choices when following those workflows, and follow the repository's validation restrictions. Use `winui-packaging` only when packaging or distribution is actually in scope.

## 1. Establish the Migration Contract

Before editing code, record these decisions from the repository and the user request:

| Decision | Questions to answer |
|---|---|
| Deployment | Is the target packaged, packaged with external location, or unpackaged? Framework-dependent or self-contained? |
| Compatibility | Which Windows versions, .NET target, Windows App SDK version, architectures, and upgrade paths must remain supported? |
| Migration shape | In-place conversion, a parallel WinUI project, or a temporary shared-core architecture? |
| UI architecture | Keep the existing MVVM/navigation/DI approach, or is an architectural change explicitly requested? |
| Contracts | Which settings, file formats, command-line arguments, IPC/COM interfaces, protocol/file activation, and localization keys must remain stable? |
| Validation | Which builds, tests, launch paths, installers, and manual scenarios are permitted and required by repository instructions? |

Do not infer these choices from a template or another repository. In particular, never add or remove `WindowsPackageType`, `WindowsAppSDKSelfContained`, `SelfContained`, or a package manifest without first identifying the intended deployment model.

## 2. Audit Before Porting

Inventory WPF dependencies and migration hotspots. Exclude generated output:

```powershell
rg -n -g '!**/bin/**' -g '!**/obj/**' 'UseWPF|Microsoft\.WindowsDesktop\.App\.WPF|Presentation(Core|Framework)|System\.Windows' .
rg -n -g '*.xaml' -g '!**/bin/**' -g '!**/obj/**' 'DynamicResource|x:Static|x:Type|MultiBinding|Style\.Triggers|DataTrigger|EventTrigger|Window\.Resources|clr-namespace:|pack://' .
rg -n -g '*.cs' -g '!**/bin/**' -g '!**/obj/**' 'Dispatcher|Window\.Current|Application\.Current\.MainWindow|MessageBox|File(Open|Save)Picker|FolderPicker|System\.Windows\.Media\.Imaging' .
```

Build a migration matrix with one row per affected surface:

```text
surface | current dependency | target approach | compatibility risk | validation
```

Classify each item as one of:

- Namespace-only change.
- API with different threading, lifetime, or error semantics.
- XAML construct with no direct equivalent.
- Control that requires a behavior-driven replacement.
- Deployment, activation, localization, or persistence contract.
- Intentional compatibility boundary that will remain outside the WinUI project.

Before implementing a target API, check the restored WinUI project's exact references rather than the old WPF project or machine SDK. With WinApp CLI 0.7+, run from the target app directory:

```powershell
winapp find-api DispatcherQueue --json --project-dir .
winapp find-api members DispatcherQueue --filter TryEnqueue --json --project-dir .
winapp find-api check-property ListView ItemsSource SelectionMode --json --project-dir .
```

Use either `--project-dir <app-directory>` or `--project <name>` to select a project, never both. See `winui-design` for sample discovery and API checks. If the lookup tool is unavailable, inspect the target packages and official API documentation and report any unverified assumptions.

## 3. Choose a Safe Migration Shape

- Prefer a parallel WinUI application project for medium or large migrations when it keeps the existing WPF app runnable while shared logic moves behind UI-neutral libraries.
- An in-place conversion is reasonable for small applications with shallow WPF coupling and a short recovery path.
- Keep domain models and services UI-neutral. Do not make shared libraries depend on WinUI merely to remove WPF.
- If WPF and WinUI must coexist temporarily, document the boundary, owner, and removal condition. Do not leave `<UseWPF>true</UseWPF>` or WPF framework references in the final WinUI application accidentally.
- Preserve assembly names, root namespaces, resource identifiers, and serialized names when external callers or upgrades depend on them.
- Check existing window base classes and approved helper libraries before replacing WPF window properties; see [Threading and Windowing](references/threading-and-windowing.md).

For project, lifecycle, MVVM, localization, and compatibility details, read [Project and Application Migration](references/project-and-application-migration.md).

## 4. Port in Bounded Slices

Use the repository's dependency graph rather than rewriting every file at once. A typical order is:

1. Create or prepare the target project without changing its deployment intent.
2. Extract or port UI-neutral models, services, and shared contracts.
3. Port localization and resource access needed by the first vertical slice.
4. Port leaf views and their view models.
5. Port window lifecycle, dialogs, pickers, and UI-thread dispatch.
6. Port the main shell and application startup after its dependencies are ready.
7. Update packaging, installer, signing, and CI only if the chosen deployment model requires it.
8. Adapt tests and validate upgrade/activation scenarios.

After each slice, run only the repository-approved checks needed to prove that slice. Keep failures attributable to the current change.

## 5. Apply Semantic Mappings

Do not perform a repository-wide `System.Windows` → `Microsoft.UI.Xaml` replacement. Some names move directly; others change behavior or have no equivalent.

For state-dependent UI, choose visual states, direct binding, or a converter already used by the project before adding a custom converter.

Use these references on demand:

- [Namespace and API Mapping](references/namespace-api-mapping.md) — namespaces, types, events, controls, interop, and package boundaries.
- [XAML Migration](references/xaml-migration.md) — resources, bindings, templates, triggers, localization, styles, and WPF-only markup.
- [Threading and Windowing](references/threading-and-windowing.md) — `DispatcherQueue`, `Window`/`AppWindow`, dialogs, pickers, sizing, and custom entry points.
- [Imaging Migration](references/imaging-migration.md) — separating XAML image sources from image processing, WinRT codecs, streams, metadata, and compatibility.

## Migration Invariants

- Do not overwrite generated WinUI `App.xaml` or `App.xaml.cs`. Merge required resources, activation, and startup behavior into the target lifecycle.
- Do not change among packaged, packaged-with-external-location, or unpackaged deployment—or between framework-dependent and self-contained deployment—as an incidental compiler fix.
- Do not delete a package manifest from a packaged app, or add one to an unpackaged app, without an explicit deployment decision.
- Do not introduce CommunityToolkit.Mvvm, WinUIEx, a third-party grid, or another dependency solely because it appeared in a different migration. Reuse project conventions or justify the dependency from requirements.
- Do not treat `ThemeResource` as a general replacement for every `DynamicResource`; use it for theme-dependent resources, `StaticResource` for fixed resources, and binding/code for other dynamic values.
- Do not assume WPF preview events all disappear. WinUI supports `PreviewKeyDown` and `PreviewKeyUp`; pointer/mouse preview events require case-by-case redesign.
- Do not place `DataContext`, element resources, or visual states on a WinUI `Window`; put UI content in a `Page`, `UserControl`, or root element that supports those features.
- Do not assume `DispatcherQueue.TryEnqueue` is a synchronous `Dispatcher.Invoke` replacement. It returns whether enqueueing succeeded and does not directly propagate the callback result or exception.
- Do not silently change persisted enum values, JSON names, resource keys, CLI options, activation identifiers, or IPC/COM contracts.

## Completion Checklist

- [ ] Remaining WPF references are either removed or documented as intentional boundaries.
- [ ] Project properties match the chosen deployment and runtime model.
- [ ] Startup, activation, shutdown, window ownership, dialogs, and file pickers work through the supported path.
- [ ] XAML resources, Light/Dark/High Contrast themes, keyboard access, focus, and UI Automation semantics are preserved.
- [ ] Localization works in XAML and code without static-initialization failures.
- [ ] Settings, serialized data, command-line behavior, activation, and other external contracts remain compatible.
- [ ] Imaging output, metadata, orientation, alpha, color handling, and error cases are validated when imaging changed.
- [ ] Tests cover behavior rather than only proving that namespaces were replaced.
- [ ] Validate the intended AOT/trimmed publish output. Use `winui-dev-workflow` for Native AOT publish/run steps and `winui-packaging`'s `references/sourcegen-patterns.md` for trimming, binding, and serialization guidance. An ordinary Release build/run does not prove AOT or trimmed-publish compatibility.
- [ ] Repository-approved build/run/package checks pass, or skipped checks are reported with the governing restriction.
