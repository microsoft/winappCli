<!-- mslearn: true -->
<!-- ms.topic: how-to -->
<!-- description: Create, run, debug, automate, and package WinUI 3 apps from the command line with winapp CLI, from a new template to a signed MSIX. -->
# Using winapp CLI with WinUI

This guide shows how to use winapp CLI across the full inner loop of a WinUI 3 app: create a project from an official template, build and launch it with package identity, debug crashes, find controls and APIs, automate the UI, and produce a signed MSIX package. Every step runs from the command line, so it works in any editor, in CI, and with AI coding agents.

WinUI project templates already include a `Package.appxmanifest`, so you don't need to run `winapp init` for a WinUI app. If you're adding package identity to an existing .NET app that doesn't use WinUI, see [Using winapp CLI with .NET](dotnet.md) instead.

## Prerequisites

- Windows 10 version 1809 or later, or Windows 11.
- The [.NET SDK](https://dotnet.microsoft.com/download), version 8.0.100 or later. The experimental Reactor templates require the .NET 10 SDK. winapp CLI doesn't install toolchains, so `winapp new` and `winapp run` stop with guidance if the .NET SDK is missing.
- winapp CLI:

  ```powershell
  winget install Microsoft.winappcli --source winget
  ```

- [Developer Mode](https://learn.microsoft.com/windows/advanced-settings/developer-mode), which Windows requires to register the app from your build output. Turn it on in **Settings > System > For developers**. For what this changes on your machine, see [Security](../security.md#developer-mode).

## Create a WinUI app

`winapp new` scaffolds a WinUI app from the official `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` pack. It installs or updates the template pack on demand and then calls `dotnet new` for you.

```powershell
# Pick a template and a name interactively
winapp new

# Or create a NavigationView app without prompts
winapp new --name MyApp --template winui-navview --use-defaults
```

Run `winapp new --list` to see the templates in the installed pack. The pack includes these templates:

| Template | Description |
|----------|-------------|
| `winui` | Minimal blank XAML app (default). [See screenshot](https://learn.microsoft.com/windows/apps/dev-tools/visual-studio#winui-blank-app-packaged) |
| `winui-navview` | XAML app with a NavigationView shell. [See screenshot](https://learn.microsoft.com/windows/apps/dev-tools/visual-studio#winui-navigation-app) |
| `winui-tabview` | XAML app with a TabView shell. [See screenshot](https://learn.microsoft.com/windows/apps/dev-tools/visual-studio#winui-tabview-app) |
| `winui-mvvm` | XAML app that uses the MVVM pattern with CommunityToolkit.Mvvm. [See screenshot](https://learn.microsoft.com/windows/apps/dev-tools/visual-studio#winui-mvvm-app) |
| `winui-lib` | WinUI class library |
| `winui-unittest` | Packaged MSTest project for WinUI unit tests |
| `reactor`, `reactor-mvu`, `reactor-navview`, `reactor-tabview` | Experimental. WinUI apps written in C# only, with no XAML |

> [!NOTE]
> The Reactor templates reference prerelease `Microsoft.UI.Reactor` packages. Their APIs can change or be removed in a future release.

For every option, see the [`new` command reference](../usage.md#new).

## Build and run with identity

From the project folder, `winapp run` builds the project, installs the Windows App SDK runtime that matches your architecture if it's missing, registers the app with package identity, and launches it.

```powershell
cd MyApp
winapp run
```

`winapp run` accepts a project file, a solution file, a folder that contains one of them, or a folder of build output:

```powershell
# A specific project and configuration
winapp run .\MyApp.csproj -c Release --arch arm64

# A solution: winapp CLI finds the single app project and skips libraries and tests
winapp run .\MySolution.sln

# A solution with more than one app project
winapp run .\MySolution.sln --project MyApp

# Build output that you produced yourself
winapp run .\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64
```

winapp CLI reads the project's `WindowsPackageType` property to decide how to launch it. A packaged project runs with package identity. A project that sets `WindowsPackageType` to `None` runs as an unpackaged app without identity. To run a packaged project as unpackaged, pass the property on the command line:

```powershell
winapp run -p WindowsPackageType=None
```

Other useful options include `--clean` to remove the previous registration and the app's saved data (LocalState and settings) for a fresh first run, `--no-launch` to register without starting the app, and `--detach --json` to return the process ID and exit so that scripts and agents can keep working while the app runs. For the full list, see [Project mode (.NET SDK projects)](../usage.md#project-mode-net-sdk-projects).

### Use dotnet run instead

If you prefer `dotnet run`, use the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package. The package hooks `dotnet run` so that it calls `winapp run` and launches the app with identity. Projects that you create with `winapp new` already reference the package, so `dotnet run` works without extra setup. For other projects, add the package first:

```powershell
dotnet add package Microsoft.Windows.SDK.BuildTools.WinApp --prerelease
dotnet run
```

You can pass `winapp run` options as `WinAppRun*` MSBuild properties, for example `dotnet run -p:WinAppRunDebugOutput=true`. To turn off the integration, set `EnableWinAppRunSupport` to `false` in the project file. For details, see [dotnet run support](../dotnet-run-support.md).

## Debug crashes

Add `--debug-output` to capture `OutputDebugString` messages and first-chance exceptions while the app runs. When a WinUI app fails with a stowed exception, winapp CLI analyzes the crash dump and prints the original error and call stack instead of a generic failure code.

```powershell
winapp run --debug-output
```

Add `--symbols` to download symbols for full native function names. Because `--debug-output` attaches winapp CLI as the debugger, you can't attach Visual Studio or another debugger to the same process. To debug with your IDE instead, see [Debugging with winapp CLI](../debugging.md). For details about the stowed-exception analysis, see [WinUI stowed-exception triage](../debugging.md#winui-stowed-exception-triage).

## Find controls and APIs

`winapp find-ui` searches XAML and C# samples from WinUI Gallery and the Windows Community Toolkit by intent, and returns the code for a scenario that you pick. `winapp find-api` searches the Windows and WinRT API surface that your project references, such as Windows App SDK and Windows SDK types, members, and enums.

```powershell
# Find a control by what you want to build
winapp find-ui "tabbed layout"

# Get the XAML and C# for one of the returned scenarios
winapp find-ui --id gallery-tabview-1

# Look up an API
winapp find-api NavigationView
```

Both commands support `--json`, so AI coding agents can use them to choose controls and verify API names. For more information, see the [`find-ui`](../usage.md#find-ui) and [`find-api`](../usage.md#find-api) references.

## Automate and verify the UI

`winapp ui` drives a running app through Microsoft UI Automation. Use it to inspect the element tree, invoke controls, wait for state changes, and take screenshots. Agents can use the same commands to check their own changes.

```powershell
winapp run --detach
winapp ui inspect -a MyApp
winapp ui search Button -a MyApp
winapp ui invoke PART_PaneToggleButton -a MyApp
winapp ui screenshot -a MyApp
```

The `PART_PaneToggleButton` selector targets the menu button in the `winui-navview` template. For your own controls, copy a selector from the output of `winapp ui inspect` or `winapp ui search`.

To run the app in Windows Sandbox instead of on your desktop, add `--on sandbox` to `winapp run` and `winapp ui`. For more information, see [UI automation](../ui-automation.md) and [Sandbox execution](../sandbox-execution.md).

## Package and sign

When you're ready to test a package locally, generate a development certificate whose publisher matches your manifest, and then build and package the project in one step:

```powershell
winapp cert generate --manifest .\Package.appxmanifest
winapp pack .\MyApp.csproj -c Release --arch x64 --cert .\devcert.pfx
```

`winapp pack` builds the project and produces a signed `.msix`. To combine builds for several architectures into one `.msixbundle`, see [Multi-architecture bundles](../usage.md#multi-architecture-bundles). The project must be packaged, so a project that sets `WindowsPackageType` to `None` can't be packed. For WinUI projects, the Windows App SDK build owns the manifest, the entry point, and resource generation, so `--manifest`, `--executable`, and `--skip-pri` aren't accepted.

The development certificate is for local testing only, and it uses a default password. Before you distribute your app, sign it with a trusted certificate, for example with [`winapp az-sign`](../usage.md#az-sign) and Azure Trusted Signing. For more information, see [Packaging a project directly](../usage.md#packaging-a-project-directly) and [Security](../security.md#the-default-password).

## Existing WinUI projects

winapp CLI works with WinUI projects that you created in Visual Studio. Point `winapp run` at the project file, the solution file, or the folder that contains one. For `winapp pack`, pass the `.csproj`. You can keep building and debugging in Visual Studio and use winapp CLI for scripts, CI, and agent workflows.

## WinUI with C++

`winapp run` also builds and launches C++/WinRT WinUI projects (`.vcxproj`) that you created from the Visual Studio **WinUI Blank App (Packaged)** template. It builds the project with Visual Studio's MSBuild, registers the app with package identity, and launches it:

```powershell
winapp run .\MyApp.vcxproj

# Or from the folder that contains the project
winapp run .
```

C++ projects need Visual Studio or Build Tools for Visual Studio with the **Desktop development with C++** workload and **C++ WinUI app development tools**. `winapp pack` doesn't build `.vcxproj` projects, so create the MSIX package in Visual Studio. For prerequisites and options, see [C++ projects (.vcxproj)](../usage.md#c-projects-vcxproj). For C++ apps that don't use WinUI, see [Using winapp CLI with C++](cpp.md).

## Samples

- [`winui-app`](../../samples/winui-app): a packaged WinUI app with controls set up for `winapp ui` automation
- [`winui-unpackaged-app`](../../samples/winui-unpackaged-app): a WinUI app that runs without package identity
- [`winui-solution`](../../samples/winui-solution): a multi-project solution that shows how `winapp run` picks the app project
- [`cpp-winui-app`](../../samples/cpp-winui-app): a packaged C++/WinRT WinUI app (Visual Studio `.vcxproj` with XAML) that runs with `winapp run`

## Next steps

- [Get started with WinUI](https://learn.microsoft.com/windows/apps/get-started/start-here)
- [winapp CLI command reference](../usage.md)
- [Debugging with winapp CLI](../debugging.md)
- [UI automation](../ui-automation.md)
