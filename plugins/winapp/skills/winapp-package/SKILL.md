---
name: winapp-package
description: Package a Windows app as an MSIX installer for distribution or testing. Use when creating a Windows installer, packaging an Electron/Flutter/.NET/Rust/C++/Tauri app for Windows, building an MSIX, distributing a desktop app, packaging a console app or CLI tool, or adding MSIX packaging to a build script or CI/CD pipeline.
---
## When to use

Use this skill when:
- **Creating an MSIX installer** from a built app for distribution or testing
- **Packaging any Windows app** — GUI apps, console apps, CLI tools, services, or background processes
- **Signing a package** with a development or production certificate
- **Bundling the Windows App SDK runtime** for self-contained deployment

## Prerequisites

Before packaging, you need:
1. **Built app output** in a folder (e.g., `bin/Release/`, `dist/`, `build/`)
2. **`Package.appxmanifest`** — from `winapp init` or `winapp manifest generate`
3. **Certificate** (optional) — `devcert.pfx` from `winapp cert generate` for signing

## Usage

### Package directly from a .csproj (project mode)

```powershell
# Build the project and create an MSIX in one step (no need to build or locate the output first)
winapp package ./MyApp.csproj

# Pick configuration/architecture, or sign in the same step
winapp package ./MyApp.csproj -c Release --arch arm64 --cert ./devcert.pfx

# Package an already-built output without rebuilding
winapp package ./MyApp.csproj --no-build
```

Project mode is triggered only by an explicit `.csproj`. It builds with the same options as
`winapp run` (`-c/--configuration`, `--arch`, `-r/--runtime`, `-f/--framework`, `--no-build`,
`--no-restore`, repeatable `-p`), resolves the build output, and packages it. If the project
builds as an unpackaged app (`WindowsPackageType=None`) there is no manifest to package and the
command errors. Folder, bundle, and sparse-manifest inputs are unchanged.

### Basic packaging (unsigned)

```powershell
# Package from build output — manifest auto-detected from current dir or input folder
winapp package ./bin/Release

# Specify manifest location explicitly
winapp package ./dist --manifest ./Package.appxmanifest
```

### Package and sign in one step

```powershell
# Sign with existing certificate
winapp package ./bin/Release --cert ./devcert.pfx

# Custom certificate password
winapp package ./bin/Release --cert ./devcert.pfx --cert-password MyP@ssw0rd
```

### Generate certificate + package in one step

```powershell
# Auto-generate cert, sign, and package
winapp package ./bin/Release --generate-cert

# Also install the cert to trust it on this machine (requires admin)
winapp package ./bin/Release --generate-cert --install-cert
```

### Self-contained deployment

```powershell
# Bundle Windows App SDK runtime so users don't need it installed (must have winappsdk reference in the winapp.yaml or *.csproj)
winapp package ./bin/Release --cert ./devcert.pfx --self-contained
```

### Custom output path and name

```powershell
# Specify output file
winapp package ./dist --output ./releases/myapp-v1.0.msix --cert ./devcert.pfx

# Custom package name
winapp package ./dist --name "MyApp_1.0.0_x64" --cert ./devcert.pfx
```

## What the command does

1. **Locates `Package.appxmanifest`** — looks in input folder, then current directory (or uses `--manifest`)
2. **Copies manifest + assets** into a staging layout alongside your app files
3. **Discovers manifest-referenced files** — any non-image file referenced in the manifest (e.g., AppExtension payloads like `manifest.json`, config files) is automatically copied from the manifest directory or input folder if missing from staging
4. **Generates `resources.pri`** — Package Resource Index for UWP-style resource lookup (skip with `--skip-pri`)
5. **Runs `makeappx pack`** — creates the `.msix` package file
6. **Signs the package** (if `--cert` provided) — calls `signtool` with your certificate

Output: a `.msix` file that can be installed on Windows via double-click or `Add-AppxPackage`.

## Installing the MSIX for testing

```powershell
# Trust the dev certificate first (one-time, requires admin)
winapp cert install ./devcert.pfx

# Install the MSIX
Add-AppxPackage ./myapp.msix

# Uninstall if needed
Get-AppxPackage *myapp* | Remove-AppxPackage
```

## Recommended workflow

1. **Build** your app (`dotnet build`, `cmake --build`, `npm run make`, etc.)
2. **Package** — `winapp package <build-output> --cert ./devcert.pfx`
3. **Trust cert** (first time) — `winapp cert install ./devcert.pfx` (admin)
4. **Install** — double-click the `.msix` or `Add-AppxPackage ./myapp.msix`
5. **Test** the installed app from the Start menu

### Advanced: External content catalog

For sparse packages with `AllowExternalContent`, you may need a code integrity catalog:

```powershell
# Generate CodeIntegrityExternal.cat for external executables
winapp create-external-catalog "./bin/Release"

# Include subdirectories and specify output path
winapp create-external-catalog "./bin/Release" --recursive --output ./catalog/CodeIntegrityExternal.cat
```

### Bundling multiple architectures

Create an MSIX bundle from multiple per-architecture build outputs:

```powershell
# Create unsigned bundle for Store submission (x64 + arm64)
winapp package ./publish/x64 ./publish/arm64

# Create signed bundle for sideloading
winapp package ./publish/x64 ./publish/arm64 --cert ./devcert.pfx

# Self-contained bundle with Windows App SDK runtime per arch
winapp package ./publish/x64 ./publish/arm64 --self-contained --generate-cert
```

**How it works:** When multiple input folders are passed, `winapp package`:
1. Detects the architecture of each folder's primary executable from its PE header
2. Resolves a manifest for each slice (see below)
3. Validates that all slices share the same Identity, Capabilities, and Dependencies
4. Packs each folder into an intermediate unsigned `.msix`
5. Bundles them into a single `.msixbundle` using `makeappx bundle`
6. Signs only the bundle (not individual slices) — the signature covers all packages inside

**Manifest resolution:** Each slice needs a manifest. Resolution order:
- `--manifest <path>` uses one manifest for all slices (architecture auto-stamped per folder)
- Per-folder `Package.appxmanifest` if present in the input folder
- Fallback to `Package.appxmanifest` in the current working directory

The `ProcessorArchitecture` is always force-set to the detected architecture per-slice. All other Identity fields must be consistent across slices.

**Output:** `<Name>_<Version>_<arch1>_<arch2>.msixbundle` (architectures sorted alphabetically).

**Store submission:** An unsigned bundle is valid for Store upload — Partner Center signs it with your reserved identity certificate. For sideloading, pass `--cert` or `--generate-cert`.

This hashes executables in the specified directories so Windows trusts them when running with sparse package identity.

## CI/CD

### GitHub Actions

Use the `microsoft/setup-winapp` action to install winapp on GitHub-hosted runners:

```yaml
- uses: microsoft/setup-winapp@v1

- name: Package
  run: winapp package ./dist --cert ${{ secrets.CERT_PATH }} --cert-password ${{ secrets.CERT_PASSWORD }} --quiet
```

**Tips for CI/CD pipelines:**
- Use `--quiet` (or `-q`) to suppress progress output
- Use `--if-exists skip` with `winapp cert generate` to avoid regenerating existing certificates
- Store your PFX certificate as a repository secret and decode it in CI
- Use `--use-defaults` (or `--no-prompt`) with `winapp init` to avoid interactive prompts

## Tips

- The `package` command aliases to `pack` — both work identically
- `Package.appxmanifest` Publisher must match the certificate publisher — use `winapp cert generate --manifest` to ensure they match
- Use `--skip-pri` if your app doesn't use Windows resource loading (e.g., most Electron/Rust/C++ apps without UWP resources)
- For framework-specific packaging paths (Electron, .NET, Rust, etc.), see the `winapp-frameworks` skill
- The `--executable` flag overrides the entry point in the manifest — useful when your exe name differs from what's in `Package.appxmanifest`
- For production distribution, use a certificate from a trusted CA and add `--timestamp` when signing with `winapp sign`

## Sparse identity packages

To grant identity to an app distributed by an existing installer (not as MSIX), build an **identity-only** sparse package: pass a sparse `appxmanifest.xml` (one declaring `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>` under `<Properties>`) to `winapp pack` instead of a folder.

```powershell
# 1. Generate the sparse manifest for your exe (skips SDK install)
winapp init --exe ./bin/Release/MyApp.exe --sparse --use-defaults
# 2. Build & sign the identity-only .msix (just the manifest)
winapp pack ./sparse/appxmanifest.xml --cert ./devcert.pfx
# 3. Embed identity into the exe, then register in your installer
winapp embed-identity ./bin/Release/MyApp.exe
```

The `.msix` contains only the manifest — binaries and assets are resolved from the external content location at runtime via `Add-AppxPackage -ExternalLocation`. If you pack a folder whose manifest declares `AllowExternalContent`, `winapp pack` warns about any assets/binaries found. See the [Sparse Packaging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/sparse.md).

## Related skills
- Need a manifest first? See `winapp-manifest` to generate `Package.appxmanifest`
- Need a certificate? See `winapp-signing` for certificate generation and management
- Having issues? See `winapp-troubleshoot` for a command selection flowchart and error solutions

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "Package.appxmanifest not found" | No manifest in input folder or current dir | Run `winapp init` or `winapp manifest generate` first |
| "Publisher mismatch" | Cert publisher ≠ manifest publisher | Regenerate cert with `winapp cert generate --manifest`, or edit manifest |
| "Package installation failed" | Cert not trusted or stale package | Run `winapp cert install ./devcert.pfx` (admin), then `Get-AppxPackage <name> \| Remove-AppxPackage` |
| "makeappx not found" | Build tools not downloaded | Run `winapp update` or `winapp tool makeappx --help` to trigger download |

## CLI reference

Run `winapp <command> --help` for current command options, or `winapp --cli-schema` for the complete machine-readable command schema.
