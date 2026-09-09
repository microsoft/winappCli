# Microsoft.Windows.SDK.BuildTools.WinApp

Enables `dotnet run` for packaged Windows applications.

## Overview

This package provides MSBuild targets that seamlessly integrate with the .NET CLI, enabling developers to build and launch packaged Windows applications with a simple `dotnet run` command. Under the hood, it invokes `winapp run` to create a loose layout package, register it with Windows, and launch the app — simulating a full MSIX install for debugging.

## Features

- **Automatic Detection**: Detects when your project is a packaged WinUI/WinAppSDK application
- **Seamless Integration**: Hooks into the standard `dotnet run` pipeline, invoking `winapp run` automatically
- **Loose Layout Package**: Registers your build output as a loose layout package with Windows (like a real MSIX install)
- **Zero Configuration**: Works out of the box with standard WinUI project templates

## Usage

### Direct consumption (head app)

Add this package to your packaged Windows app project. `PrivateAssets="all"` is recommended so that downstream consumers of your app (if any) don't see this build tooling:

```xml
<PackageReference Include="Microsoft.Windows.SDK.BuildTools.WinApp" Version="<latest>" PrivateAssets="all" />
```

> Replace `<latest>` with the latest version from [the NuGet feed](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools.WinApp/).

Then run your application:

```bash
dotnet run
```

### Transitive consumption (library author)

If you ship a library that you want to expose this package's behavior to its consumers (e.g. a MAUI library wrapper), reference the package with `PrivateAssets="none"` and `IncludeAssets="build;buildTransitive"`:

```xml
<PackageReference Include="Microsoft.Windows.SDK.BuildTools.WinApp"
                  Version="<latest>"
                  PrivateAssets="none"
                  IncludeAssets="build;buildTransitive" />
```

The package ships its props and targets in both `build/` and `buildTransitive/`, so consumers of your library will pick them up automatically. The targets only activate when the consuming project is a packaged Windows app (has an appxmanifest, OutputType is not `Library`, target platform is `windows`); they are no-ops in unrelated TFMs (e.g. the `net*-android` / `net*-ios` TFMs of a multi-targeted MAUI app), libraries, and test projects.

## How It Works

When you run `dotnet run`, this package:

1. Builds your project normally
2. Detects if the project uses Windows App SDK with packaging
3. Prepares a loose-layout package in the output directory
4. Registers the package with Windows via `winapp run` (like a real MSIX install)
5. Launches the application using the Windows Application Activation Manager

## Requirements

- Windows 10 or later
- .NET 8.0 or later
- Windows App SDK 1.4 or later

## Configuration

Everything written after `dotnet run` is passed to **your application**, exactly as it would be
without this package. A standalone `--` is optional for arguments that do not collide with a
`dotnet run` option, because the .NET SDK consumes the separator before forwarding:

```powershell
dotnet run --devtools          # your app receives --devtools
dotnet run -- --devtools       # identical
```

Use `--` when your app's flag is also a `dotnet run` option (`--configuration`, `--framework`,
`--project`, `-c`, `-f`, `-r`, ...); otherwise the SDK claims it and your app never sees it:

```powershell
dotnet run -- --configuration Release   # your app receives --configuration Release
```

Configure the launcher itself with the MSBuild properties below, which MSBuild consumes so they never
reach your application:

```powershell
dotnet run -p:WinAppRunDetach=true --devtools
```

Set these MSBuild properties in your `.csproj` to customize behavior:

| Property | Default | Description |
|----------|---------|-------------|
| `EnableWinAppRunSupport` | `true` | Enable/disable the run support functionality |
| `WinAppLaunchArgs` | (empty) | Arguments to pass to the app on launch |
| `WinAppRunUseExecutionAlias` | inferred from the app | Launch via execution alias instead of AUMID activation. Left unset, winapp infers it: console apps use an alias so their output reaches the terminal, windowed apps use AUMID. Set `true` or `false` to decide it yourself. |
| `WinAppRunNoLaunch` | `false` | Only register identity without launching the app |
| `WinAppRunDebugOutput` | `false` | Capture `OutputDebugString` messages and first-chance exceptions. Only one debugger can attach at a time (prevents VS/VS Code). Use `WinAppRunNoLaunch` instead to attach a different debugger. Cannot be combined with `WinAppRunNoLaunch`. |
| `WinAppRunDetach` | `false` | Return immediately after launching instead of waiting for the app to exit. Prints the PID. |
| `WinAppRunUnregisterOnExit` | `false` | Unregister the development package after the app exits |
| `WinAppRunClean` | `false` | Remove the existing package's application data (LocalState, settings) before re-deploying |
| `WinAppRunSymbols` | `false` | Download symbols from the Microsoft Symbol Server for richer native crash analysis. Only has an effect with `WinAppRunDebugOutput`. |
| `WinAppRunExecutable` | (empty) | Executable path relative to the build-output folder. Use when the manifest contains `$targetnametoken$` and the output folder has more than one `.exe`. |
| `WinAppRunArgs` | (empty) | Raw arguments appended to the `winapp run` command line, for options with no dedicated property. Appended after every property above. |

**Mutually exclusive settings.** `WinAppRunNoLaunch` and `WinAppRunDetach` each describe a different launch behavior, so they conflict with the other launch properties and with each other:

| Property | Cannot be combined with |
|----------|-------------------------|
| `WinAppRunNoLaunch` | `WinAppRunDetach`, `WinAppRunDebugOutput`, `WinAppRunUnregisterOnExit` |
| `WinAppRunDetach` | `WinAppRunNoLaunch`, `WinAppRunDebugOutput`, `WinAppRunUnregisterOnExit` |

`WinAppRunUseExecutionAlias` is deliberately **not** in that list. Neither value conflicts: `false` asks for AUMID activation, which no-launch and detach already use, and `true` is simply not applied when either is set — an execution alias needs a tracked, running process. So a project that checks in `<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>` still runs cleanly under `dotnet run -p:WinAppRunDetach=true`, launching via AUMID rather than failing.

A conflicting pair fails the run with `--X and --Y cannot be used together`. The other three launch properties can be combined with each other, and `WinAppRunClean`, `WinAppRunSymbols`, `WinAppRunExecutable`, and `WinAppLaunchArgs` have no restrictions. `WinAppRunArgs` adds no restriction of its own, but a switch passed through it is checked like any other, so `WinAppRunArgs="--detach"` still conflicts with `WinAppRunNoLaunch`.

Example:

```xml
<PropertyGroup>
  <!-- Launch via execution alias so console I/O stays in the current terminal -->
  <WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>

  <!-- Capture OutputDebugString messages and first-chance exceptions -->
  <WinAppRunDebugOutput>true</WinAppRunDebugOutput>
</PropertyGroup>
```


## Troubleshooting

### `dotnet run` is not intercepted (app is not registered or launched)

If `dotnet run` runs your app as a plain executable instead of registering it as a packaged app, the package's run-support gate did not activate. Run the diagnostic target to see why:

```bash
dotnet msbuild -t:WinAppRunSupportInfo
```

The gate (`_WinAppRunSupportActive`) requires **all five** of these to be true:

1. `EnableWinAppRunSupport` is `true` (the default — set to `false` to disable explicitly).
2. `WindowsPackageType` is not `None`.
3. `OutputType` is not `Library` (must be `Exe`, `WinExe`, etc.).
4. `_WinAppEffectiveTargetPlatformIdentifier` is `windows` (derived from `$(TargetPlatformIdentifier)` if set, else from `$(TargetFramework)`).
5. A manifest exists in the project directory (`Package.appxmanifest`, `AppxManifest.xml`, or `appxmanifest.xml`) **or** you explicitly set `WinAppManifestPath` to a file that exists.

Look at the `WinAppRunSupportInfo` output — the property whose value disagrees with the list above is the one that's keeping the gate inactive.

### Application fails to launch

Ensure your `appxmanifest.xml` is correctly configured with:
- Valid Identity (Name, Publisher, Version)
- Valid Application entry (Id, Executable, EntryPoint)

### Debug identity registration fails

Run Visual Studio or the terminal as Administrator, or ensure Developer Mode is enabled in Windows Settings.

## Behavior changes

Starting with the version that introduced transitive consumption support (`buildTransitive/`):

- The package is no longer marked `<DevelopmentDependency>true`. This was required to allow the targets to flow transitively to consumers of a library that depends on this package. Direct head-app consumers using the default `PrivateAssets` setting will now also flow this package as a regular dependency in their own `.nupkg` output. **If you ship a packaged `.nupkg` and do not want consumers to inherit this build tooling, add `PrivateAssets="all"` to your `<PackageReference>` (see "Direct consumption" above).**
- The targets are now gated by a single property (`_WinAppRunSupportActive`) and are inert in any project that is not a packaged Windows app (libraries, test projects, console apps without a manifest, non-Windows TFMs of multi-targeted projects). If you previously relied on side effects from importing this package without meeting the gate criteria, those side effects no longer occur. Use the `WinAppRunSupportInfo` MSBuild target (see "Troubleshooting") to inspect the gate inputs.

## License

MIT License - see LICENSE file for details.
