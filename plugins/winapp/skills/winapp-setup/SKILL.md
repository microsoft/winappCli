---
name: winapp-setup
description: Add Windows SDK and Windows App SDK support to an existing project (winapp init), restore pinned SDKs after cloning (winapp restore), update SDKs, or create a new WinUI app (winapp new). Use for Electron, .NET, C++, Rust, Flutter, or Tauri projects that need Windows setup.
---

**If you can't run `winapp` yourself** (no shell, or the command is denied), give the user the exact `winapp` command(s) for their project instead of only describing the steps.

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

### Restore after cloning

```powershell
# Reinstall SDK packages from existing winapp.yaml (does not change versions)
winapp restore

# Restore into a specific directory
winapp restore ./my-project
```

Use `restore` when you clone a repo that already has `winapp.yaml` but no `.winapp/` folder. For a .NET project there is no `winapp.yaml`, so `restore` runs `dotnet restore` instead.

### Update SDK versions

```powershell
# Check for and install latest stable SDK versions
winapp update

# Switch to preview channel
winapp update --setup-sdks preview
```

This updates `winapp.yaml` with the latest versions and reinstalls packages. If the newest Windows App SDK release is still being published (some of its packages aren't on the feed yet), `update` uses the previous release instead and prints a note; run `winapp update` again later to pick up the new release.

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

## Load when

| Read | When |
|---|---|
| `references/nuget-feeds.md` | SDK download needs a private feed, mirror, or credentials (401/403) |

## Related skills

- `winapp-frameworks` — framework specifics
- `winapp-identity` — `winapp run` details (project mode, `.vcxproj`, single-file `.cs` apps) and debugging with identity
- `winapp-manifest`, `winapp-package`, `winapp-signing`, `winapp-troubleshoot`

Run `winapp <command> --help` for current command options, or `winapp --cli-schema` for the complete machine-readable command schema.
