<!-- mslearn: true -->
<!-- description: Grant package identity to an unpackaged desktop app using an identity-only (sparse) MSIX and external-location registration with the winapp CLI. -->
# Sparse packaging: grant identity to an unpackaged app

> For a working end-to-end example (WPF app + Inno Setup installer), see the [sparse-app](../../samples/sparse-app) sample.

A standard desktop executable — built with `dotnet build`, MSBuild, CMake, or any other toolchain — has no [package identity](https://learn.microsoft.com/windows/apps/desktop/modernize/package-identity-overview). Without identity, it cannot use many modern Windows APIs (toast notifications, background tasks, share targets, startup tasks, the app data APIs, and more).

**Sparse packaging** grants identity to an app *without* moving its binaries into an MSIX. You ship a tiny **identity-only** `.msix` (just a manifest) and register it alongside your normally-installed app using an *external location*. Your `.exe` stays exactly where your installer puts it. This is the production counterpart to [`winapp create-debug-identity`](../usage.md#create-debug-identity), which is for developer-time debugging only.

This guide covers the three CLI steps that map to the first three steps of the official [Grant identity to non-packaged apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps) workflow:

| Step | Command | Result |
|------|---------|--------|
| 1. Create the identity manifest | `winapp init --exe <exe> --sparse` | `sparse/appxmanifest.xml` + `sparse/Assets/` |
| 2. Build & sign the identity package | `winapp pack <appxmanifest.xml> --cert <pfx>` | `<PackageName>.identity.msix` |
| 3. Embed identity into the app | `winapp embed-identity <exe>` | `<msix>` element in the exe's fusion manifest |

Steps 4–5 of the docs (register / unregister the package) are your **installer's** responsibility — see [Installer integration](#installer-integration).

## When to use sparse packaging

- You already have a mature installer (Inno Setup, WiX, NSIS, MSI) and don't want to switch to MSIX for distribution, but you need identity-gated Windows APIs.
- Your app must install to a path or with a layout that MSIX doesn't allow.
- You want a minimal, additive change: keep your existing install flow and add one `.msix` registration step.

If you're starting fresh and can distribute as MSIX, a full packaged app (`winapp init` + `winapp pack <folder>`) is simpler.

## Prerequisites

1. **Windows 10, version 2004 (build 19041) or later.** Sparse packages rely on `uap10:AllowExternalContent`, which requires 19041+.
2. **winapp CLI** — install via winget (or update if already installed):
   ```powershell
   winget install Microsoft.WinApp --source winget
   ```
3. **A code-signing certificate** trusted on the target machine. For local testing, generate a development certificate with [`winapp cert generate`](../usage.md#cert) and trust it. Production packages must be signed with a certificate whose subject matches the manifest `Publisher`.

## Walkthrough

The examples below assume a built executable at `./bin/Release/net8.0-windows/MyApp.exe`.

### Step 1 — Create the sparse identity manifest

```powershell
winapp init --exe ./bin/Release/net8.0-windows/MyApp.exe --sparse
```

This infers the package name, publisher, version, and description from the exe (via its file version info) and prompts you to accept or override them. Add `--use-defaults` (or `--no-prompt`) to skip the prompts in CI, and `--name` / `--publisher` to override specific values:

```powershell
winapp init --exe ./bin/Release/net8.0-windows/MyApp.exe --sparse --use-defaults `
  --name "Contoso.MyApp" --publisher "CN=Contoso"
```

It writes the following to a dedicated `sparse/` folder in the current directory by default (override with `--output-dir`):

- `appxmanifest.xml` — a sparse manifest with `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>` (an element under `<Properties>`), `ProcessorArchitecture="neutral"`, a `win32App` application, and the exe name filled into `Executable`.
- `Assets/` — placeholder visual assets (extracted from the exe's icon when possible).

> [!NOTE]
> **Why a `sparse/` folder and not next to the exe?** The manifest and `Assets/` are **build-time inputs** consumed by `winapp pack` and `winapp embed-identity` — nothing reads them from beside the exe at runtime (runtime identity comes from the `<msix>` element embedded in the exe plus the registered package's external location, and the manifest references the exe by *name*, so its location is independent of where the exe lives). Writing them into a dedicated, source-controlled folder keeps them out of a build-output directory (like `bin/`) that a clean/rebuild would wipe, and keeps the folder free of binaries so the next steps stay clean. `winapp pack` and `winapp embed-identity` look in `sparse/` automatically, so you rarely need to name the path.

> [!NOTE]
> The sparse init flow deliberately **skips all SDK/package installation** — identity-only packages have no SDK dependencies.

If an `appxmanifest.xml` already exists in the target directory, init stops rather than overwriting it (and its `Assets/`). Re-run with `--force` to regenerate it.

Make sure the `Publisher` in the generated manifest matches the certificate you'll sign with. Edit `appxmanifest.xml` if needed, or pass `--publisher` when generating.

### Step 2 — Build and sign the identity package

Point `winapp pack` at the sparse manifest (a file, not a folder):

```powershell
winapp pack ./sparse/appxmanifest.xml --cert ./devcert.pfx
```

Because the manifest declares `AllowExternalContent`, `winapp pack` builds an **identity-only** `.msix` containing just the manifest — no binaries, no assets. The output defaults to `<PackageName>.identity.msix` in the current directory; use `--output` to change it. Signing happens only when you pass `--cert` (or `--generate-cert`).

### Step 3 — Embed identity into your app

Embed the `<msix>` element so Windows connects the running exe to the identity package:

```powershell
# EXE mode — modify the built binary in place (uses mt.exe)
winapp embed-identity ./bin/Release/net8.0-windows/MyApp.exe
```

Or maintain the side-by-side manifest as a checked-in file and rebuild:

```powershell
# XML mode — update an external SxS manifest, then rebuild your app
winapp embed-identity ./app.manifest
```

In XML mode the `<msix>` element is inserted into (or replaced in) the target manifest. Reference that manifest from your project (for .NET, set `<ApplicationManifest>app.manifest</ApplicationManifest>`) and rebuild so the element is embedded in the exe.

Both modes read identity from a sparse `appxmanifest.xml`. When you omit `--manifest`, winapp looks in a `sparse/` folder (where `winapp init --exe --sparse` writes it by default) beside the target first, then in the current directory, then falls back to beside the target and the current directory; pass `--manifest` to point elsewhere.

> [!IMPORTANT]
> EXE mode rewrites the binary with `mt.exe`, which invalidates any existing Authenticode signature. Re-sign the exe (e.g. `winapp sign ./MyApp.exe <cert.pfx>`) before distributing it.

### Step 4 — Register (for local testing)

The manifest's logos are resolved from the **external location** at runtime, not from the
identity-only `.msix`. Step 1 wrote them under `./sparse/Assets`, so copy them next to your exe
(the external location) before registering — otherwise Windows registers a layout missing every
logo the manifest references:

```powershell
# Copy the generated assets into the external location (beside your exe)
Copy-Item ./sparse/Assets -Destination .\bin\Release\net8.0-windows\Assets -Recurse -Force
```

Then register the identity package against that folder (the *external location*):

```powershell
Add-AppxPackage -Path .\MyApp.identity.msix `
  -ExternalLocation (Resolve-Path .\bin\Release\net8.0-windows)
```

Launch the app and confirm identity is present — for example, `Windows.ApplicationModel.Package.Current.Id.FamilyName` should return your package family name instead of throwing.

To clean up:

```powershell
Remove-AppxPackage <full-package-name>
```

## Asset handling

The sparse `.msix` is **identity-only**. The visual assets referenced by the manifest (`Assets\StoreLogo.png`, tiles, etc.) are resolved from the **external content location** at runtime — i.e., from your app's install directory — **not** from inside the `.msix`.

This means you must **deploy the `Assets/` folder alongside your application** (same layout the manifest expects, relative to the external location).

Step 2 packs the manifest **file** directly (`winapp pack ./sparse/appxmanifest.xml`), which builds the identity-only `.msix` from just that manifest — sibling files are ignored, so it never includes your assets or binaries. (If you instead point `winapp pack` at a *folder* whose manifest declares `AllowExternalContent`, it warns about any assets or binaries it finds, since for a sparse package those belong at the external location, not inside the `.msix`.)

## Installer integration

Registration and unregistration are the installer's job. The pattern is the same across installer tools:

- **Install:** copy your app binaries, the `Assets/` folder, and the `.msix` to the install directory, then run
  `Add-AppxPackage -Path "<install-dir>\MyApp.identity.msix" -ExternalLocation "<install-dir>"`.
- **Uninstall:** run `Remove-AppxPackage <full-package-name>` before deleting files.

> [!WARNING]
> The install directory is resolved at install time and may contain characters
> (e.g. a single quote) that break out of a PowerShell string literal. Always escape or validate
> the path before interpolating it into a `-Command` string — the WiX and NSIS snippets below
> assume a trusted install path, while the Inno Setup example demonstrates safe escaping. Prefer
> passing paths as arguments to a `-File` script over inline `-Command` interpolation.

### Inno Setup

Build the PowerShell arguments in a `[Code]` function so the runtime install path is escaped
for the single-quoted PowerShell literal (an install directory containing a `'` must not be able
to inject script):

```pascal
[Files]
Source: "dist\*"; DestDir: "{app}"; Flags: recursesubdirs
Source: "MyApp.identity.msix"; DestDir: "{app}"

[Run]
Filename: "powershell.exe"; Parameters: "{code:RegisterParams}"; Flags: runhidden

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name 'MyApp' | Remove-AppxPackage"""; \
  Flags: runhidden

[Code]
function EscapePSLiteral(const Value: string): string;
var S: string;
begin
  S := Value; StringChange(S, '''', ''''''); Result := S;
end;

function RegisterParams(Param: string): string;
var AppDir: string;
begin
  AppDir := ExpandConstant('{app}');
  { -ErrorAction Stop + try/catch make a registration failure terminating, so powershell.exe
    exits nonzero and the AfterInstall callback (see the full sample) can abort with rollback. }
  Result := '-NoProfile -ExecutionPolicy Bypass -Command "try { Add-AppxPackage -Path ''' +
    EscapePSLiteral(AppDir + '\MyApp.identity.msix') +
    ''' -ExternalLocation ''' + EscapePSLiteral(AppDir) + ''' -ErrorAction Stop } catch { Write-Error $_; exit 1 }"';
end;
```

See the [sparse-app](../../samples/sparse-app) sample for a complete, working `setup.iss`.

The WiX and NSIS examples below invoke a small `register-sparse.ps1` via `-File` so the install path is passed as a **parameter** (PowerShell binds it as data) instead of being interpolated into a `-Command` string. This avoids script injection through a crafted install directory (e.g. a folder name containing a quote or `$(...)`):

```powershell
# register-sparse.ps1 — ship this alongside your installer
param(
  [Parameter(Mandatory)] [string] $MsixPath,
  [Parameter(Mandatory)] [string] $ExternalLocation,
  [Parameter(Mandatory)] [string] $PackageName
)
$ErrorActionPreference = 'Stop'
try {
  # Add-AppxPackage emits NON-terminating errors by default, so a failure would otherwise leave
  # the process exit code at 0 and let the installer complete without identity. Try the add
  # directly first: a fresh install or a version-bumped upgrade registers/updates in place
  # without touching any existing registration. -ErrorAction Stop + the outer trap make a real
  # failure terminating so the installer (WiX Return="check" / NSIS) sees it.
  try {
    Add-AppxPackage -Path $MsixPath -ExternalLocation $ExternalLocation -ErrorAction Stop
  } catch {
    # Only ONE failure is safe to resolve by unregister+retry: the exact same version is already
    # registered (HRESULT 0x80073CFB, ERROR_PACKAGE_ALREADY_EXISTS — "already installed,
    # reinstallation blocked"), which Add-AppxPackage rejects. Re-throw everything else
    # (untrusted/corrupt .msix, unsupported OS, ...) so a bad new package can NEVER unregister a
    # working prior registration and strip the installed app of the identity it already had.
    if ($_.Exception.HResult -ne 0x80073CFB) { throw }
    Get-AppxPackage -Name $PackageName | Remove-AppxPackage -ErrorAction SilentlyContinue
    Add-AppxPackage -Path $MsixPath -ExternalLocation $ExternalLocation -ErrorAction Stop
  }
} catch {
  Write-Error $_
  exit 1
}
```

### WiX (v3)

Register **per user** (`Impersonate="yes"`), because `Add-AppxPackage` registers the package for the account that runs it. A deferred action with `Impersonate="no"` runs as `LocalSystem`, which does **not** grant identity to the installing user (and is commonly rejected). For a per-machine MSI, run the registration impersonated so it applies to the invoking user.

A deferred custom action can't read `INSTALLFOLDER` directly (deferred actions run in a context without access to properties), and simply *declaring* the action doesn't run it. So marshal the paths in through `CustomActionData` — an immediate type-51 action whose `Property` name equals the deferred action's `Id` — and schedule **both** after `InstallFiles`:

```xml
<!-- Immediate: stash the command line (with the resolved paths) into the deferred action's
     CustomActionData. Windows Installer copies the value of the property named the same as a
     deferred action into that action's CustomActionData. -->
<CustomAction Id="SetRegisterSparseCmd" Property="RegisterSparse" Execute="immediate"
  Value="powershell.exe -NoProfile -ExecutionPolicy Bypass -File &quot;[INSTALLFOLDER]register-sparse.ps1&quot; -MsixPath &quot;[INSTALLFOLDER]MyApp.identity.msix&quot; -ExternalLocation &quot;[INSTALLFOLDER]&quot; -PackageName &quot;MyPackageIdentityName&quot;" />

<!-- Deferred + impersonated: CAQuietExec reads its command line from CustomActionData when run
     deferred, so it registers the package for the invoking user. Return="check" fails the
     install if registration fails. -->
<CustomAction Id="RegisterSparse" BinaryKey="WixCA" DllEntry="CAQuietExec"
  Execute="deferred" Impersonate="yes" Return="check" />

<InstallExecuteSequence>
  <Custom Action="SetRegisterSparseCmd" After="InstallFiles">NOT Installed</Custom>
  <Custom Action="RegisterSparse" After="SetRegisterSparseCmd">NOT Installed</Custom>
</InstallExecuteSequence>
```

`CAQuietExec` ships in the WiX util extension (`WixUtilExtension`); reference it so the `WixCA` binary is available.

> A single impersonated action registers identity only for the user running the installer. To provision every user of a per-machine install, register on first launch (per-user) instead, or use a provisioning mechanism such as `Add-AppxProvisionedPackage`.

### NSIS

```nsis
Section
  # Capture the PowerShell exit code and abort if registration failed. register-sparse.ps1 exits
  # nonzero on failure (it sets $ErrorActionPreference='Stop' and traps), so without this check the
  # installer would complete even though the app has no identity.
  ExecWait 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\register-sparse.ps1" -MsixPath "$INSTDIR\MyApp.identity.msix" -ExternalLocation "$INSTDIR" -PackageName "MyPackageIdentityName"' $0
  IntCmp $0 0 +2
    Abort "Registering the sparse identity package failed (exit code $0). The app requires package identity."
SectionEnd
```

## Troubleshooting

**`Package.Current` throws / "no package identity" at runtime**
- The identity package isn't registered, or the exe's fusion manifest is missing the `<msix>` element. Re-run [`winapp embed-identity`](../usage.md#embed-identity) (and rebuild if using XML mode), then re-register with `Add-AppxPackage -ExternalLocation`.
- The `<msix packageName>` / `publisher` / `applicationId` in the exe must **exactly** match the registered package's identity.

**Assets/logos don't appear**
- Ensure the `Assets/` folder is deployed at the external location with the same relative paths the manifest expects. Assets are resolved from the external location, not the `.msix`.

**`Add-AppxPackage` fails with a signing / trust error**
- The `.msix` must be signed by a certificate that is trusted on the machine and whose subject matches the manifest `Publisher`. For local testing, generate and trust a dev certificate with [`winapp cert generate`](../usage.md#cert), and make sure the manifest `Publisher` matches it.

**MakeAppx: "Application with RuntimeBehavior value 'win32App' must not declare EntryPoint"**
- A sparse `win32App` application must not declare `EntryPoint`. Manifests generated by `winapp init --sparse` are already correct; remove any `EntryPoint` attribute if you hand-edited the manifest.

**"Input is a file but not a sparse manifest"**
- `winapp pack <file>` only accepts a manifest that declares `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>`. Generate one with `winapp init --exe <exe> --sparse`, or pass an input *folder* to build a full MSIX.

## See also

- [CLI usage: `init`](../usage.md#init), [`pack`](../usage.md#pack), [`embed-identity`](../usage.md#embed-identity)
- [Grant identity to non-packaged apps (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)
- [Debugging with package identity](../debugging.md)
