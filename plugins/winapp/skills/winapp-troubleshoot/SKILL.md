---
name: winapp-troubleshoot
description: Diagnose and fix common Windows app packaging, signing, identity, and SDK errors. Use when encountering errors with MSIX packaging, certificate signing, Windows SDK setup, or app installation.
---
## When to use

Use this skill when:
- **Diagnosing errors** from winapp CLI commands
- **Choosing the right command** for a task
- **Understanding prerequisites** — what each command needs and what it produces

## Common errors & solutions

| Error | Cause | Solution |
|-------|-------|----------|
| "winapp.yaml not found" | Running `restore` or `update` without config | Run `winapp init` first, or `cd` to the directory containing `winapp.yaml` |
| "Package.appxmanifest not found" | Running `package`, `create-debug-identity`, or `cert generate --manifest` | Run `winapp init` or `winapp manifest generate` first, or pass `--manifest <path>` |
| "Publisher mismatch" | Certificate publisher ≠ manifest publisher | Regenerate cert: `winapp cert generate --manifest`, or edit `Package.appxmanifest` `Identity.Publisher` to match |
| "Access denied" / "elevation required" | `cert install` without admin | Run terminal as Administrator for `winapp cert install` |
| "Package installation failed" | Cert not trusted, or stale package registration | `winapp cert install ./devcert.pfx` (admin), then `Get-AppxPackage <name> \| Remove-AppxPackage` |
| "Certificate not trusted" | Dev cert not installed on machine | `winapp cert install ./devcert.pfx` (admin) |
| "Build tools not found" | First run, tools not yet downloaded | Run `winapp update` to download tools; ensure internet access |
| "Failed to add package identity" | Stale debug identity or untrusted cert | `Get-AppxPackage *yourapp* \| Remove-AppxPackage` to clean up, then `winapp cert install` and retry |
| "Certificate file already exists" | `devcert.pfx` already present | Use `winapp cert generate --if-exists overwrite` or `--if-exists skip` |
| "Manifest already exists" | `Package.appxmanifest` already present | Use `winapp manifest generate --if-exists overwrite` or edit manifest directly |
| `run` / `create-debug-identity` registration error `0x800704EC` | Developer Mode is disabled | Enable it in **Settings → Privacy & security → For developers**, or `Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -Value 1`, then retry |
| `run` / `create-debug-identity` registration error `0x80073CFB` | Package already registered with a conflicting identity | Run `winapp unregister` (or `winapp unregister --force` if the package was registered from a different project tree), then retry |
| App's Start menu entry launches nothing, silently | Package still registered after its files were deleted | Run `winapp unregister --prune` to remove every dev registration whose files are gone |

## Command selection guide

```
Is the app a single .cs file (.NET file-based app)?
├─ Yes → winapp run <file>.cs  (builds it and generates the manifest for you)
└─ No → Does the project have a Package.appxmanifest?
   ├─ No → Do you want full setup (manifest + config + optional SDKs)?
   │       ├─ Yes → winapp init (adds Windows platform files to existing project)
   │       └─ No, just a manifest → winapp manifest generate
   └─ Yes
      ├─ Has winapp.yaml, cloned/pulled but .winapp/ folder missing?
      │  └─ winapp restore
      ├─ Want newer SDK versions?
      │  └─ winapp update
      ├─ Need a dev certificate?
      │  └─ winapp cert generate (then winapp cert install for trust)
      ├─ Need package identity for debugging? (see [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md))
      │  ├─ Exe is in your build output folder? (most frameworks)
      │  │  └─ winapp run <build-output-dir>
      │  └─ Exe is separate from app code? (Electron, sparse testing)
      │     └─ winapp create-debug-identity <exe>
      ├─ Ready to create MSIX installer?
      │  └─ winapp package <build-output> --cert ./devcert.pfx
      ├─ Need to sign an existing file?
      │  └─ winapp sign <file> <cert>
      ├─ Need to update app icons?
      │  └─ winapp manifest update-assets ./logo.png
      ├─ Need to run SDK tools directly?
      │  └─ winapp tool <toolname> <args>
      ├─ Need to publish to Microsoft Store?
      │  └─ winapp store <args> (passthrough to Store Developer CLI)
      └─ Need the .winapp directory path for build scripts?
         └─ winapp get-winapp-path (or --global for shared cache)
```

**Important notes:**
- `winapp init` adds files to an **existing** project — it does not create a new project
- The key prerequisite for most commands is `Package.appxmanifest`, not `winapp.yaml`
- `winapp.yaml` is only needed for SDK version management (`restore`/`update`)
- Projects with NuGet package references (e.g., `.csproj` referencing `Microsoft.Windows.SDK.BuildTools`) can use winapp commands without `winapp.yaml`
- For Electron projects, use the npm package (`npm install --save-dev @microsoft/winappcli`) which includes Node.js-specific commands under `npx winapp node`

## Debugging approach quick reference

| Goal | Command | Key detail |
|------|---------|------------|
| Run with identity (most common) | `winapp run .\build\Debug` | Registers loose layout + launches; a console app gets an execution alias automatically |
| Attach debugger to running app | `winapp run .\build\Debug` → attach to PID | Misses startup code |
| Register identity, launch manually | `winapp run .\build\Debug --no-launch` | Launch via `start shell:AppsFolder\<AUMID>` or execution alias — **not** the exe directly |
| F5 startup debugging (IDE launches exe) | `winapp create-debug-identity .\bin\myapp.exe` | Exe has identity regardless of how it's launched; best for debugging activation/startup code |
| Capture OutputDebugString + crash dump | `winapp run .\build\Debug --debug-output` | On crash, writes minidump and shows exception type, message, and faulting methods. **Blocks other debuggers** — use `--no-launch` if you need VS Code/WinDbg |
| Run and auto-clean | `winapp run .\build\Debug --unregister-on-exit` | Unregisters the dev package after the app exits |
| Launch and detach (CI) | `winapp run .\build\Debug --detach` | Returns immediately after launch; use `--json` to get PID for scripting |
| Clean up stale registration | `winapp unregister` | Removes dev-mode packages for the current project (pass a `.cs` for a file-based app: `winapp unregister counter.cs`) |
| Start menu entry does nothing when clicked | `winapp unregister --prune` | The package is registered but its files were deleted, so activation silently fails. Prune removes every dev registration whose files are gone |

> **Visual Studio users:** If you have a packaging project, VS already handles identity and debugging from F5 — you likely don't need winapp for debugging. These workflows are for VS Code, terminal, and frameworks VS doesn't natively package.

For full details, see the [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md).

## Prerequisites & state matrix

| Command | Requires | Creates/Modifies |
|---------|----------|------------------|
| `init` | Existing project (any framework) | `winapp.yaml`, `.winapp/`, `Package.appxmanifest`, `Assets/`, `.gitignore` update |
| `restore` | `winapp.yaml` | `.winapp/packages/`, generated projections |
| `update` | `winapp.yaml` | Updates versions in `winapp.yaml`, reinstalls packages |
| `manifest generate` | Nothing | `Package.appxmanifest`, `Assets/` |
| `manifest update-assets` | `Package.appxmanifest` + source image | Regenerates `Assets/` icons |
| `cert generate` | Nothing (or `Package.appxmanifest` for publisher) | `devcert.pfx` |
| `cert install` | Certificate file + admin | Machine certificate store |
| `create-debug-identity` | `Package.appxmanifest` + exe + trusted cert | Registers sparse package with Windows |
| `run` | Build output folder + `Package.appxmanifest`; **or** a `.csproj`/`.sln`; **or** a `.cs` file-based app (no manifest needed — one is generated) | Registers loose layout package, launches app |
| `unregister` | A `.cs` file-based app, **or** `Package.appxmanifest` (auto-detect or `--manifest`) | Removes dev-mode package registrations |
| `package` | Build output + `Package.appxmanifest` | `.msix` file |
| `sign` | File + certificate | Signed file (in-place) |
| `create-external-catalog` | Directory with executables | `CodeIntegrityExternal.cat` |
| `tool <name>` | Nothing (auto-downloads tools) | Runs SDK tool directly |
| `store` | Nothing (auto-downloads Store CLI) | Passthrough to Microsoft Store Developer CLI |
| `get-winapp-path` | Nothing | Prints `.winapp` directory path |

## Debugging tips

- Add `--verbose` (or `-v`) to any command for detailed output
- Add `--quiet` (or `-q`) to suppress progress messages (useful in CI/CD)
- Run `winapp --cli-schema` to get the full JSON schema of all commands and options
- Run any command with `--help` for its specific usage information
- Use `winapp get-winapp-path` to find where packages are stored locally
- Use `winapp get-winapp-path --global` to find the shared cache location

## Getting more help

- Full CLI documentation: https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md
- Framework-specific guides: https://github.com/microsoft/WinAppCli/tree/main/docs/guides
- File an issue: https://github.com/microsoft/WinAppCli/issues

## Related skills
- **Setup & init**: `winapp-setup` — adding Windows support to a project
- **Manifest**: `winapp-manifest` — creating and editing `Package.appxmanifest`
- **Signing**: `winapp-signing` — certificate generation and management
- **Packaging**: `winapp-package` — creating MSIX installers
- **Identity**: `winapp-identity` — enabling package identity for Windows APIs
- **Frameworks**: `winapp-frameworks` — framework-specific guidance (Electron, .NET, C++, Rust, Flutter, Tauri)
- **MAUI**: `winapp-maui` — packaging/signing .NET MAUI Windows apps and resolving the resizetizer manifest

## CLI reference

Run `winapp <command> --help` for current command options, or `winapp --cli-schema` for the complete machine-readable command schema.
