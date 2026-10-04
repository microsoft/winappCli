---
name: winui-dev-workflow
description: "Build and run workflow for WinUI 3 apps with WinApp CLI 0.7+ — project creation with winapp new, per-app NuGet analyzer setup, project-mode winapp run, Native AOT publish runs, crash diagnosis, and prerequisites. Use when creating, building, running, or fixing build errors in a WinUI 3 project."
---

Requires **WinApp CLI 0.7+**.

### Create or Open a Project

**New app** — let WinApp CLI install/update the official templates and scaffold:
```powershell
winapp new --name <AppName> --template winui-mvvm --template-version latest --use-defaults
cd <AppName>
```
Run `winapp new --list` to discover the currently installed template short names. Do not install the template pack separately and do not create the output directory first.

**Existing app** — read the `.csproj` to understand:
- `<TargetFramework>` (e.g., `net10.0-windows10.0.26100.0`)
- `<PackageReference>` versions (WindowsAppSDK, CommunityToolkit)
- Project structure and established patterns

### Add or Check the Per-App Analyzer

Add the latest stable analyzer to each app project; do not assume a template includes it:
```powershell
dotnet add .\MyApp.csproj package Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer
```

Keep `PrivateAssets="all"` on the reference. It loads in normal CLI, IDE, and CI builds; WinApp CLI does not inject it. If the package is unavailable, continue and tell the user its checks for potential runtime issues did not run. Undo only an incomplete reference added by this attempt; do not remove existing references or hide other restore failures.

For other packages, prefer the latest stable unless the project has a version policy or the user requests a specific version. Before coding API assumptions, use `winapp find-api` scoped to the restored app with `--project-dir <app-project-dir>` (or `--project <name>` in a solution); see `winui-design`.

### Build & Run (JIT Development)

```powershell
winapp run . --detach --json
```
For UI testing, see `winui-ui-testing`, which chooses the execution target itself.

Ordinary `winapp run` uses the build/JIT path, **even with `-c Release`**; it does not validate Native AOT. Use an explicit `.csproj` when project selection is ambiguous; see `winapp run --help` for options.

**If build fails:** Read all errors, batch-fix them in one pass, then rerun the same command.

### Native AOT Publish Runs

For intended AOT deployment, set `<PublishAot>true</PublishAot>` in the app project (it also enables analysis during development); for a one-off trial, pass `-p PublishAot=true`. `--aot` publishes rather than builds; choose an `--arch` runnable on this machine:
```powershell
winapp run . --aot -c Release --arch <x64|arm64> --detach --json
winapp run . --aot -c Release --arch <x64|arm64> -p PublishAot=true --detach --json
```
Fix IL/CsWinRT warnings rather than suppressing them. See `winui-packaging`'s `references/sourcegen-patterns.md`.

### Diagnosing Crashes

Run attached with `--debug-output` and **invoke it with `mode: "async"`**, then read the same shell. On a WinUI crash, stowed-exception triage surfaces the real XAML error behind an opaque `0x8000FFFF` / `E_FAIL`; add `--symbols` for richer native frames. The first crash downloads debugger components; set `WINAPP_DBGTOOLS_DIR` to an existing *Debugging Tools for Windows* install when offline. `--debug-output` cannot combine with `--json` or `--no-launch`.

### Common Errors

| Error | Fix |
|-------|-----|
| Developer Mode not enabled | Settings → System → For developers → On |
| CS0234/CS0246 missing type | Add `using` or `dotnet add package` |
| NETSDK1136 platform required | Target a Windows TFM (for example `net10.0-windows10.0.26100.0`); use `-f <windows-tfm>` when the project already multi-targets |
| XLS0414 XAML type not found | Add `xmlns` declaration |
| XDG0062 binding path missing | Check `x:Bind` property exists on ViewModel |
| Dynamic bound value does not update | Check effective mode, including inherited `x:DefaultBindMode`; use `OneWay`/`TwoWay` and change notifications where needed |
| App silently exits | Use project-mode `winapp run`; don't bypass packaged activation by running the .exe directly |
| App crashes with opaque `0x8000FFFF` / `E_FAIL` | See **Diagnosing Crashes** |
| XAML compiler crashes silently | Remove any `PresentationCore.dll` / `System.Windows` references |
| MSB3073 / `XamlCompiler.exe ... exited with code 1`, no `.xaml` named | Old WindowsAppSDK XAML-compiler bug — update `Microsoft.WindowsAppSDK` NuGet to latest (≥ 2.1.3, or ≥ 1.8 on the 1.x line) |
| 0x80073CF6 package install failed | Check the manifest publisher and Developer Mode; apps from `winapp new` need no separate `winapp init` |
| 0x80073CF9 / "Failed to reach state Staged" on a deeply nested project | For a packaged app, rerun with `--output-appx-directory "$env:LOCALAPPDATA\winapp-layout\<app>-<config>-<arch>"`, or move the repo closer to the drive root. Keep the directory unique per configuration and architecture — a registered development package holds a live reference to it, so Debug and Release must not share one — and empty it before reuse so payload files dropped since the last build do not linger |
| 0x8007000B bad image format | Wrong platform target — use x64 or ARM64, not AnyCPU |

### Prerequisites

| Requirement | Required for this workflow |
|-------------|----------------------------|
| Windows | Windows 10 v1903+ and the app's OS requirements |
| Developer Mode | Enabled for development deployment |
| .NET SDK | 8.0.100 minimum **plus the SDK required by the app's TFM** (e.g., .NET 10 for `net10.0-windows…`) |
| WinApp CLI | 0.7+ |
| Native AOT only | MSVC C++ build tools (Visual Studio or Build Tools, **Desktop development with C++** workload, target-architecture tools); not needed for normal builds |

If WinApp CLI is missing or older than 0.7, install or upgrade it using `winui-setup` without asking (it needs no admin rights) and tell the user. Ask before installing anything that needs admin rights — the .NET SDK, Developer Mode, or the [Native AOT toolchain](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/); do not work around them.

### Critical Rules

- Keep **packaged** as the default and use project-mode `winapp run` for activation.
- Only for an **explicitly requested unpackaged/debug experiment**, set `WindowsPackageType=None`; package-identity-dependent APIs may fail and runtime requirements still apply. Do not use this as a silent launch workaround. Preserve the manifest and restore the original packaged setting after the experiment.
- Do not delete `Package.appxmanifest`.
- ❌ NEVER use `AnyCPU` — always x64 or ARM64

### References

- `winui-packaging` — release packaging directly from the project; no development registration required.
