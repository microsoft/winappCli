---
name: winapp-setup
description: Set up a Windows app project for MSIX packaging, Windows SDK access, or Windows API usage, or scaffold a brand-new WinUI 3 app. Use when creating or scaffolding a new WinUI app from scratch, adding Windows support to an Electron, .NET, C++, Rust, Flutter, or Tauri project, or restoring SDK packages after cloning.
---
## When to use

Use this skill when:
- **Scaffolding a brand-new WinUI 3 app** from an official Windows App SDK template (`winapp new`)
- **Adding Windows platform support** to an existing project (Electron, .NET, C++, Rust, Flutter, Tauri, etc.)
- **Cloning a repo** that already uses winapp and need to restore SDK packages
- **Updating SDK versions** to get the latest Windows SDK or Windows App SDK

## Prerequisites

Install the winapp CLI before running any commands:

```powershell
# Via winget (recommended for non-Node projects)
winget install Microsoft.WinAppCli --source winget

# Via npm (recommended for Electron/Node projects — includes Node.js SDK)
npm install --save-dev @microsoft/winappcli
```

You need an **existing app project** — `winapp init` does **not** create new projects, it adds Windows platform files to your existing codebase.

> **Already have a `Package.appxmanifest`?** .NET projects that already have a packaging manifest (e.g., WinUI 3 apps or projects with an existing MSIX packaging setup) likely **don't need `winapp init`**. Ensure your `.csproj` references the `Microsoft.WindowsAppSDK` NuGet package and has the right properties for packaged builds (e.g., `<WindowsPackageType>MSIX</WindowsPackageType>`). WinUI 3 apps created from Visual Studio templates are typically already fully configured — you can go straight to building and using `winapp run` or `winapp package`.

## Key concepts

**`Package.appxmanifest`** is the most important file winapp creates — it declares your app's identity, capabilities, and visual assets. Most winapp commands require it (`package`, `run`, `cert generate --manifest`).

**`winapp.yaml`** is only needed for SDK version management via `restore`/`update`. Projects that already reference Windows SDK packages (e.g., via NuGet in a `.csproj`) can use winapp commands without it.

**`.winapp/`** is the local folder where SDK packages and generated projections (e.g., CppWinRT headers) are stored. This folder is `.gitignore`d — team members recreate it via `winapp restore`.

## Usage

### Create a new WinUI app

To start a brand-new **WinUI** app (rather than adding Windows support to an existing project), use `winapp new`. It verifies the .NET SDK, installs the official WinUI `dotnet new` template pack on demand (grabbing the latest, or offering to update a stale one), and scaffolds the app against your installed SDK's target framework. Most WinUI templates already include packaging/identity, so **no `winapp init` step is needed** afterward — follow the template-specific next step `winapp new` prints when it finishes. App templates go straight to `winapp run` (which builds and launches the app); the `winui-lib` (class library) and `winui-unittest` templates differ (reference the library from an app project, or `winapp run` the packaged test app to run its tests). The template list is read live from the installed pack — run `winapp new --list` to see the current set.

The pack ships two styles of app. **XAML** templates (`winui`, `winui-navview`, `winui-tabview`, `winui-mvvm`) define the UI in markup with a C# code-behind. **Reactor** templates (`reactor`, `reactor-mvu`, `reactor-navview`, `reactor-tabview`) are pure C# with no XAML, using an MVU pattern.

> **Reactor templates are experimental.** They reference the prerelease `Microsoft.UI.Reactor` packages, whose APIs can change or be removed in a future release — don't pick one unless the user explicitly asks for Reactor. `winapp new` marks them **(Experimental)** in `--list` and in the picker, reports `"Experimental": true` in `--json`, and never selects one as the default. They also require the **.NET 10 SDK or newer**; on an older SDK `winapp new` fails up front naming the version it needs.

> A first run, template-pack update, or newly published Windows App SDK version may take longer while missing NuGet packages download and restore. If scaffolding continues beyond 10 seconds, `winapp new` updates its status message rather than silently waiting.

```powershell
# Interactive — pick a template, then name/output
winapp new

# See the available templates without scaffolding
winapp new --list

# One-shot with a specific template (short names come from `winapp new --list`)
winapp new --name MyApp --template winui-navview

# Experimental Reactor app (pure C#, no XAML) — requires the .NET 10 SDK
winapp new --name MyApp --template reactor-mvu

# Diagnose a failed scaffold: --verbose streams dotnet new's post-creation actions
# (restore, package add, etc.) live so the underlying dotnet error is visible
winapp new --name MyApp --verbose

# Always use the newest template pack without prompting
winapp new --name MyApp --template-version latest --use-defaults

# Non-interactive (agent) with machine-readable output
winapp new --use-defaults --name MyApp --json
```

### Initialize a new winapp project

```powershell
# Interactive — prompts for app name, publisher, SDK channel, etc.
# Automatically searches for compatible projects (Tauri, Electron, .NET, Rust, C++, Flutter)
winapp init

# Non-interactive — accepts all defaults (stable SDKs, current folder name as app name)
winapp init . --use-defaults

# Non-interactive with JS bindings enabled
winapp init . --use-defaults --add-js-bindings

# Skip SDK installation (just manifest + config)
winapp init . --use-defaults --setup-sdks none

# Install preview SDKs instead of stable
winapp init . --use-defaults --setup-sdks preview
```

After `init`, your project will contain:
- `Package.appxmanifest` — package identity and capabilities
- `Assets/` — default app icons (Square44x44Logo, Square150x150Logo, etc.)
- `winapp.yaml` — SDK version pinning for `restore`/`update`
- `.winapp/` — downloaded SDK packages and generated projections
- `.gitignore` update — excludes `.winapp/` and `devcert.pfx`

When JS bindings are enabled (via `--add-js-bindings` or by answering yes in interactive init), npm/Electron projects also get:
- `.winapp/bindings/` — generated JS bindings for Windows App SDK APIs (npm-only, Node / Electron)
- `package.json` update — adds the `winapp.jsBindings` namespace and `@microsoft/dynwinrt` dependency (npm-only)

### Initialize a sparse identity package (existing exe)

Use `--sparse` when you have an **already-built desktop exe** (WPF, WinForms, Win32, Electron, etc.) and only want to give it **package identity** — without repackaging the whole app into the MSIX. The app's files stay where they are and are resolved from an *external content location* at runtime.

```powershell
# Generate an identity-only sparse manifest for an existing exe
winapp init --exe ./bin/Release/MyApp.exe --sparse

# Non-interactive, with explicit identity values
winapp init --exe ./bin/Release/MyApp.exe --sparse --name MyApp --publisher "CN=Contoso" --use-defaults
```

`--sparse` requires `--exe`. It skips all SDK/package installation (sparse identity packages have no SDK dependencies) and, by default, writes to a dedicated `sparse/` folder in the current directory (override with `--output-dir`) so the manifest and its `Assets/` stay out of a build-output folder that a rebuild would wipe:
- `appxmanifest.xml` — identity-only sparse manifest (declares `uap10:AllowExternalContent`)
- `Assets/` — placeholder visual assets (extracted from the exe's icon when possible), resolved from the **external location** at runtime — **not** bundled into the `.msix`

If an `appxmanifest.xml` already exists in the target directory, init fails instead of overwriting it; re-run with `--force` to regenerate.

This is step 1 of the sparse packaging workflow. Continue with:
1. `winapp pack ./sparse/appxmanifest.xml --cert ./devcert.pfx` — build the signed identity `.msix`
2. `winapp embed-identity ./bin/Release/MyApp.exe` — connect the exe to the identity package (re-sign the exe afterward)
3. Register in your installer with `Add-AppxPackage -Path <msix> -ExternalLocation <install-dir>`

For the full walkthrough, see the [Sparse packaging guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/sparse.md).

### Restore after cloning

```powershell
# Reinstall SDK packages from existing winapp.yaml (does not change versions)
winapp restore

# Restore into a specific directory
winapp restore ./my-project
```

Use `restore` when you clone a repo that already has `winapp.yaml` but no `.winapp/` folder. For a .NET project there is no `winapp.yaml`, so `restore` runs `dotnet restore` instead.

### Private or custom NuGet feeds

`init`, `restore`, and `update` download the SDK packages through NuGet, honoring your standard `nuget.config` hierarchy. Private feeds and mirrors, feed credentials (including credential providers), and a custom `globalPackagesFolder` all work as they do for `dotnet restore`. To use only your feed, `<clear />` the inherited sources first:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="contoso" value="https://pkgs.dev.azure.com/contoso/_packaging/winsdk-mirror/nuget/v3/index.json" />
  </packageSources>
</configuration>
```

> **Security note:** For native projects winapp resolves `nuget.config` from the directory it operates on: the `init`/`restore` directory argument, `--config-dir` when given, otherwise the current directory. For **.NET projects** the sources come from the project's own `nuget.config` hierarchy instead, because that is what `dotnet add package` and `dotnet restore` use — so put a private feed's config in the project directory or an ancestor, not in a sibling passed via `--config-dir` (that is reported and ignored). Run these commands only against directories you trust, the same as `dotnet restore`. Use `<packageSourceMapping>` to pin packages to specific feeds when more than one source is configured.

### Update SDK versions

```powershell
# Check for and install latest stable SDK versions
winapp update

# Switch to preview channel
winapp update --setup-sdks preview
```

This updates `winapp.yaml` with the latest versions and reinstalls packages.

### Run and debug with identity

```powershell
# Register debug identity and launch app from build output
winapp run ./bin/Debug

# Launch with custom manifest and pass arguments to the app
winapp run ./dist --manifest ./out/Package.appxmanifest --args "--my-flag value"

# Pass arguments after -- to avoid escaping (equivalent to --args)
winapp run ./bin/Debug -- --my-flag value

# Register identity without launching (useful for attaching a debugger manually)
winapp run ./bin/Debug --no-launch

# Launch and capture OutputDebugString messages and crash diagnostics
# Note: prevents other debuggers (VS, VS Code) from attaching — use --no-launch if you need those instead
winapp run ./bin/Debug --debug-output
```

Use `winapp run` during iterative development — it creates a loose layout package, registers a debug identity, and launches the app in one step. For identity-only registration without loose layout, use `winapp create-debug-identity` instead.

#### Project mode: `winapp run` on a `.csproj` (.NET / WinUI)

For .NET SDK projects you can point `winapp run` **at the project instead of the build output** — it builds the `.csproj` and launches it in one step, so there's no separate `dotnet build` and no need to know the output path:

```powershell
# Build and run the project in the current directory (input defaults to ".")
winapp run

# Run a specific project, configuration, and architecture
winapp run ./src/MyApp/MyApp.csproj -c Release --arch arm64

# Force an unpackaged run of a packaged project
winapp run . -p WindowsPackageType=None

# Show winapp's build decision traces (dotnet build stays at minimal verbosity)
winapp run . --verbose
```

Project mode supports both **packaged** and **unpackaged** WinUI apps, detected from the project's effective `WindowsPackageType` (`MSIX` ⇒ loose-layout register + AUMID launch; `None` ⇒ launch the built `.exe`), and installs the matching-architecture Windows App Runtime before launching. Requires .NET SDK 8.0.100+.

- **Build inputs:** `-c/--configuration`, `--arch`, `-r/--runtime`, `-f/--framework`, `--no-build`, `--no-restore`, `-p/--property` (repeatable).
- **Packaged-only options:** `--manifest`, `--no-launch`, `--with-alias`, `--clean`, `--unregister-on-exit`, `--output-appx-directory`, `--executable` — rejected for unpackaged apps.
- **Output:** winapp prints the exact `dotnet build …` invocation, then streams build output live (warnings included on success). Under `--json`/`--quiet` both go to **stderr** so stdout stays clean.

#### Choosing between `run` and `create-debug-identity`

| | `winapp run` | `create-debug-identity` |
|---|---|---|
| **Registers** | Full loose layout package (entire folder) | Sparse package (single exe) |
| **App launch** | Winapp launches via AUMID or alias | You launch the exe yourself |
| **Simulates MSIX** | Yes — closest to production | No — identity only |
| **Files** | Copied to AppX layout dir | Exe stays in place |
| **Best for** | Most frameworks (.NET, C++, Rust, Flutter, Tauri) | Electron, or F5 startup debugging |

**Default to `winapp run`.** Use `create-debug-identity` when you need your IDE to launch and debug the exe directly (startup debugging), or when the exe is separate from your source (Electron).

For console apps, add `--with-alias` to preserve stdin/stdout in the current terminal.

> **`--debug-output` caveat:** Captures `OutputDebugString` and crash diagnostics (minidump + automatic analysis for both managed and native crashes) but attaches winapp as the debugger — you cannot also attach VS Code or WinDbg. Use `--no-launch` if you need your own debugger. Add `--symbols` to download PDB symbols for richer native crash analysis. For WinUI 3 apps, a stowed-exception triage pass runs automatically (surfacing the originating HRESULT and native XAML dispatch stack); the debugger components it needs are downloaded on first use, or set `WINAPP_DBGTOOLS_DIR` to a directory containing `dbgeng.dll` and `JsProvider.dll` for offline/locked-down environments.

For full debugging scenarios and IDE setup, see the [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md).

## Recommended workflow

1. **Initialize** — `winapp init . --use-defaults` in your existing project
2. **Configure** — edit `Package.appxmanifest` to add capabilities your app needs (e.g., `runFullTrust`, `internetClient`)
3. **Build** — build your app as usual (dotnet build, cmake, npm run build, etc.)
4. **Run with identity** — `winapp run ./bin/Debug` to register identity and launch for debugging
5. **Package** — `winapp package ./bin/Release --cert ./devcert.pfx` to create MSIX

## Tips

- Use `--use-defaults` (alias: `--no-prompt`) in CI/CD pipelines and scripts to avoid interactive prompts. Non-interactive environments (piped stdin, CI runners) are auto-detected and will use defaults automatically with a warning.
- If you only need `Package.appxmanifest` without SDK setup, use `winapp manifest generate` instead of `init`
- `winapp init` is idempotent for the config file — re-running it won't overwrite an existing `winapp.yaml` unless you use `--config-only`
- For Electron projects, prefer `npm install --save-dev @microsoft/winappcli` and use `npx winapp init` instead of the standalone CLI

## Related skills
- After setup, see `winapp-manifest` to customize your `Package.appxmanifest`
- Ready to package? See `winapp-package` to create an MSIX installer
- Need a certificate? See `winapp-signing` for certificate generation
- Not sure which command to use? See `winapp-troubleshoot` for a command selection flowchart

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "winapp.yaml not found" | Running `restore`/`update` without config | Run `winapp init` first, or ensure you're in the right directory |
| "Directory not found" | Target directory doesn't exist | Create the directory first or check the path |
| SDK download fails | Network issue or firewall | Ensure internet access; check proxy settings |
| SDK download fails with 401/403 | Private feed requires authentication | Store credentials in `nuget.config` (`<packageSourceCredentials>`) or configure a credential provider / feed environment credentials before running in CI |
| SDK package not found on private feed | Feed doesn't mirror the SDK packages, or the wrong source is configured | Ensure the feed serves `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.CPP`, `Microsoft.Windows.CppWinRT`, etc.; keep `nuget.org` enabled if the feed only supplements it |
| `init` prompts unexpectedly in CI | Missing `--use-defaults` flag | Add `--use-defaults` to skip all prompts (note: non-interactive shells are now auto-detected) |
| `winapp new` fails during scaffolding | A `dotnet new` post-creation action (restore, package add) failed | Re-run with `--verbose` to stream the live dotnet output and see the underlying error |

## CLI reference

Run `winapp <command> --help` for current command options, or `winapp --cli-schema` for the complete machine-readable command schema.
