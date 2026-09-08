# dotnet run Support for Packaged WinUI Apps

This document describes the implementation of `dotnet run` support for packaged WinUI applications using a custom NuGet package.

## Overview

The solution enables developers to run packaged WinUI (WinAppSDK) applications using just the .NET CLI:

```bash
winapp init
dotnet run
```

## Architecture

### Components

1. **Microsoft.Windows.SDK.BuildTools.WinApp** (NuGet Package)
   - Contains the WinAppCLI binary in `tools/` folder
   - Provides MSBuild targets that hook into `dotnet run`
   - Automatically detects packaged WinUI apps and handles launch

2. **WinAppCLI**
   - Handles debug identity registration
   - Launches packaged apps via Windows Application Activation Manager

### How It Works

```
dotnet run
    │
    ▼
MSBuild Build Target
    │
    ▼
_WinAppValidateRunSupport (validates prerequisites; gated by _WinAppRunSupportActive)
    │
    ▼
_WinAppPrepareRunArguments (overrides RunCommand with CLI path)
    │
    ▼
Run Target (invokes: winapp run --manifest ...)
    │
    ▼
WinAppCLI
    ├── Creates loose-layout package
    ├── Registers debug identity
    └── Launches via Application Activation Manager
```

## File Structure

```
src/
├── winapp-NuGet/                           # BuildTools.WinApp NuGet package
│   ├── Microsoft.Windows.SDK.BuildTools.WinApp.csproj
│   ├── README.md
│   ├── build/
│   │   ├── Microsoft.Windows.SDK.BuildTools.WinApp.props
│   │   └── Microsoft.Windows.SDK.BuildTools.WinApp.targets
│   └── tools/                              # CLI binaries (copied by build script)
│       ├── win-x64/
│       └── win-arm64/
│
samples/
└── winui-app/                              # Sample WinUI app for testing
```

## MSBuild Integration Details

### Properties (Microsoft.Windows.SDK.BuildTools.WinApp.props)

| Property | Default | Description |
|----------|---------|-------------|
| `EnableWinAppRunSupport` | `true` | Enable/disable the run support functionality |
| `WinAppManifestPath` | Auto-detected | Path to the AppxManifest file |
| `WinAppLooseLayoutPath` | `$(OutputPath)AppX\` | Output directory for loose-layout package |
| `WinAppLaunchArgs` | (empty) | Arguments to pass to the app on launch |
| `WinAppCliPath` | (in package) | Path to the winapp.exe CLI |
| `WinAppRunUseExecutionAlias` | `false` | Launch via execution alias instead of AUMID. Keeps console I/O in the current terminal. Requires `uap5:ExecutionAlias` in the manifest. Cannot be combined with `WinAppRunNoLaunch`. |
| `WinAppRunNoLaunch` | `false` | Only register package identity without launching the app. Cannot be combined with `WinAppRunUseExecutionAlias`. |
| `WinAppRunDebugOutput` | `false` | Attach as a debugger to capture `OutputDebugString` messages and first-chance exceptions. Only one debugger can attach at a time, so Visual Studio or VS Code cannot debug simultaneously. Use `WinAppRunNoLaunch` instead to attach a different debugger. Cannot be combined with `WinAppRunNoLaunch`. |
| `WinAppRunDetach` | `false` | Return immediately after launching instead of waiting for the app to exit. Prints the PID. |
| `WinAppRunUnregisterOnExit` | `false` | Unregister the development package after the app exits. Only removes packages registered in development mode. |
| `WinAppRunClean` | `false` | Remove the existing package's application data (LocalState, settings) before re-deploying. Application data is preserved by default. |
| `WinAppRunSymbols` | `false` | Download symbols from the Microsoft Symbol Server for richer native crash analysis. Only has an effect together with `WinAppRunDebugOutput`. |
| `WinAppRunExecutable` | (empty) | Executable path relative to the build-output folder. Use to disambiguate when the manifest contains a `$targetnametoken$` placeholder and the output folder contains more than one `.exe`. |
| `WinAppRunArgs` | (empty) | Raw arguments appended to the `winapp run` command line, for options that have no dedicated property. See [Escape hatch](#escape-hatch-winapprunargs). |

#### Mutually exclusive settings
`WinAppRunNoLaunch` and `WinAppRunDetach` each describe a different launch behavior — one never
starts the app, the other starts it and stops tracking it — so neither can be combined with the
properties that need a tracked, running process, nor with each other:

| Property | Cannot be combined with |
|----------|-------------------------|
| `WinAppRunNoLaunch` | `WinAppRunDetach`, `WinAppRunUseExecutionAlias`, `WinAppRunDebugOutput`, `WinAppRunUnregisterOnExit` |
| `WinAppRunDetach` | `WinAppRunNoLaunch`, `WinAppRunUseExecutionAlias`, `WinAppRunDebugOutput`, `WinAppRunUnregisterOnExit` |

The CLI rejects a conflicting pair before doing any work, so the run fails immediately:

```
> dotnet run -p:WinAppRunDetach=true -p:WinAppRunUnregisterOnExit=true
❌ --detach and --unregister-on-exit cannot be used together.
```

`WinAppRunUseExecutionAlias`, `WinAppRunDebugOutput`, and `WinAppRunUnregisterOnExit` can be combined
with each other. `WinAppRunClean`, `WinAppRunSymbols`, `WinAppRunExecutable`, and `WinAppLaunchArgs`
have no restrictions. `WinAppRunSymbols` is not rejected on its own, but only has an effect together
with `WinAppRunDebugOutput`. `WinAppRunArgs` adds no restriction of its own, but a switch passed
through it is checked like any other, so `WinAppRunArgs="--detach"` still conflicts with
`WinAppRunNoLaunch`.

#### Escape hatch: WinAppRunArgs

Every option `winapp run` accepts in folder mode has a dedicated property above. `WinAppRunArgs`
exists for the rest — the global options such as `--verbose`, and any option added to the CLI before
a property is wired up for it:

```powershell
dotnet run -p:WinAppRunArgs="--verbose"
```

It is appended **after** every property-derived switch, the same position `AdditionalOptions`
occupies in other toolsets:

```
run "<output>" --manifest "<manifest>" --detach --caller nuget-package --verbose
                                       ^ from WinAppRunDetach          ^ from WinAppRunArgs
```

Use it for options that have no property rather than to override one. Repeating a boolean switch is
harmless, but `winapp` rejects a scalar option supplied twice instead of letting the later value win:

```
> dotnet run -p:WinAppRunExecutable=a.exe -p:WinAppRunArgs="--executable b.exe"
Option '--executable' expects a single argument but 2 were provided.
```

### Targets (Microsoft.Windows.SDK.BuildTools.WinApp.targets)

| Target | Description |
|--------|-------------|
| `_WinAppValidateRunSupport` | Validates prerequisites (CLI exists, manifest exists) |
| `_WinAppBuildRunArgs` | Builds CLI command arguments (shared by run targets) |
| `_WinAppPrepareRunArguments` | Overrides RunCommand to use CLI |
| `RunPackagedApp` | Direct target to run packaged app |
| `WinAppRunSupportInfo` | Diagnostic target showing all properties |

### Detection Logic

The package only activates when **all** of the following are true (gated by the internal `_WinAppRunSupportActive` property):

1. `EnableWinAppRunSupport` is `true` (the default). Set it to `false` to explicitly disable.
2. `WindowsPackageType` is not set to `None` (absence of the property means packaged).
3. `OutputType` is not `Library` — both `Exe` (packaged console apps via execution alias) and `WinExe` (WinUI apps) are supported.
4. The target platform identifier is `windows` (derived from `$(TargetPlatformIdentifier)` if set, else from `$(TargetFramework)`). In multi-targeted projects (e.g. MAUI `net*-android;net*-ios;net*-windows10.0.19041.0`), the targets are inert for non-Windows TFMs.
5. `WinAppManifestPath` resolves to an existing file. The targets auto-detect the manifest by checking the output directory first (`$(OutputPath)AppxManifest.xml`, `$(OutputPath)Package.appxmanifest`, `$(OutputPath)appxmanifest.xml`) and then the project directory (`AppxManifest.xml`, `Package.appxmanifest`, `appxmanifest.xml`); a consumer-supplied `WinAppManifestPath` is honored as-is. Output-directory paths are accepted because frameworks like MAUI generate the manifest at build time into `$(OutputPath)` from platform / msbuild props; without that the gate could never activate for transitive MAUI head apps.

This gating ensures the package is safe to consume transitively (e.g. when re-exported by a library): unrelated projects (libraries, test projects, console apps without manifests, non-Windows TFMs) see no winapp activity and no impact on `dotnet run`.

## Build Scripts

### package-nuget.ps1

Creates all three NuGet packages — the `Microsoft.Windows.SDK.BuildTools.WinApp` tools package
described above, plus the two UI Automation library packages:

```powershell
.\scripts\package-nuget.ps1                    # Prerelease version
.\scripts\package-nuget.ps1 -Version 1.0.0 -Stable  # Stable version
.\scripts\package-nuget.ps1 -SkipCliPackage    # Only the UI Automation libraries
```

The tools package wraps the published CLI, so it needs `artifacts/cli` to hold an x64 and an arm64
build. The library packages build from source, so `-SkipCliPackage` produces them in seconds
without a full publish.

### Integration with build-cli.ps1

The main build script now includes NuGet packaging:

```powershell
.\scripts\build-cli.ps1                        # Full build including NuGet
.\scripts\build-cli.ps1 -SkipNuGet             # Skip NuGet packages
```

## Usage

### Argument routing

`dotnet run` behaves the same way whether or not a project references this package: **everything you
write after `dotnet run` is passed to your application.**

```powershell
dotnet run --devtools          # your app receives --devtools
dotnet run -- --devtools       # identical
dotnet run --detach            # your app receives --detach
```

A standalone `--` is optional for arguments that do not collide with a `dotnet run` option: the .NET
SDK consumes the separator while parsing its own command line and never re-emits it, so
`dotnet run --devtools` and `dotnet run -- --devtools` reach winapp as exactly the same token list.
(Mechanically, the targets end `RunArguments` with a separator, so every argument the SDK appends
lands in winapp's passthrough region.)

Use `--` when your application takes a flag that `dotnet run` itself defines — `--configuration`,
`--framework`, `--project`, `--no-build`, `-c`, `-f`, `-r`, `-v`, and friends. Without it the SDK
claims the token and your app never sees it:

```powershell
dotnet run --configuration Release      # the SDK builds Release; your app gets nothing
dotnet run -- --configuration Release   # your app receives --configuration Release
```

Configure the launcher itself with the `WinAppRun*` MSBuild properties, which MSBuild consumes:

```powershell
dotnet run -p:WinAppRunDetach=true --devtools
```

Here `-p:WinAppRunDetach=true` detaches the launcher and `--devtools` goes to your app.

> [!IMPORTANT]
> **This is a breaking change introduced in this release.** Options written directly after
> `dotnet run` used to configure WinApp, so `dotnet run --detach` detached the launcher; now it
> reaches your application instead. Move those to the matching property (`-p:WinAppRunDetach=true`).
> When winapp sees a forwarded argument that matches one of its own options, it prints the
> replacement:
>
> ```
> ℹ '--detach' was passed to your application, not to winapp.
>   To configure winapp, use -p:WinAppRunDetach=true instead.
> ```

### Customization

Disable run support for a project:
```xml
<PropertyGroup>
  <EnableWinAppRunSupport>false</EnableWinAppRunSupport>
</PropertyGroup>
```

Specify manifest path:
```xml
<PropertyGroup>
  <WinAppManifestPath>$(MSBuildProjectDirectory)\custom\Package.appxmanifest</WinAppManifestPath>
</PropertyGroup>
```

Pass launch arguments:
```xml
<PropertyGroup>
  <WinAppLaunchArgs>--debug --verbose</WinAppLaunchArgs>
</PropertyGroup>
```

Launch via execution alias (for console apps):
```xml
<PropertyGroup>
  <WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>
</PropertyGroup>
```

Register identity without launching:
```xml
<PropertyGroup>
  <WinAppRunNoLaunch>true</WinAppRunNoLaunch>
</PropertyGroup>
```


Capture OutputDebugString messages and first-chance exceptions:
```xml
<PropertyGroup>
  <WinAppRunDebugOutput>true</WinAppRunDebugOutput>
</PropertyGroup>
```

## Production Blockers

### 1. Developer Mode Requirement

Running packaged apps requires Developer Mode enabled on Windows. The solution should:
- Detect when Developer Mode is disabled
- Provide clear error messages
- Consider documenting this requirement prominently

### 2. First-run Experience

On first `dotnet run`, the CLI needs to:
- Download Windows SDK Build Tools (if not cached)
- This can take time on slow connections

Consider pre-caching or documenting this.

### 3. Platform Detection

The current implementation defaults to x64. For ARM64 machines, the targets correctly detect architecture, but the default Platform may need adjustment.

## Testing

### Local Testing (without published NuGet)

The sample project imports the MSBuild targets directly:

```xml
<Import Project="..\..\src\winapp-NuGet\build\Microsoft.Windows.SDK.BuildTools.WinApp.props" />
<Import Project="..\..\src\winapp-NuGet\build\Microsoft.Windows.SDK.BuildTools.WinApp.targets" />
```

### Diagnostic Commands

```bash
# Show MSBuild property values
dotnet msbuild -t:WinAppRunSupportInfo

# Verbose build output
dotnet run -v:detailed
```

## Future Enhancements

1. **Hot Reload Support**: Integrate with `dotnet watch` for live reloading
2. **Debug Attachment**: Return process ID for debugger attachment in IDEs
3. **Unpackaged Mode**: Auto-detect and use unpackaged mode when appropriate
4. **Multiple Apps**: Support projects with multiple Application entries in manifest
