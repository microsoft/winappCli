---
name: winapp-identity
description: Enable Windows package identity for desktop apps to access Windows APIs like push notifications, background tasks, share target, and startup tasks. Use when adding Windows notifications, background tasks, or other identity-requiring Windows features to a desktop app.
---
## When to use

Use this skill when:
- **The exe is separate from your app code** — e.g., Electron apps where `electron.exe` is in `node_modules`, not your build output
- **Testing sparse package behavior** specifically — `AllowExternalContent`, `TrustedLaunch`, etc.
- **Registering identity without copying files** — `create-debug-identity` leaves the exe in place

> **Prefer `winapp run` for most frameworks.** If your exe is inside your build output folder (.NET, C++, Rust, Flutter, Tauri), use `winapp run <build-output>` instead — it registers a full loose layout package and launches the app, simulating an MSIX install. Use `create-debug-identity` only when `winapp run` doesn't fit your scenario.

## Prerequisites

1. **`Package.appxmanifest`** in your project — from `winapp init` or `winapp manifest generate`
2. **Built executable** — the `.exe` your app runs from

## What is package identity?

Windows package identity enables your app to use restricted APIs and OS integration features:
- **Push notifications** (WNS)
- **Background tasks**
- **Share target** / share source
- **App startup tasks**
- **Taskbar pinning**
- **Windows AI APIs** (Phi Silica, OCR, etc.)
- **File type associations** registered properly in Settings

A standard `.exe` (from `dotnet build`, `cmake`, etc.) does **not** have identity. `create-debug-identity` registers a *sparse package* with Windows — the exe stays in its original location and Windows associates identity with it via `Add-AppxPackage -ExternalLocation`. This is different from `winapp run`, which copies files into a loose layout package.

## Usage

### Basic usage

```powershell
# Register sparse package for your exe (manifest auto-detected from current dir)
winapp create-debug-identity ./bin/Release/myapp.exe

# Specify manifest location
winapp create-debug-identity ./bin/Release/myapp.exe --manifest ./Package.appxmanifest
```

### Keep the original package identity

```powershell
# By default, '.debug' is appended to the package name to avoid conflicts with
# an installed MSIX version. Use --keep-identity to keep the manifest identity as-is.
winapp create-debug-identity ./myapp.exe --keep-identity
```

### Generate without installing

```powershell
# Create the sparse package layout but don't register it with Windows
winapp create-debug-identity ./myapp.exe --no-install
```

## What the command does

1. **Reads `Package.appxmanifest`** — extracts identity, capabilities, and assets
2. **Creates a sparse package layout** in a temp directory
3. **Appends `.debug`** to the package name (unless `--keep-identity`) to avoid conflicts
4. **Registers with Windows** via `Add-AppxPackage -ExternalLocation` — makes your exe "identity-aware"

After running, launch your exe normally — Windows will recognize it as having package identity.

## Recommended workflow

1. **Setup** — `winapp init . --use-defaults` (creates `Package.appxmanifest`)
2. **Generate development certificate** — `winapp cert generate`
3. **Build** your app
4. **Register identity** — `winapp create-debug-identity ./bin/myapp.exe`
5. **Run** your app — identity-requiring APIs now work
6. **Re-run step 4** whenever you change `Package.appxmanifest` or `Assets/`

## Tips

- You must re-run `create-debug-identity` after any changes to `Package.appxmanifest` or image assets
- The debug identity persists across reboots until explicitly removed
- To remove: `Get-AppxPackage *yourapp.debug* | Remove-AppxPackage`
- If you have both a debug identity and an installed MSIX, they may conflict — use `--keep-identity` carefully
- For Electron apps, use `npx winapp node add-electron-debug-identity` instead (handles Electron-specific paths)

## Debugging: `winapp run` vs `create-debug-identity`

| | `winapp run` | `create-debug-identity` |
|---|---|---|
| **What it registers** | Full loose layout package (entire folder) | Sparse package (single exe) |
| **How the app launches** | Launched by winapp (AUMID activation or execution alias) | You launch the exe yourself (command line, IDE, etc.) |
| **Simulates MSIX install** | Yes — closest to production behavior | No — sparse identity only |
| **Files stay in place** | Copied to an AppX layout directory | Yes — exe stays at its original path |
| **Debugger-friendly** | Attach to PID after launch, or use `--no-launch` then launch via alias | Launch directly from your IDE's debugger — the exe has identity regardless |
| **Console app support** | Launched through an execution alias automatically, so stdin/stdout stay in this terminal | Run exe directly in terminal |
| **Best for** | Most frameworks (.NET, C++, Rust, Flutter, Tauri) | Electron, or when you need full IDE debugger control (F5 startup debugging) |

### When to use which

**Default to `winapp run`** for most development — it simulates a real MSIX install with full identity, capabilities, and file associations:

```powershell
winapp run .\build\output          # GUI and console apps alike; a console app
                                   # gets an execution alias automatically
```

**Use `create-debug-identity` when:**
- **Debugging startup code** — your IDE launches + debugs the exe directly; identity is attached from the first instruction
- **Exe is separate from build output** — e.g., Electron where `electron.exe` is in `node_modules/`
- **Testing sparse package behavior** — `AllowExternalContent`, `TrustedLaunch`

```powershell
winapp create-debug-identity .\bin\Debug\myapp.exe
# Now launch any way you like — F5, terminal, script — the exe has identity
```

### Common debugging scenarios

| Scenario | Command | Notes |
|----------|---------|-------|
| **Just run with identity** | `winapp run .\build\Debug` | Simplest workflow; a console app gets an execution alias automatically |
| **Attach debugger to running app** | `winapp run .\build\Debug`, then attach to PID | Misses startup code |
| **Register identity, launch via AUMID** | `winapp run .\build\Debug --no-launch` | Launch with `start shell:AppsFolder\<AUMID>` or the execution alias (not the exe directly) |
| **F5 startup debugging** | `winapp create-debug-identity .\bin\myapp.exe` | IDE controls process from first instruction; best for debugging activation/startup code |
| **Capture debug output** | `winapp run .\build\Debug --debug-output` | Captures `OutputDebugString`; on crash, writes minidump and analyzes managed exceptions automatically. **Blocks other debuggers** (one debugger per process) |
| **Run and auto-clean** | `winapp run .\build\Debug --unregister-on-exit` | Unregisters the dev package after the app exits |
| **Launch and detach (CI)** | `winapp run .\build\Debug --detach` | Returns immediately after launch; use `--json` to get PID for scripting |
| **Clean up stale registration** | `winapp unregister` | Removes dev packages for the current project (auto-detects from manifest; pass a `.cs` for a file-based app) |

> **Using Visual Studio with a packaging project?** VS already handles identity, AUMID activation, and debugger attachment from F5. These workflows are most useful for VS Code, terminal-based development, and frameworks VS doesn't natively package (Rust, Flutter, Tauri, Electron, C++).

For full details including IDE setup examples, see the [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md).

## Production sparse packaging (`init --sparse` / `pack` / `embed-identity`)

`create-debug-identity` is for **developer-time debugging** (requires Developer Mode, registers a raw manifest). For **production** — shipping identity to an app distributed by an existing installer (Inno Setup, WiX, NSIS) — use the sparse packaging workflow, which produces a signed identity-only `.msix`:

```powershell
# 1. Create the sparse identity manifest for your exe (skips SDK install)
winapp init --exe ./bin/Release/MyApp.exe --sparse --use-defaults

# 2. Build and sign the identity-only .msix (just the manifest, no binaries)
winapp pack ./sparse/appxmanifest.xml --cert ./devcert.pfx

# 3. Embed the <msix> identity element into the exe's fusion manifest
winapp embed-identity ./bin/Release/MyApp.exe
```

Then your installer registers the package against the install directory:
`Add-AppxPackage -Path MyApp.identity.msix -ExternalLocation <install-dir>`.

Assets are resolved from the external (install) location at runtime, **not** bundled into the `.msix`. `winapp embed-identity` also supports an XML mode (`winapp embed-identity ./app.manifest`) for updating a checked-in side-by-side manifest. See the [Sparse Packaging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/sparse.md) and the [sparse-app sample](https://github.com/microsoft/WinAppCli/tree/main/samples/sparse-app).

## Related skills
- Need a manifest? See `winapp-manifest` to generate `Package.appxmanifest`
- Need a certificate? See `winapp-signing` — a trusted cert is required for identity registration
- Ready for full MSIX distribution? See `winapp-package` to create an installer
- Having issues? See `winapp-troubleshoot` for common error solutions

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "Package.appxmanifest not found" | No manifest in current directory | Run `winapp init` or `winapp manifest generate`, or pass `--manifest` |
| "Failed to add package identity" | Previous registration stale or cert untrusted | Run `winapp unregister` to remove stale packages, then `winapp cert install ./devcert.pfx` (admin) |
| "Access denied" | Cert not trusted or permission issue | Run `winapp cert install ./devcert.pfx` as admin |
| APIs still fail after registration | App launched before registration completed | Close app, re-run `create-debug-identity`, then relaunch |

## CLI reference

Run `winapp <command> --help` for current command options, or `winapp --cli-schema` for the complete machine-readable command schema.
