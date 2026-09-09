---
ms.custom: mslearn
---
<!-- AUTO-GENERATED — DO NOT EDIT -->
<!-- Regenerate with: cd src/winapp-npm && npm run generate-docs -->

# NPM Package — Programmatic API

TypeScript/JavaScript API reference for `@microsoft/winappcli`.
Each CLI command is available as an async function that captures stdout/stderr and returns a typed result.
Helper utilities for MSIX identity, Electron debug identity, and build tools are also exported.

## Installation

```bash
npm install @microsoft/winappcli
```

## Quick start

```typescript
import { init, packageApp, certGenerate } from '@microsoft/winappcli';

// Initialize a new project with defaults
await init({ useDefaults: true });

// Generate a dev certificate
await certGenerate({ install: true });

// Package the built app
await packageApp({ inputFolder: './dist', cert: './devcert.pfx' });
```

## Common types

Every CLI command wrapper accepts an options object extending `CommonOptions` and returns `Promise<WinappResult>`.

### `CommonOptions`

Base options shared by most commands.

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `WinappResult`

Result returned by every command wrapper.

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exitCode` | `number` | Yes | Process exit code (always 0 on success – non-zero throws). |
| `stdout` | `string` | Yes | Captured standard output. |
| `stderr` | `string` | Yes | Captured standard error. |

## CLI command wrappers

These functions wrap native `winapp` CLI commands. All accept [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).

### `azSign()`

Code-sign a file using Azure Trusted Signing. Signs executables, MSIX packages, or MSIX bundles using a cloud-managed signing identity. Example: winapp az-sign ./app.msix

```typescript
function azSign(options: AzSignOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `filePath` | `string` | Yes | Path to the file to sign (exe, msix, or msixbundle) |
| `account` | `string \| undefined` | No | Signing account name. Must be used with --resource-group |
| `metadataFile` | `string \| undefined` | No | Path to an existing metadata.json file. Skips resource discovery and account/profile selection prompts and signs using this file directly. A non-interactive Azure credential should already be available; the CLI can otherwise fall back to an interactive tenant prompt or 'az login', but the npm programmatic API is always non-interactive and fails instead of prompting. |
| `profile` | `string \| undefined` | No | Certificate profile name. Must be used with --account |
| `resourceGroup` | `string \| undefined` | No | Resource group to narrow down signing accounts |
| `subscription` | `string \| undefined` | No | Azure subscription ID to use. If not provided and multiple subscriptions exist, you will be prompted. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `certGenerate()`

Create a self-signed certificate for local testing only. Publisher must match the manifest (auto-inferred if --manifest provided or Package.appxmanifest is in working directory). Output: devcert.pfx (default password: 'password'). For production, obtain a certificate from a trusted CA. Use 'cert install' to trust on this machine.

```typescript
function certGenerate(options?: CertGenerateOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exportCer` | `boolean \| undefined` | No | Export a .cer file (public key only) alongside the .pfx |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) |
| `install` | `boolean \| undefined` | No | Install the certificate to the local machine store after generation |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file to extract publisher information from |
| `output` | `string \| undefined` | No | Output path for the generated PFX file |
| `password` | `string \| undefined` | No | Password for the generated PFX file |
| `publisher` | `string \| undefined` | No | Publisher distinguished name (DN) for the generated certificate (e.g., CN=MyCompany or OU=Team, O=Corp, C=US). If not specified, will be inferred from manifest. Bare names are auto-wrapped as CN=<name>. |
| `validDays` | `number \| undefined` | No | Number of days the certificate is valid |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `certInfo()`

Display certificate details (subject, thumbprint, expiry). Useful for verifying a certificate matches your manifest before signing.

```typescript
function certInfo(options: CertInfoOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `certPath` | `string` | Yes | Path to the certificate file (PFX) |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `password` | `string \| undefined` | No | Password for the PFX file |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `certInstall()`

Trust a certificate on this machine (requires admin). Run before installing MSIX packages signed with dev certificates. Example: winapp cert install ./devcert.pfx. Only needed once per certificate.

```typescript
function certInstall(options: CertInstallOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `certPath` | `string` | Yes | Path to the certificate file (PFX or CER) |
| `force` | `boolean \| undefined` | No | Force installation even if the certificate already exists |
| `password` | `string \| undefined` | No | Password for the PFX file |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `createDebugIdentity()`

Enable package identity for debugging without creating full MSIX. Required for testing Windows APIs (push notifications, share target, etc.) during development. Example: winapp create-debug-identity ./myapp.exe. Requires Package.appxmanifest or appxmanifest.xml in current directory or passed via --manifest. Re-run after changing the manifest or Assets/.

```typescript
function createDebugIdentity(options?: CreateDebugIdentityOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `entrypoint` | `string \| undefined` | No | Path to the .exe that will need to run with identity, or entrypoint script. |
| `keepIdentity` | `boolean \| undefined` | No | Keep the package identity from the manifest as-is, without appending '.debug' to the package name and application ID. |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest or appxmanifest.xml |
| `noInstall` | `boolean \| undefined` | No | Do not install the package after creation. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `createExternalCatalog()`

Generates a CodeIntegrityExternal.cat catalog file with hashes of executable files from specified directories. Used with the TrustedLaunch flag in MSIX sparse package manifests (AllowExternalContent) to allow execution of external files not included in the package.

```typescript
function createExternalCatalog(options: CreateExternalCatalogOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string` | Yes | List of input folders with executable files to process (separated by semicolons) |
| `computeFlatHashes` | `boolean \| undefined` | No | Include flat hashes when generating the catalog |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file already exists |
| `output` | `string \| undefined` | No | Output catalog file path. If not specified, the default CodeIntegrityExternal.cat name is used. |
| `recursive` | `boolean \| undefined` | No | Include files from subdirectories |
| `usePageHashes` | `boolean \| undefined` | No | Include page hashes when generating the catalog |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `embedIdentity()`

Connect a desktop exe to its sparse identity package by embedding the <msix> element. Reads identity (packageName, publisher, applicationId) from a sparse appxmanifest.xml and writes it into the target's side-by-side (fusion) manifest. EXE targets are updated with mt.exe; .xml/.manifest targets are edited directly. Example: winapp embed-identity ./bin/MyApp.exe. This is step 3 of the sparse packaging workflow (after 'winapp init --exe --sparse' and 'winapp pack').

```typescript
function embedIdentity(options: EmbedIdentityOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `target` | `string` | Yes | Path to the .exe (embeds identity into its side-by-side manifest via mt.exe) or an .xml/.manifest side-by-side manifest file (inserts/replaces the <msix> element; created if it doesn't exist). |
| `manifest` | `string \| undefined` | No | Path to the sparse appxmanifest.xml to read identity from. When omitted, searched in a 'sparse/' folder (where 'winapp init --exe --sparse' writes it by default) beside the target first, then in the current directory, then beside the target and in the current directory. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `findUi()`

Search WinUI controls and samples for a working code example. WinUI-only: covers the WinUI 3 Gallery and the Windows Community Toolkit by default (plus the microsoft-ui-reactor ReactorGallery as an opt-in source via --source reactor); not WPF/WinForms. A corpus is baked into the CLI, so this works offline and behind proxies; when GitHub is reachable it refreshes to the latest samples and caches them per-user.

```typescript
function findUi(options?: FindUiOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `query` | `string \| undefined` | No | What you're looking for, e.g. "tabbed layout" or "color picker". Matched lexically against WinUI control names, sample headers, and tags. |
| `id` | `string \| string[] \| undefined` | No | Fetch the code (Gallery/Toolkit return XAML and/or C#; Reactor is C#-only) plus prerequisite notes for one or more scenario ids from a prior search (e.g. gallery-tabview-1). |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `list` | `boolean \| undefined` | No | List every discoverable control/sample id instead of searching. Covers Gallery, Toolkit, and core; the opt-in Reactor source is excluded (search it with --source reactor). |
| `max` | `number \| undefined` | No | Maximum number of matched controls to return. Applies to search only; ignored with --list and --id. |
| `refresh` | `boolean \| undefined` | No | Bypass the local cache and re-fetch the WinUI corpus from GitHub. |
| `source` | `string \| undefined` | No | Restrict results to a single source: gallery (WinUI 3 Gallery), toolkit (Windows Community Toolkit), reactor (microsoft-ui-reactor, C#-only declarative WinUI), or core (curated patterns). Reactor is opt-in — it is excluded from a normal search, so pass --source reactor to search it (only do this for a Reactor/MVU project; its C#-only samples don't paste into a standard XAML app). |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `getWinappPath()`

Print the path to the .winapp directory. Use --global for the shared cache location, or omit for the project-local .winapp folder. Useful for build scripts that need to reference installed packages.

```typescript
function getWinappPath(options?: GetWinappPathOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `global` | `boolean \| undefined` | No | Get the global .winapp directory instead of local |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `init()`

Start here for initializing a Windows app with required setup. Sets up everything needed for Windows app development: creates Package.appxmanifest with default assets, downloads Windows SDK and Windows App SDK packages, and generates projections. When SDK packages are managed (--setup-sdks stable/preview/experimental), also creates winapp.yaml to pin versions for 'restore'/'update'; with --setup-sdks none (e.g., for Rust/Tauri projects that bring their own SDK bindings), no winapp.yaml is created. Interactive by default; automatically uses defaults in non-interactive environments (use --use-defaults to skip prompts explicitly). Use 'restore' instead if you cloned a repo that already has winapp.yaml. Use 'manifest generate' if you only need a manifest, or 'cert generate' if you need a development certificate for code signing.

```typescript
function init(options?: InitOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `baseDirectory` | `string \| undefined` | No | Base/root directory for the winapp workspace, for consumption or installation. |
| `configDir` | `string \| undefined` | No | Directory to read/store configuration (default: the selected project directory, or current directory if no project is detected) |
| `configOnly` | `boolean \| undefined` | No | Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps. |
| `exe` | `string \| undefined` | No | Path to the application executable. Requires --sparse. Generates an identity-only sparse manifest for the exe instead of a full package/SDK setup. |
| `force` | `boolean \| undefined` | No | Overwrite an existing appxmanifest.xml in the target directory (sparse only). Without this, init fails instead of replacing existing manifest/asset files. |
| `ignoreConfig` | `boolean \| undefined` | No | Don't use configuration file for version management |
| `name` | `string \| undefined` | No | Override the package name (sparse only; default: inferred from the exe) |
| `noGitignore` | `boolean \| undefined` | No | Don't update .gitignore file |
| `outputDir` | `string \| undefined` | No | Directory to write the sparse manifest and Assets/ (sparse only; default: a 'sparse/' folder in the current directory) |
| `publisher` | `string \| undefined` | No | Override the publisher CN (sparse only; default: inferred from the exe's company name). Bare names are auto-wrapped as CN=<name>. |
| `setupSdks` | `SdkInstallMode \| undefined` | No | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) |
| `sparse` | `boolean \| undefined` | No | Generate a sparse identity manifest (appxmanifest.xml) for an existing desktop exe instead of a full package manifest. Use with --exe. Skips SDK/package installation. |
| `useDefaults` | `boolean \| undefined` | No | Skip interactive prompts and use default answers. Normal init targets the positional project directory if given, otherwise the current directory (e.g., winapp init . --use-defaults). Sparse init (--exe --sparse) ignores the positional directory and writes to --output-dir instead. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `manifestAddAlias()`

Add an execution alias (uap5:AppExecutionAlias) to a Package.appxmanifest. This allows launching the packaged app from the command line by typing the alias name. By default, the alias is inferred from the Executable attribute (e.g. $targetnametoken$.exe becomes $targetnametoken$.exe alias).

```typescript
function manifestAddAlias(options?: ManifestAddAliasOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `appId` | `string \| undefined` | No | Application Id to add the alias to (default: first Application element) |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) |
| `name` | `string \| undefined` | No | Alias name (e.g. 'myapp.exe'). Default: inferred from the Executable attribute in the manifest. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `manifestGenerate()`

Create Package.appxmanifest without full project setup. Use when you only need a manifest and image assets (no SDKs, no certificate). For full setup, use 'init' instead. Templates: 'packaged' (full MSIX), 'sparse' (desktop app needing Windows APIs).

```typescript
function manifestGenerate(options?: ManifestGenerateOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `directory` | `string \| undefined` | No | Directory to generate manifest in |
| `description` | `string \| undefined` | No | Human-readable app description shown during installation and in Windows Settings |
| `executable` | `string \| undefined` | No | Path to the application's executable. Default: <package-name>.exe |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) |
| `logoPath` | `string \| undefined` | No | Path to logo image file |
| `packageName` | `string \| undefined` | No | Package name (default: folder name) |
| `publisherName` | `string \| undefined` | No | Publisher distinguished name (DN) (default: CN=<current user>). Accepts any valid X.500 DN; bare names are auto-wrapped as CN=<name>. |
| `template` | `ManifestTemplates \| undefined` | No | Manifest template type: 'packaged' (full MSIX app, default) or 'sparse' (desktop app with package identity for Windows APIs) |
| `version` | `string \| undefined` | No | App version in Major.Minor.Build.Revision format (e.g., 1.0.0.0). |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `manifestUpdateAssets()`

Generate new assets for images referenced in a Package.appxmanifest from a single source image. Source image should be at least 400x400 pixels.

```typescript
function manifestUpdateAssets(options: ManifestUpdateAssetsOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `imagePath` | `string` | Yes | Path to source image file (SVG, PNG, ICO, JPG, BMP, GIF) |
| `lightImage` | `string \| undefined` | No | Path to source image for light theme variants (SVG, PNG, ICO, JPG, BMP, GIF) |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `newCommand()`

Create a new WinUI app from an official Windows App SDK template. Templates cover both markup-based XAML apps (blank, NavigationView, TabView, MVVM) and the experimental Reactor apps (C#-only, MVU) — pick one interactively, then a name (the output directory defaults to ./<name>). Automatically uses defaults in non-interactive environments (use --use-defaults to skip prompts explicitly). Requires the .NET SDK; installs the WinUI template pack on demand (grabbing the latest, or offering to update a stale one) and delegates scaffolding to 'dotnet new'. Use --list to see the available templates. Scaffolds against the installed SDK's target framework and prints a template-specific next step when done (e.g. 'dotnet run' for app templates).

```typescript
function newCommand(options?: NewOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `force` | `boolean \| undefined` | No | Scaffold even if the output directory already contains files. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `list` | `boolean \| undefined` | No | List the available WinUI templates and exit (installs the latest template pack if none is installed). |
| `name` | `string \| undefined` | No | Name for the new app/project (default: derived from --output, else 'WinUIApp'). |
| `output` | `string \| undefined` | No | Directory to create the app in (default: ./<name>). Created if it doesn't exist. |
| `template` | `string \| undefined` | No | Template short name. XAML templates: winui, winui-navview, winui-tabview, winui-mvvm, winui-lib, winui-unittest. Experimental Reactor (C#-only, MVU) templates: reactor, reactor-mvu, reactor-navview, reactor-tabview. Run 'winapp new --list' to see all. |
| `templateVersion` | `string \| undefined` | No | WinUI template pack version: 'latest' (install newest), 'installed' (keep what's installed), or an explicit version. Default: install latest if none, else prompt to update a stale pack. |
| `useDefaults` | `boolean \| undefined` | No | Do not prompt; use defaults (blank template, name from --output/--name, keep installed templates). |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `packageApp()`

Create MSIX installer from your built app. Run after building your app. A manifest (Package.appxmanifest or appxmanifest.xml) is required for packaging - it must be in current working directory, passed as --manifest or be in the input folder. Use --cert devcert.pfx to sign for testing. Example: winapp package ./dist --manifest Package.appxmanifest --cert ./devcert.pfx

```typescript
function packageApp(options: PackageOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string \| string[]` | Yes | One or more input folders with package layout, or a single sparse appxmanifest.xml file (an identity-only package with AllowExternalContent). Pass multiple folders to create an MSIX bundle (e.g., winapp pack ./publish/x64 ./publish/arm64). |
| `cert` | `string \| undefined` | No | Path to signing certificate (will auto-sign if provided) |
| `certPassword` | `string \| undefined` | No | Certificate password (default: password) |
| `executable` | `string \| undefined` | No | Path to the executable relative to the input folder. |
| `generateCert` | `boolean \| undefined` | No | Generate a new development certificate |
| `installCert` | `boolean \| undefined` | No | Install certificate to machine |
| `manifest` | `string \| undefined` | No | Path to AppX manifest file (default: auto-detect from input folder or current directory) |
| `name` | `string \| undefined` | No | Package name (default: from manifest) |
| `output` | `string \| undefined` | No | Output file name for the generated package (.msix) or bundle (.msixbundle). Defaults to <name>_<version>_<arch>.msix for single packages, or <name>_<version>_<arch1>_<arch2>.msixbundle for bundles. |
| `publisher` | `string \| undefined` | No | Publisher distinguished name (DN) for certificate generation (e.g., CN=MyCompany). Bare names are auto-wrapped as CN=<name>. |
| `selfContained` | `boolean \| undefined` | No | Bundle Windows App SDK runtime for self-contained deployment |
| `skipPri` | `boolean \| undefined` | No | Skip PRI file generation |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `restore()`

Use after cloning a repo or when .winapp/ folder is missing. Reinstalls SDK packages without changing versions, reading them from winapp.yaml or, for a .NET project initialized by 'init', from the .csproj via 'dotnet restore'. Requires a project already initialized by 'init'. To check for newer SDK versions, use 'update' instead.

```typescript
function restore(options?: RestoreOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `baseDirectory` | `string \| undefined` | No | Base/root directory for the winapp workspace |
| `configDir` | `string \| undefined` | No | Directory to read configuration from (default: base-directory) |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `run()`

Builds and runs a Windows app from a .csproj/.sln or a build-output folder. In project mode, invokes dotnet build then launches the app (packaged or unpackaged); in folder mode, creates a debug-signed layout, registers the package, and launches it.

```typescript
function run(options?: RunOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `input` | `string \| undefined` | No | Path to the app to run: a build-output folder, a .csproj project, a .sln/.slnx solution, or a directory containing one of those at its top level (default: current directory). |
| `inputFolder` | `string \| undefined` | No |  |
| `arch` | `string \| undefined` | No | Project mode: target architecture (x64, arm64, or x86). Sets the canonical Windows RID and selects a matching platform-dependent publish profile when required by the effective build. Ignored in folder mode. Default: the current process architecture. |
| `args` | `string \| undefined` | No | Command-line arguments to pass to the application. Alternatively, use -- followed by arguments to avoid escaping (e.g., winapp run . -- --flag value). |
| `clean` | `boolean \| undefined` | No | Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments. |
| `configuration` | `string \| undefined` | No | Project mode: build configuration (e.g., Debug, Release). Ignored in folder mode. Default: Debug. |
| `debugOutput` | `boolean \| undefined` | No | Capture OutputDebugString messages and first-chance exceptions from the launched application. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use --no-launch instead if you need to attach a different debugger. For WinUI apps, a crash also triggers a stowed-exception triage pass; the first run downloads debugger components (cached under the winapp global directory) and can be pointed at an existing debugger install via the WINAPP_DBGTOOLS_DIR environment variable. Cannot be combined with --no-launch or --json. |
| `detach` | `boolean \| undefined` | No | Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with --json). |
| `executable` | `string \| undefined` | No | Path to the executable relative to the input folder. Use to disambiguate when the manifest contains a $targetnametoken$ placeholder and multiple .exe files are present in the input folder. |
| `framework` | `string \| undefined` | No | Project mode: target framework moniker for multi-targeted projects (e.g. net10.0-windows10.0.26100.0). Ignored in folder mode. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest (default: auto-detect from input folder or current directory) |
| `noBuild` | `boolean \| undefined` | No | Project mode: skip building and run the existing build output (still evaluates output properties). Ignored in folder mode. |
| `noLaunch` | `boolean \| undefined` | No | Only create the debug identity and register the package without launching the application |
| `noRestore` | `boolean \| undefined` | No | Project mode: skip restoring the project before building. Ignored in folder mode. |
| `outputAppxDirectory` | `string \| undefined` | No | Output directory for the loose layout package. If not specified, a directory named AppX inside the input directory will be used. |
| `project` | `string \| undefined` | No | Project mode: when the input is a solution (.sln/.slnx) or a directory with multiple runnable app projects, selects which project to launch (by name or path). Ignored in folder mode. |
| `property` | `string \| string[] \| undefined` | No | Project mode: MSBuild property as Name=Value, forwarded to both build and evaluation. Repeatable. Ignored in folder mode. |
| `runtime` | `string \| undefined` | No | Project mode: target .NET runtime identifier (RID), e.g. win-x64. Project mode uses only the RID's architecture, always builds the canonical win-<arch>, rejects non-Windows RIDs (e.g. linux-x64), and can select a required architecture-dependent publish profile; it overrides --arch. Ignored in folder mode. |
| `symbols` | `boolean \| undefined` | No | Download symbols from Microsoft Symbol Server for richer native crash analysis, including the WinUI stowed-exception dispatch stack. Only used with --debug-output. First run downloads symbols and caches them locally; subsequent runs use the cache. |
| `unregisterOnExit` | `boolean \| undefined` | No | Unregister the development package after the application exits. Only removes packages registered in development mode. |
| `withAlias` | `boolean \| undefined` | No | Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a uap5:ExecutionAlias in the manifest. Use "winapp manifest add-alias" to add an execution alias to the manifest. |
| `appArgs` | `string \| string[] \| undefined` | No | Arguments to pass to the launched application (forwarded after --). |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `sign()`

Code-sign an MSIX package or executable. Example: winapp sign ./app.msix ./devcert.pfx. Use --timestamp for production builds to remain valid after cert expires. The 'package' command can sign automatically with --cert.

```typescript
function sign(options: SignOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `filePath` | `string` | Yes | Path to the file/package to sign |
| `certPath` | `string` | Yes | Path to the certificate file (PFX format) |
| `password` | `string \| undefined` | No | Certificate password |
| `timestamp` | `string \| undefined` | No | Timestamp server URL |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `store()`

Run a Microsoft Store Developer CLI command. This command will download the Microsoft Store Developer CLI if not already downloaded. Learn more about the Microsoft Store Developer CLI here: https://aka.ms/msstoredevcli

```typescript
function store(options?: StoreOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `storeArgs` | `string \| string[] \| undefined` | No | Arguments to pass through to the Microsoft Store Developer CLI. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `tool()`

Run Windows SDK tools directly (makeappx, signtool, makepri, etc.). Auto-downloads Build Tools if needed. For most tasks, prefer higher-level commands like 'package' or 'sign'. Example: winapp tool makeappx pack /d ./folder /p ./out.msix

```typescript
function tool(options?: ToolOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `toolArgs` | `string \| string[] \| undefined` | No | Arguments to pass to the SDK tool, e.g. ['makeappx', 'pack', '/d', './folder', '/p', './out.msix']. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiClick()`

Click an element by slug or text search using mouse simulation. Works on elements that don't support InvokePattern (e.g., column headers, list items). Use --double for double-click, --right for right-click.

```typescript
function uiClick(options?: UiClickOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `double` | `boolean \| undefined` | No | Perform a double-click instead of a single click |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `right` | `boolean \| undefined` | No | Perform a right-click instead of a left click |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiDrag()`

Press the mouse button at one point, move to another, then release. 'drag <from> <to>', where <from>/<to> are each an element selector (uses the element's center) or screen x,y coordinates as reported by 'ui inspect'. Useful for reorder/resize/slider gestures and drag-and-drop. Use --right for a right-button drag, --hold-ms for press-and-hold/long-press, and --dwell-ms to settle on a drop target before releasing.

```typescript
function uiDrag(options?: UiDragOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `from` | `string \| undefined` | No | Start point — an element selector (drags from its center) or screen coordinates x,y as reported by 'ui inspect' (e.g. pn-list-d736 or 100,200). |
| `to` | `string \| undefined` | No | End point — an element selector (drops at its center) or screen coordinates x,y as reported by 'ui inspect' (e.g. pn-target-d746 or 300,400). |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `dwellMs` | `number \| undefined` | No | Milliseconds to dwell at the destination after moving, before releasing (default: 0). Lets drop targets / merge overlays that arm from a sustained hover latch before release. |
| `holdMs` | `number \| undefined` | No | Milliseconds to hold the button down at the start before moving (default: 0). With <from> == <to> (no movement) this performs a press-and-hold / long-press gesture. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `right` | `boolean \| undefined` | No | Drag with the right mouse button instead of the left button |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiFocus()`

Move keyboard focus to the specified element using UIA SetFocus.

```typescript
function uiFocus(options?: UiFocusOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiGetFocused()`

Show the element that currently has keyboard focus in the target app.

```typescript
function uiGetFocused(options?: UiGetFocusedOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiGetProperty()`

Read UIA property values from an element. Specify --property for a single property or omit for all.

```typescript
function uiGetProperty(options?: UiGetPropertyOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `property` | `string \| undefined` | No | Property name to read or filter on |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiGetValue()`

Read the current value from an element. Tries TextPattern (RichEditBox, Document), ValuePattern (TextBox, ComboBox, Slider), then Name (labels). Usage: winapp ui get-value <selector> -a <app>

```typescript
function uiGetValue(options?: UiGetValueOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiHover()`

Move the mouse to an element's center to trigger hover effects (tooltips, flyouts, visual states). Uses SendInput for realistic mouse movement and waits for a configurable dwell time.

```typescript
function uiHover(options?: UiHoverOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `dwellTime` | `number \| undefined` | No | Time in milliseconds to wait after hovering for hover effects to appear (default: 800) |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiInspect()`

View the UI element tree with semantic slugs, element types, names, and bounds.

```typescript
function uiInspect(options?: UiInspectOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `ancestors` | `boolean \| undefined` | No | Walk up the tree from the specified element to the root |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `depth` | `number \| undefined` | No | Tree inspection depth |
| `hideDisabled` | `boolean \| undefined` | No | Hide disabled elements from output |
| `hideOffscreen` | `boolean \| undefined` | No | Hide offscreen elements from output |
| `interactive` | `boolean \| undefined` | No | Show only interactive/invokable elements (buttons, links, inputs, list items). Increases default depth to 8. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiInvoke()`

Activate an element by slug or text search. Tries InvokePattern, TogglePattern, SelectionItemPattern, and ExpandCollapsePattern in order.

```typescript
function uiInvoke(options?: UiInvokeOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiListWindows()`

List all visible windows with their HWND, title, process, and size. Use -a to filter by app name. Use the HWND with -w to target a specific window.

```typescript
function uiListWindows(options?: UiListWindowsOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `showHidden` | `boolean \| undefined` | No | Include untitled zero-size windows that are hidden by default |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiPen()`

Inject synthetic pen/stylus input using the Windows synthetic-pointer API. Taps or draws ink strokes with configurable pressure, tilt and eraser mode, at an element's center or explicit screen x,y coordinates. Requires an unlocked, interactive desktop with the target window foregroundable (Windows 10 1809+).

```typescript
function uiPen(options?: UiPenOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `at` | `string \| undefined` | No | Pen contact point as screen coordinates x,y (as reported by 'ui inspect'). Defaults to the selector's element center. Ignored when --path is given. |
| `durationMs` | `number \| undefined` | No | Total glide time in milliseconds distributed across the stroke path segments (default: ~10 ms per segment). |
| `eraser` | `boolean \| undefined` | No | Use the eraser end of the pen instead of the tip. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `path` | `string \| undefined` | No | Ink stroke path as a whitespace-separated list of x,y pairs, e.g. "10,10 20,30 40,50". |
| `pressure` | `number \| undefined` | No | Pen pressure from 0.0 to 1.0 (default: 0.5). |
| `tiltX` | `number \| undefined` | No | Pen tilt along the x-axis in degrees (-90 to 90, default: 0). |
| `tiltY` | `number \| undefined` | No | Pen tilt along the y-axis in degrees (-90 to 90, default: 0). |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiScreenshot()`

Capture the target window or element as a PNG image. When multiple windows exist (e.g., dialogs), captures each to a separate file. With --json, returns file path and dimensions. Use --capture-screen for popup overlays.

```typescript
function uiScreenshot(options?: UiScreenshotOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `captureScreen` | `boolean \| undefined` | No | Capture from screen DC via BitBlt (includes popups/overlays not owned by the target). |
| `focus` | `boolean \| undefined` | No | Bring the target window to the foreground before capture. Already implied by --capture-screen. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `output` | `string \| undefined` | No | Save output to this file path. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiScroll()`

Scroll a container element using ScrollPattern. Use --direction to scroll incrementally, --to to jump to top/bottom, or --wheel to synthesize mouse-wheel input.

```typescript
function uiScroll(options?: UiScrollOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `direction` | `string \| undefined` | No | Scroll direction: up, down, left, right |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `to` | `string \| undefined` | No | Scroll to position: top, bottom |
| `wheel` | `number \| undefined` | No | Rotate the mouse wheel over the element by this many notches (1 = one notch up, -1 = one notch down). Synthesizes real wheel input instead of using ScrollPattern. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiScrollIntoView()`

Scroll the specified element into the visible area using UIA ScrollItemPattern.

```typescript
function uiScrollIntoView(options?: UiScrollIntoViewOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiSearch()`

Search the element tree for elements matching a text query. Returns all matches with semantic slugs.

```typescript
function uiSearch(options?: UiSearchOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `max` | `number \| undefined` | No | Maximum search results |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiSendKeys()`

Send synthetic keyboard input to a window. Supports named keys (down, enter, tab), modifier combos (ctrl+shift+t), raw virtual keys (vk=0xNN), and literal text. Use --verbatim to type the whole argument literally, or --target to focus an element first. Two transports via --via: post-message (default, HWND-targeted, bypasses UIPI) or send-input (OS-wide). For per-keystroke KeyDown on typed text (e.g. a WinUI 3/WPF TextBox), use --via send-input.

```typescript
function uiSendKeys(options?: UiSendKeysOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `keys` | `string \| undefined` | No | Keys to send. Whitespace-separated tokens: named keys (down, enter, tab, esc, f5), modifier combos (ctrl+shift+t, alt+f4), raw virtual keys (vk=0x42), or literal text (hello). Use text=<literal> to type a single value verbatim when it would otherwise be read as a key name or combo (text=enter types "enter"; text=ctrl+a types "ctrl+a"); backslash escapes \s \t \n \r \\ are supported (text=a\s\sb types "a b"). To type the whole argument literally without escaping each token, pass --verbatim instead. Quote multi-token strings, e.g. "ctrl+a delete". |
| `allowSystemKeys` | `boolean \| undefined` | No | Allow synthesizing system-/shell-reserved combos (win+<key>, alt+f4, alt+tab, ctrl+esc, …) via --via send-input, which are refused by default because they act on the OS/shell beyond the target app. Opt in to drive global hotkeys (e.g. PowerToys' win+shift+v, win+r). No effect on --via post-message (already window-scoped; a warning is emitted if set without send-input). Note: win+l and ctrl+alt+del stay blocked even with this flag — win+l locks the workstation (LockWorkStation() via the shell hook), which is unrecoverable from automation, and ctrl+alt+del is a Secure Attention Sequence (SAS) that Windows drops from injected input regardless of this flag, so it can never take effect. |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `target` | `string \| undefined` | No | Optional selector (slug or text) to focus before sending keys. |
| `verbatim` | `boolean \| undefined` | No | Type the entire keys argument as literal text — no named-key, combo, or vk= interpretation, and exact whitespace preserved. The whole-argument form of the per-token text= escape: --verbatim "down down enter" types the words instead of pressing Down, Down, Enter. |
| `via` | `string \| undefined` | No | Transport: post-message (default, HWND-targeted, bypasses UIPI; typed text raises TextChanged but not a per-character KeyDown) or send-input (OS-wide; typed text raises a real per-character KeyDown + TextChanged). Named keys and combos raise KeyDown on both, but keyboard accelerators/shortcuts (KeyboardAccelerator, e.g. ctrl+t) only fire via send-input. post-message targets the focused child control and works for classic Win32/WinForms controls, but WinUI 3 / UWP / XAML controls are windowless and ignore posted messages — use send-input for those (a warning is emitted when the target looks like a XAML app). |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiSetValue()`

Set a value on an element programmatically. Works for TextBox, ComboBox, Slider, and other editable controls via UIA ValuePattern/RangeValuePattern, with a LegacyIAccessible (put_accValue) fallback for TextPattern-only edit controls — no app foreground required. Some rich text controls (e.g. WinUI 3 RichEditBox and WPF RichTextBox) don't support setting their value programmatically — use the 'send-keys' command with '--via send-input' to type into them instead. Usage: winapp ui set-value <selector> <value> -a <app>

```typescript
function uiSetValue(options?: UiSetValueOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `value` | `string \| undefined` | No | Value to set (text for TextBox/ComboBox, number for Slider) |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiStatus()`

Connect to a target app and display connection info.

```typescript
function uiStatus(options?: UiStatusOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiTouch()`

Inject synthetic touch input using the Windows touch-injection API. Supports tap, double-tap, long-press, swipe, pinch and stretch gestures at an element's center or explicit screen x,y coordinates. Requires an unlocked, interactive desktop with the target window foregroundable.

```typescript
function uiTouch(options?: UiTouchOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `at` | `string \| undefined` | No | Explicit start point as screen coordinates x,y (as reported by 'ui inspect'). Defaults to the selector's element center. |
| `direction` | `string \| undefined` | No | Swipe direction: right (default), left, up, or down. Combined with --distance to compute the end point when --to-point is not given. |
| `distance` | `number \| undefined` | No | Distance in pixels for pinch/stretch (finger spread) or swipe. |
| `durationMs` | `number \| undefined` | No | Glide time in milliseconds for moving gestures (swipe/pinch/stretch). |
| `fingers` | `number \| undefined` | No | Number of touch contacts (default: 1). Pinch/stretch always use 2. |
| `gesture` | `string \| undefined` | No | Gesture to perform: tap, double-tap, long-press, swipe, pinch, stretch (default: tap). |
| `holdMs` | `number \| undefined` | No | Milliseconds to hold contacts down before lifting (long-press hold time). Defaults to 500 ms when --gesture long-press is used and this option is not set. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `toPoint` | `string \| undefined` | No | End point x,y for a swipe (screen coordinates). Takes precedence over --direction. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiWaitFor()`

Wait for an element to appear, disappear, or have a property reach a target value. Polls at 100ms intervals until condition met or timeout.

```typescript
function uiWaitFor(options?: UiWaitForOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `contains` | `boolean \| undefined` | No | Use substring matching for --value instead of exact match |
| `gone` | `boolean \| undefined` | No | Wait for element to disappear instead of appear |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `property` | `string \| undefined` | No | Property name to read or filter on |
| `timeout` | `number \| undefined` | No | Timeout in milliseconds |
| `value` | `string \| undefined` | No | Wait for element value to equal this string. Uses smart fallback (TextPattern -> ValuePattern -> Name). Combine with --property to check a specific property instead. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `unregister()`

Unregisters a sideloaded development package. Only removes packages registered in development mode (e.g., via 'winapp run' or 'create-debug-identity').

```typescript
function unregister(options?: UnregisterOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `force` | `boolean \| undefined` | No | Skip the install-location directory check and unregister even if the package was registered from a different project tree |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest (default: auto-detect from current directory) |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `update()`

Check for and install newer SDK versions. Updates winapp.yaml with latest versions and reinstalls packages. Requires existing winapp.yaml (created by 'init'). Use --setup-sdks preview for preview SDKs. To reinstall current versions without updating, use 'restore' instead.

```typescript
function update(options?: UpdateOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `setupSdks` | `SdkInstallMode \| undefined` | No | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

## Utility functions

### `uiRecord()`

Record a window or element region to an H.264 MP4.

**`durationSec` is required and must be > 0.** Unbounded recording (durationSec == 0) is only
supported via the CLI with Ctrl+C or piped stdin. The npm wrapper has no mechanism to stop
an unbounded spawn, so passing durationSec == 0 or omitting it will throw a clear error.
Set `frames` to write timestamped JPEG evidence beside the MP4.

```typescript
function uiRecord(options: UiRecordOptions): Promise<WinappResult>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `options` | `UiRecordOptions` | Yes |  |

---

### `execWithBuildTools()`

Execute a command with BuildTools bin path added to PATH environment

```typescript
function execWithBuildTools(command: string, options?: ExecSyncOptions): string | Buffer<ArrayBufferLike>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `command` | `string` | Yes | The command to execute |
| `options` | `ExecSyncOptions` | No | Options to pass to execSync (optional) |

**Returns:** The output from execSync

---

### `addMsixIdentityToExe()`

Adds package identity information from a Package.appxmanifest or appxmanifest.xml file to an executable's embedded manifest

```typescript
function addMsixIdentityToExe(exePath: string, appxManifestPath?: string | undefined, options?: MsixIdentityOptions): Promise<MsixIdentityResult>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `exePath` | `string` | Yes | Path to the executable file |
| `appxManifestPath` | `string \| undefined` | No | Path to the Package.appxmanifest or appxmanifest.xml file containing package identity data |
| `options` | `MsixIdentityOptions` | No | Optional configuration |

---

### `addElectronDebugIdentity()`

Adds package identity to the Electron debug process

```typescript
function addElectronDebugIdentity(options?: MsixIdentityOptions): Promise<ElectronDebugIdentityResult>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `options` | `MsixIdentityOptions` | No | Configuration options |

---

### `clearElectronDebugIdentity()`

Clears/removes package identity from the Electron debug process by restoring from backup

```typescript
function clearElectronDebugIdentity(options?: MsixIdentityOptions): Promise<ClearElectronDebugIdentityResult>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `options` | `MsixIdentityOptions` | No | Configuration options |

---

### `getGlobalWinappPath()`

Get the path to the global .winapp directory

```typescript
function getGlobalWinappPath(): string
```

**Returns:** The full path to the global .winapp directory

---

### `getLocalWinappPath()`

Get the path to the local .winapp directory

```typescript
function getLocalWinappPath(): string
```

**Returns:** The full path to the local .winapp directory

---

## Node.js CLI commands

These commands are available exclusively via `npx winapp node <subcommand>` and are not exported as programmatic functions.

### `node create-addon`

Generate native addon files for an Electron project.  Supports C++ (node-gyp) and C# (node-api-dotnet) templates.

```bash
npx winapp node create-addon [options]
```

**Options:**

| Flag | Description |
|------|-------------|
| `--name <name>` | Addon name (default depends on template) |
| `--template <type>` | Addon template: `cpp` or `cs` (default: `cpp`) |
| `--verbose` | Enable verbose output |

> **Note:** Must be run from the root of an Electron project (directory containing `package.json`).

**Examples:**

```bash
npx winapp node create-addon
npx winapp node create-addon --name myAddon
npx winapp node create-addon --template cs --name MyCsAddon
```

---

### `node add-electron-debug-identity`

Add package identity to the Electron debug process using sparse packaging.  Creates a backup of `electron.exe`, generates a sparse MSIX manifest, adds identity to the executable, and registers the sparse package.  Requires a `Package.appxmanifest` (create one with `winapp init` or `winapp manifest generate`).

```bash
npx winapp node add-electron-debug-identity [options]
```

**Options:**

| Flag | Description |
|------|-------------|
| `--manifest <path>` | Path to custom `Package.appxmanifest` (default: `Package.appxmanifest` in current directory) |
| `--no-install` | Do not install the package after creation |
| `--keep-identity` | Keep the manifest identity as-is, without appending `.debug` suffix |
| `--verbose` | Enable verbose output |

> **Note:** Must be run from the root of an Electron project (directory containing `node_modules/electron`).  To undo, use `npx winapp node clear-electron-debug-identity`.

**Examples:**

```bash
npx winapp node add-electron-debug-identity
npx winapp node add-electron-debug-identity --manifest ./custom/Package.appxmanifest
```

---

### `node clear-electron-debug-identity`

Remove package identity from the Electron debug process.  Restores `electron.exe` from the backup created by `add-electron-debug-identity` and removes the backup files.

```bash
npx winapp node clear-electron-debug-identity [options]
```

**Options:**

| Flag | Description |
|------|-------------|
| `--verbose` | Enable verbose output |

> **Note:** Must be run from the root of an Electron project (directory containing `node_modules/electron`).

**Examples:**

```bash
npx winapp node clear-electron-debug-identity
```

---

## Types reference

### `ExecSyncOptions`

Re-exported from Node.js for convenience. See [Node.js docs](https://nodejs.org/api/child_process.html).

### `MsixIdentityOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `verbose` | `boolean \| undefined` | No |  |
| `noInstall` | `boolean \| undefined` | No |  |
| `keepIdentity` | `boolean \| undefined` | No |  |
| `manifest` | `string \| undefined` | No |  |

### `MsixIdentityResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |

### `ElectronDebugIdentityResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |
| `electronExePath` | `string` | Yes |  |
| `backupPath` | `string` | Yes |  |
| `manifestPath` | `string` | Yes |  |
| `assetsDir` | `string` | Yes |  |

### `ClearElectronDebugIdentityResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |
| `electronExePath` | `string` | Yes |  |
| `restoredFromBackup` | `boolean` | Yes |  |

### `CallWinappCliOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exitOnError` | `boolean \| undefined` | No |  |

### `CallWinappCliResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exitCode` | `number` | Yes |  |

### `CallWinappCliCaptureOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()) |

### `CallWinappCliCaptureResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exitCode` | `number` | Yes |  |
| `stdout` | `string` | Yes |  |
| `stderr` | `string` | Yes |  |

### `GenerateCppAddonOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `name` | `string \| undefined` | No |  |
| `projectRoot` | `string \| undefined` | No |  |
| `verbose` | `boolean \| undefined` | No |  |

### `GenerateCppAddonResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |
| `addonName` | `string` | Yes |  |
| `addonPath` | `string` | Yes |  |
| `needsTerminalRestart` | `boolean` | Yes |  |
| `files` | `string[]` | Yes |  |

### `GenerateCsAddonOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `name` | `string \| undefined` | No |  |
| `projectRoot` | `string \| undefined` | No |  |
| `verbose` | `boolean \| undefined` | No |  |

### `GenerateCsAddonResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |
| `addonName` | `string` | Yes |  |
| `addonPath` | `string` | Yes |  |
| `needsTerminalRestart` | `boolean` | Yes |  |
| `files` | `string[]` | Yes |  |

### `UiRecordOptions`

Stricter version of `UiRecordOptions` where `durationSec` is **required** (not optional).
This type is the public surface of `uiRecord`; the generated type has it optional.
Survives regeneration because it is defined here in the hand-written guard module.

```typescript
type UiRecordOptions = Omit<GeneratedUiRecordOptions, "durationSec"> & { durationSec: number; }
```

### `IfExists`

IfExists values.

```typescript
type IfExists = "error" | "overwrite" | "skip"
```

### `SdkInstallMode`

SdkInstallMode values.

```typescript
type SdkInstallMode = "stable" | "preview" | "experimental" | "none"
```

### `ManifestTemplates`

ManifestTemplates values.

```typescript
type ManifestTemplates = "packaged" | "sparse"
```

### `AzSignOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `filePath` | `string` | Yes | Path to the file to sign (exe, msix, or msixbundle) |
| `account` | `string \| undefined` | No | Signing account name. Must be used with --resource-group |
| `metadataFile` | `string \| undefined` | No | Path to an existing metadata.json file. Skips resource discovery and account/profile selection prompts and signs using this file directly. A non-interactive Azure credential should already be available; the CLI can otherwise fall back to an interactive tenant prompt or 'az login', but the npm programmatic API is always non-interactive and fails instead of prompting. |
| `profile` | `string \| undefined` | No | Certificate profile name. Must be used with --account |
| `resourceGroup` | `string \| undefined` | No | Resource group to narrow down signing accounts |
| `subscription` | `string \| undefined` | No | Azure subscription ID to use. If not provided and multiple subscriptions exist, you will be prompted. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `CertGenerateOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exportCer` | `boolean \| undefined` | No | Export a .cer file (public key only) alongside the .pfx |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) |
| `install` | `boolean \| undefined` | No | Install the certificate to the local machine store after generation |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file to extract publisher information from |
| `output` | `string \| undefined` | No | Output path for the generated PFX file |
| `password` | `string \| undefined` | No | Password for the generated PFX file |
| `publisher` | `string \| undefined` | No | Publisher distinguished name (DN) for the generated certificate (e.g., CN=MyCompany or OU=Team, O=Corp, C=US). If not specified, will be inferred from manifest. Bare names are auto-wrapped as CN=<name>. |
| `validDays` | `number \| undefined` | No | Number of days the certificate is valid |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `CertInfoOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `certPath` | `string` | Yes | Path to the certificate file (PFX) |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `password` | `string \| undefined` | No | Password for the PFX file |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `CertInstallOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `certPath` | `string` | Yes | Path to the certificate file (PFX or CER) |
| `force` | `boolean \| undefined` | No | Force installation even if the certificate already exists |
| `password` | `string \| undefined` | No | Password for the PFX file |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `CreateDebugIdentityOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `entrypoint` | `string \| undefined` | No | Path to the .exe that will need to run with identity, or entrypoint script. |
| `keepIdentity` | `boolean \| undefined` | No | Keep the package identity from the manifest as-is, without appending '.debug' to the package name and application ID. |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest or appxmanifest.xml |
| `noInstall` | `boolean \| undefined` | No | Do not install the package after creation. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `CreateExternalCatalogOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string` | Yes | List of input folders with executable files to process (separated by semicolons) |
| `computeFlatHashes` | `boolean \| undefined` | No | Include flat hashes when generating the catalog |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file already exists |
| `output` | `string \| undefined` | No | Output catalog file path. If not specified, the default CodeIntegrityExternal.cat name is used. |
| `recursive` | `boolean \| undefined` | No | Include files from subdirectories |
| `usePageHashes` | `boolean \| undefined` | No | Include page hashes when generating the catalog |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `EmbedIdentityOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `target` | `string` | Yes | Path to the .exe (embeds identity into its side-by-side manifest via mt.exe) or an .xml/.manifest side-by-side manifest file (inserts/replaces the <msix> element; created if it doesn't exist). |
| `manifest` | `string \| undefined` | No | Path to the sparse appxmanifest.xml to read identity from. When omitted, searched in a 'sparse/' folder (where 'winapp init --exe --sparse' writes it by default) beside the target first, then in the current directory, then beside the target and in the current directory. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `FindUiOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `query` | `string \| undefined` | No | What you're looking for, e.g. "tabbed layout" or "color picker". Matched lexically against WinUI control names, sample headers, and tags. |
| `id` | `string \| string[] \| undefined` | No | Fetch the code (Gallery/Toolkit return XAML and/or C#; Reactor is C#-only) plus prerequisite notes for one or more scenario ids from a prior search (e.g. gallery-tabview-1). |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `list` | `boolean \| undefined` | No | List every discoverable control/sample id instead of searching. Covers Gallery, Toolkit, and core; the opt-in Reactor source is excluded (search it with --source reactor). |
| `max` | `number \| undefined` | No | Maximum number of matched controls to return. Applies to search only; ignored with --list and --id. |
| `refresh` | `boolean \| undefined` | No | Bypass the local cache and re-fetch the WinUI corpus from GitHub. |
| `source` | `string \| undefined` | No | Restrict results to a single source: gallery (WinUI 3 Gallery), toolkit (Windows Community Toolkit), reactor (microsoft-ui-reactor, C#-only declarative WinUI), or core (curated patterns). Reactor is opt-in — it is excluded from a normal search, so pass --source reactor to search it (only do this for a Reactor/MVU project; its C#-only samples don't paste into a standard XAML app). |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `GetWinappPathOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `global` | `boolean \| undefined` | No | Get the global .winapp directory instead of local |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `InitOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `baseDirectory` | `string \| undefined` | No | Base/root directory for the winapp workspace, for consumption or installation. |
| `configDir` | `string \| undefined` | No | Directory to read/store configuration (default: the selected project directory, or current directory if no project is detected) |
| `configOnly` | `boolean \| undefined` | No | Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps. |
| `exe` | `string \| undefined` | No | Path to the application executable. Requires --sparse. Generates an identity-only sparse manifest for the exe instead of a full package/SDK setup. |
| `force` | `boolean \| undefined` | No | Overwrite an existing appxmanifest.xml in the target directory (sparse only). Without this, init fails instead of replacing existing manifest/asset files. |
| `ignoreConfig` | `boolean \| undefined` | No | Don't use configuration file for version management |
| `name` | `string \| undefined` | No | Override the package name (sparse only; default: inferred from the exe) |
| `noGitignore` | `boolean \| undefined` | No | Don't update .gitignore file |
| `outputDir` | `string \| undefined` | No | Directory to write the sparse manifest and Assets/ (sparse only; default: a 'sparse/' folder in the current directory) |
| `publisher` | `string \| undefined` | No | Override the publisher CN (sparse only; default: inferred from the exe's company name). Bare names are auto-wrapped as CN=<name>. |
| `setupSdks` | `SdkInstallMode \| undefined` | No | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) |
| `sparse` | `boolean \| undefined` | No | Generate a sparse identity manifest (appxmanifest.xml) for an existing desktop exe instead of a full package manifest. Use with --exe. Skips SDK/package installation. |
| `useDefaults` | `boolean \| undefined` | No | Skip interactive prompts and use default answers. Normal init targets the positional project directory if given, otherwise the current directory (e.g., winapp init . --use-defaults). Sparse init (--exe --sparse) ignores the positional directory and writes to --output-dir instead. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `ManifestAddAliasOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `appId` | `string \| undefined` | No | Application Id to add the alias to (default: first Application element) |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) |
| `name` | `string \| undefined` | No | Alias name (e.g. 'myapp.exe'). Default: inferred from the Executable attribute in the manifest. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `ManifestGenerateOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `directory` | `string \| undefined` | No | Directory to generate manifest in |
| `description` | `string \| undefined` | No | Human-readable app description shown during installation and in Windows Settings |
| `executable` | `string \| undefined` | No | Path to the application's executable. Default: <package-name>.exe |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) |
| `logoPath` | `string \| undefined` | No | Path to logo image file |
| `packageName` | `string \| undefined` | No | Package name (default: folder name) |
| `publisherName` | `string \| undefined` | No | Publisher distinguished name (DN) (default: CN=<current user>). Accepts any valid X.500 DN; bare names are auto-wrapped as CN=<name>. |
| `template` | `ManifestTemplates \| undefined` | No | Manifest template type: 'packaged' (full MSIX app, default) or 'sparse' (desktop app with package identity for Windows APIs) |
| `version` | `string \| undefined` | No | App version in Major.Minor.Build.Revision format (e.g., 1.0.0.0). |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `ManifestUpdateAssetsOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `imagePath` | `string` | Yes | Path to source image file (SVG, PNG, ICO, JPG, BMP, GIF) |
| `lightImage` | `string \| undefined` | No | Path to source image for light theme variants (SVG, PNG, ICO, JPG, BMP, GIF) |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `NewOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `force` | `boolean \| undefined` | No | Scaffold even if the output directory already contains files. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `list` | `boolean \| undefined` | No | List the available WinUI templates and exit (installs the latest template pack if none is installed). |
| `name` | `string \| undefined` | No | Name for the new app/project (default: derived from --output, else 'WinUIApp'). |
| `output` | `string \| undefined` | No | Directory to create the app in (default: ./<name>). Created if it doesn't exist. |
| `template` | `string \| undefined` | No | Template short name. XAML templates: winui, winui-navview, winui-tabview, winui-mvvm, winui-lib, winui-unittest. Experimental Reactor (C#-only, MVU) templates: reactor, reactor-mvu, reactor-navview, reactor-tabview. Run 'winapp new --list' to see all. |
| `templateVersion` | `string \| undefined` | No | WinUI template pack version: 'latest' (install newest), 'installed' (keep what's installed), or an explicit version. Default: install latest if none, else prompt to update a stale pack. |
| `useDefaults` | `boolean \| undefined` | No | Do not prompt; use defaults (blank template, name from --output/--name, keep installed templates). |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `PackageOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string \| string[]` | Yes | One or more input folders with package layout, or a single sparse appxmanifest.xml file (an identity-only package with AllowExternalContent). Pass multiple folders to create an MSIX bundle (e.g., winapp pack ./publish/x64 ./publish/arm64). |
| `cert` | `string \| undefined` | No | Path to signing certificate (will auto-sign if provided) |
| `certPassword` | `string \| undefined` | No | Certificate password (default: password) |
| `executable` | `string \| undefined` | No | Path to the executable relative to the input folder. |
| `generateCert` | `boolean \| undefined` | No | Generate a new development certificate |
| `installCert` | `boolean \| undefined` | No | Install certificate to machine |
| `manifest` | `string \| undefined` | No | Path to AppX manifest file (default: auto-detect from input folder or current directory) |
| `name` | `string \| undefined` | No | Package name (default: from manifest) |
| `output` | `string \| undefined` | No | Output file name for the generated package (.msix) or bundle (.msixbundle). Defaults to <name>_<version>_<arch>.msix for single packages, or <name>_<version>_<arch1>_<arch2>.msixbundle for bundles. |
| `publisher` | `string \| undefined` | No | Publisher distinguished name (DN) for certificate generation (e.g., CN=MyCompany). Bare names are auto-wrapped as CN=<name>. |
| `selfContained` | `boolean \| undefined` | No | Bundle Windows App SDK runtime for self-contained deployment |
| `skipPri` | `boolean \| undefined` | No | Skip PRI file generation |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `RestoreOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `baseDirectory` | `string \| undefined` | No | Base/root directory for the winapp workspace |
| `configDir` | `string \| undefined` | No | Directory to read configuration from (default: base-directory) |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `RunOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `input` | `string \| undefined` | No | Path to the app to run: a build-output folder, a .csproj project, a .sln/.slnx solution, or a directory containing one of those at its top level (default: current directory). |
| `inputFolder` | `string \| undefined` | No |  |
| `arch` | `string \| undefined` | No | Project mode: target architecture (x64, arm64, or x86). Sets the canonical Windows RID and selects a matching platform-dependent publish profile when required by the effective build. Ignored in folder mode. Default: the current process architecture. |
| `args` | `string \| undefined` | No | Command-line arguments to pass to the application. Alternatively, use -- followed by arguments to avoid escaping (e.g., winapp run . -- --flag value). |
| `clean` | `boolean \| undefined` | No | Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments. |
| `configuration` | `string \| undefined` | No | Project mode: build configuration (e.g., Debug, Release). Ignored in folder mode. Default: Debug. |
| `debugOutput` | `boolean \| undefined` | No | Capture OutputDebugString messages and first-chance exceptions from the launched application. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use --no-launch instead if you need to attach a different debugger. For WinUI apps, a crash also triggers a stowed-exception triage pass; the first run downloads debugger components (cached under the winapp global directory) and can be pointed at an existing debugger install via the WINAPP_DBGTOOLS_DIR environment variable. Cannot be combined with --no-launch or --json. |
| `detach` | `boolean \| undefined` | No | Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with --json). |
| `executable` | `string \| undefined` | No | Path to the executable relative to the input folder. Use to disambiguate when the manifest contains a $targetnametoken$ placeholder and multiple .exe files are present in the input folder. |
| `framework` | `string \| undefined` | No | Project mode: target framework moniker for multi-targeted projects (e.g. net10.0-windows10.0.26100.0). Ignored in folder mode. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest (default: auto-detect from input folder or current directory) |
| `noBuild` | `boolean \| undefined` | No | Project mode: skip building and run the existing build output (still evaluates output properties). Ignored in folder mode. |
| `noLaunch` | `boolean \| undefined` | No | Only create the debug identity and register the package without launching the application |
| `noRestore` | `boolean \| undefined` | No | Project mode: skip restoring the project before building. Ignored in folder mode. |
| `outputAppxDirectory` | `string \| undefined` | No | Output directory for the loose layout package. If not specified, a directory named AppX inside the input directory will be used. |
| `project` | `string \| undefined` | No | Project mode: when the input is a solution (.sln/.slnx) or a directory with multiple runnable app projects, selects which project to launch (by name or path). Ignored in folder mode. |
| `property` | `string \| string[] \| undefined` | No | Project mode: MSBuild property as Name=Value, forwarded to both build and evaluation. Repeatable. Ignored in folder mode. |
| `runtime` | `string \| undefined` | No | Project mode: target .NET runtime identifier (RID), e.g. win-x64. Project mode uses only the RID's architecture, always builds the canonical win-<arch>, rejects non-Windows RIDs (e.g. linux-x64), and can select a required architecture-dependent publish profile; it overrides --arch. Ignored in folder mode. |
| `symbols` | `boolean \| undefined` | No | Download symbols from Microsoft Symbol Server for richer native crash analysis, including the WinUI stowed-exception dispatch stack. Only used with --debug-output. First run downloads symbols and caches them locally; subsequent runs use the cache. |
| `unregisterOnExit` | `boolean \| undefined` | No | Unregister the development package after the application exits. Only removes packages registered in development mode. |
| `withAlias` | `boolean \| undefined` | No | Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a uap5:ExecutionAlias in the manifest. Use "winapp manifest add-alias" to add an execution alias to the manifest. |
| `appArgs` | `string \| string[] \| undefined` | No | Arguments to pass to the launched application (forwarded after --). |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `SignOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `filePath` | `string` | Yes | Path to the file/package to sign |
| `certPath` | `string` | Yes | Path to the certificate file (PFX format) |
| `password` | `string \| undefined` | No | Certificate password |
| `timestamp` | `string \| undefined` | No | Timestamp server URL |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `StoreOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `storeArgs` | `string \| string[] \| undefined` | No | Arguments to pass through to the Microsoft Store Developer CLI. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `ToolOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `toolArgs` | `string \| string[] \| undefined` | No | Arguments to pass to the SDK tool, e.g. ['makeappx', 'pack', '/d', './folder', '/p', './out.msix']. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiClickOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `double` | `boolean \| undefined` | No | Perform a double-click instead of a single click |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `right` | `boolean \| undefined` | No | Perform a right-click instead of a left click |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiDragOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `from` | `string \| undefined` | No | Start point — an element selector (drags from its center) or screen coordinates x,y as reported by 'ui inspect' (e.g. pn-list-d736 or 100,200). |
| `to` | `string \| undefined` | No | End point — an element selector (drops at its center) or screen coordinates x,y as reported by 'ui inspect' (e.g. pn-target-d746 or 300,400). |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `dwellMs` | `number \| undefined` | No | Milliseconds to dwell at the destination after moving, before releasing (default: 0). Lets drop targets / merge overlays that arm from a sustained hover latch before release. |
| `holdMs` | `number \| undefined` | No | Milliseconds to hold the button down at the start before moving (default: 0). With <from> == <to> (no movement) this performs a press-and-hold / long-press gesture. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `right` | `boolean \| undefined` | No | Drag with the right mouse button instead of the left button |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiFocusOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiGetFocusedOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiGetPropertyOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `property` | `string \| undefined` | No | Property name to read or filter on |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiGetValueOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiHoverOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `dwellTime` | `number \| undefined` | No | Time in milliseconds to wait after hovering for hover effects to appear (default: 800) |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiInspectOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `ancestors` | `boolean \| undefined` | No | Walk up the tree from the specified element to the root |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `depth` | `number \| undefined` | No | Tree inspection depth |
| `hideDisabled` | `boolean \| undefined` | No | Hide disabled elements from output |
| `hideOffscreen` | `boolean \| undefined` | No | Hide offscreen elements from output |
| `interactive` | `boolean \| undefined` | No | Show only interactive/invokable elements (buttons, links, inputs, list items). Increases default depth to 8. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiInvokeOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiListWindowsOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `showHidden` | `boolean \| undefined` | No | Include untitled zero-size windows that are hidden by default |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiPenOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `at` | `string \| undefined` | No | Pen contact point as screen coordinates x,y (as reported by 'ui inspect'). Defaults to the selector's element center. Ignored when --path is given. |
| `durationMs` | `number \| undefined` | No | Total glide time in milliseconds distributed across the stroke path segments (default: ~10 ms per segment). |
| `eraser` | `boolean \| undefined` | No | Use the eraser end of the pen instead of the tip. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `path` | `string \| undefined` | No | Ink stroke path as a whitespace-separated list of x,y pairs, e.g. "10,10 20,30 40,50". |
| `pressure` | `number \| undefined` | No | Pen pressure from 0.0 to 1.0 (default: 0.5). |
| `tiltX` | `number \| undefined` | No | Pen tilt along the x-axis in degrees (-90 to 90, default: 0). |
| `tiltY` | `number \| undefined` | No | Pen tilt along the y-axis in degrees (-90 to 90, default: 0). |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiScreenshotOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `captureScreen` | `boolean \| undefined` | No | Capture from screen DC via BitBlt (includes popups/overlays not owned by the target). |
| `focus` | `boolean \| undefined` | No | Bring the target window to the foreground before capture. Already implied by --capture-screen. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `output` | `string \| undefined` | No | Save output to this file path. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiScrollOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `direction` | `string \| undefined` | No | Scroll direction: up, down, left, right |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `to` | `string \| undefined` | No | Scroll to position: top, bottom |
| `wheel` | `number \| undefined` | No | Rotate the mouse wheel over the element by this many notches (1 = one notch up, -1 = one notch down). Synthesizes real wheel input instead of using ScrollPattern. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiScrollIntoViewOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiSearchOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `max` | `number \| undefined` | No | Maximum search results |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiSendKeysOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `keys` | `string \| undefined` | No | Keys to send. Whitespace-separated tokens: named keys (down, enter, tab, esc, f5), modifier combos (ctrl+shift+t, alt+f4), raw virtual keys (vk=0x42), or literal text (hello). Use text=<literal> to type a single value verbatim when it would otherwise be read as a key name or combo (text=enter types "enter"; text=ctrl+a types "ctrl+a"); backslash escapes \s \t \n \r \\ are supported (text=a\s\sb types "a b"). To type the whole argument literally without escaping each token, pass --verbatim instead. Quote multi-token strings, e.g. "ctrl+a delete". |
| `allowSystemKeys` | `boolean \| undefined` | No | Allow synthesizing system-/shell-reserved combos (win+<key>, alt+f4, alt+tab, ctrl+esc, …) via --via send-input, which are refused by default because they act on the OS/shell beyond the target app. Opt in to drive global hotkeys (e.g. PowerToys' win+shift+v, win+r). No effect on --via post-message (already window-scoped; a warning is emitted if set without send-input). Note: win+l and ctrl+alt+del stay blocked even with this flag — win+l locks the workstation (LockWorkStation() via the shell hook), which is unrecoverable from automation, and ctrl+alt+del is a Secure Attention Sequence (SAS) that Windows drops from injected input regardless of this flag, so it can never take effect. |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `target` | `string \| undefined` | No | Optional selector (slug or text) to focus before sending keys. |
| `verbatim` | `boolean \| undefined` | No | Type the entire keys argument as literal text — no named-key, combo, or vk= interpretation, and exact whitespace preserved. The whole-argument form of the per-token text= escape: --verbatim "down down enter" types the words instead of pressing Down, Down, Enter. |
| `via` | `string \| undefined` | No | Transport: post-message (default, HWND-targeted, bypasses UIPI; typed text raises TextChanged but not a per-character KeyDown) or send-input (OS-wide; typed text raises a real per-character KeyDown + TextChanged). Named keys and combos raise KeyDown on both, but keyboard accelerators/shortcuts (KeyboardAccelerator, e.g. ctrl+t) only fire via send-input. post-message targets the focused child control and works for classic Win32/WinForms controls, but WinUI 3 / UWP / XAML controls are windowless and ignore posted messages — use send-input for those (a warning is emitted when the target looks like a XAML app). |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiSetValueOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `value` | `string \| undefined` | No | Value to set (text for TextBox/ComboBox, number for Slider) |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiStatusOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiTouchOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `at` | `string \| undefined` | No | Explicit start point as screen coordinates x,y (as reported by 'ui inspect'). Defaults to the selector's element center. |
| `direction` | `string \| undefined` | No | Swipe direction: right (default), left, up, or down. Combined with --distance to compute the end point when --to-point is not given. |
| `distance` | `number \| undefined` | No | Distance in pixels for pinch/stretch (finger spread) or swipe. |
| `durationMs` | `number \| undefined` | No | Glide time in milliseconds for moving gestures (swipe/pinch/stretch). |
| `fingers` | `number \| undefined` | No | Number of touch contacts (default: 1). Pinch/stretch always use 2. |
| `gesture` | `string \| undefined` | No | Gesture to perform: tap, double-tap, long-press, swipe, pinch, stretch (default: tap). |
| `holdMs` | `number \| undefined` | No | Milliseconds to hold contacts down before lifting (long-press hold time). Defaults to 500 ms when --gesture long-press is used and this option is not set. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `toPoint` | `string \| undefined` | No | End point x,y for a swipe (screen coordinates). Takes precedence over --direction. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiWaitForOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `contains` | `boolean \| undefined` | No | Use substring matching for --value instead of exact match |
| `gone` | `boolean \| undefined` | No | Wait for element to disappear instead of appear |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `property` | `string \| undefined` | No | Property name to read or filter on |
| `timeout` | `number \| undefined` | No | Timeout in milliseconds |
| `value` | `string \| undefined` | No | Wait for element value to equal this string. Uses smart fallback (TextPattern -> ValuePattern -> Name). Combine with --property to check a specific property instead. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UnregisterOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `force` | `boolean \| undefined` | No | Skip the install-location directory check and unregister even if the package was registered from a different project tree |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest (default: auto-detect from current directory) |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UpdateOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `setupSdks` | `SdkInstallMode \| undefined` | No | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

