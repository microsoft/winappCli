<!-- mslearn: true -->
<!-- ms.topic: reference -->
<!-- description: Complete command reference for the winapp CLI covering setup, packaging, identity, certificates, signing, and other utility commands. -->
# CLI Documentation and Usage

## Shell Completion

Enable tab completion for commands, options, and values. See the [Shell Completion guide](guides/shell-completion.md) for setup instructions.

```powershell
# Quick setup for PowerShell (permanent — add to profile)
winapp complete --setup powershell >> $PROFILE

# Or try it in the current session only
winapp complete --setup powershell | Out-String | Invoke-Expression
```

### init

Initialize a directory with Windows SDK, Windows App SDK, and required assets for modern Windows development.

```bash
winapp init [base-directory] [options]
```

**Arguments:**

- `base-directory` - Base/root directory for the app/workspace (default: current directory)

**Options:**

- `--config-dir <path>` - Directory to read/store configuration (default: the selected project directory, or current directory if no project is detected)
- `--setup-sdks` - SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)
- `--ignore-config`, `--no-config` - Don't use configuration file for version management
- `--no-gitignore` - Don't update .gitignore file
- `--use-defaults`, `--no-prompt` - Do not prompt, and use default of all prompts
- `--config-only` - Only handle configuration file operations, skip package installation
- `--exe <path>` - Path to the application executable. **Requires `--sparse`.** Generates an identity-only sparse manifest for the exe instead of a full package/SDK setup.
- `--sparse` - Generate a sparse identity manifest (`appxmanifest.xml`) for an existing desktop exe. Skips SDK/package installation. Use with `--exe`.
- `--name <name>` - Override the package name (sparse only; default: inferred from the exe)
- `--publisher <CN>` - Override the publisher CN (sparse only; default: inferred from the exe's company name)
- `--output-dir <path>` - Directory to write the sparse manifest and `Assets/` (sparse only; default: a `sparse/` folder in the current directory)
- `--force` - Overwrite an existing `appxmanifest.xml` in the target directory (sparse only). Without it, init fails instead of replacing an existing manifest/assets.
- `--add-js-bindings` *(npm only)* - Add `winapp.jsBindings` to package.json and generate JS/TypeScript bindings, without prompting (incompatible with `--setup-sdks none`)

**What it does:**

- Creates `winapp.yaml` configuration file (only when SDK packages are managed; skipped with `--setup-sdks none`)
- Downloads Windows SDK and Windows App SDK packages
- Generates C++/WinRT headers and binaries
- Creates Package.appxmanifest
- Sets up build tools and enables developer mode
- Updates .gitignore to exclude generated files
- Stores shareable files in the global cache directory
- Generates JS bindings for Windows App SDK APIs when enabled (npm only)

**Automatic project detection:**

When `init` is run without a directory argument, it performs a breadth-first search of the current directory tree to find compatible projects (up to 10). Supported project types:

- **Tauri** — `tauri.conf.json` found one level below the directory
- **Electron** — `package.json` with `electron` in dependencies or devDependencies
- **Flutter** — `pubspec.yaml` at project root
- **.NET** — `.csproj` at project root
- **Rust** — `Cargo.toml` at project root
- **C++** — `CMakeLists.txt` at project root

The search skips commonly ignored directories (node_modules, bin, obj, .git, etc.). When a compatible project is found, subdirectories below it are not searched.

- If a directory argument is provided (e.g., `winapp init .` or `winapp init path/to/project`), the search is skipped and `init` checks only that directory for a compatible project
- If `--use-defaults` (or `--no-prompt`) is set without a directory argument, `init` skips the search and initializes the current directory non-interactively, warning first if no known project type is detected there (e.g., `winapp init --use-defaults`)
- In non-interactive environments (piped stdin, CI, redirected input), `init` automatically uses `--use-defaults` behavior and emits a warning: `Non-interactive environment detected. Using default values.`
- If the current directory is a compatible project, `init` proceeds immediately
- If exactly one project is found elsewhere, you're prompted to confirm
- If multiple projects are found, you can select which one to initialize — the current directory is always available as a fallback option
- If no projects are found, you're warned and asked whether to proceed anyway
- If the search reaches the 10-project limit, a warning suggests providing a directory argument

**Automatic .NET project flow:**

When a `.csproj` file is found in the target directory, `init` uses a streamlined .NET-specific flow:

- Validates and updates the `TargetFramework` to a Windows-compatible TFM (e.g., `net10.0-windows10.0.26100.0`)
- Adds `Microsoft.WindowsAppSDK` and `Microsoft.Windows.SDK.BuildTools` as NuGet `PackageReference` entries directly in the `.csproj`
- Generates `Package.appxmanifest`, assets, and a development certificate
- Does **not** create a `winapp.yaml` or download C++ projections (use `dotnet restore` for NuGet packages)

**Sparse identity mode (`--exe` + `--sparse`):**

Generates an identity-only [sparse package](guides/sparse.md) manifest for an existing desktop executable — the first step of the [sparse packaging workflow](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps). Unlike the full `init` flow, this **skips all SDK/package installation** (sparse identity packages have no SDK dependencies) and only generates a manifest and placeholder assets.

- Infers the package name, publisher, description, and version from the exe via `FileVersionInfo` (override with `--name`, `--publisher`, or interactively)
- Writes `appxmanifest.xml` (with the exe name substituted into `Executable`) plus an `Assets/` folder to a `sparse/` folder in the current directory (or `--output-dir`)
- Uses `--use-defaults`/`--no-prompt` to skip the interactive override prompts (CI-friendly)
- `--exe` without `--sparse` is an error

> **Assets are external.** The sparse `.msix` is identity-only: the generated `Assets/` are resolved from the app's install directory (the external content location) at runtime, **not** bundled into the `.msix`. Deploy them alongside your application.

Next steps after `winapp init --exe <exe> --sparse`: [`winapp pack <appxmanifest.xml>`](#pack) to build the identity `.msix`, then [`winapp embed-identity <exe>`](#embed-identity). See the [Sparse Packaging Guide](guides/sparse.md) for the full walkthrough.


**Examples:**

```bash
# Initialize current directory
winapp init

# Initialize with experimental packages
winapp init --setup-sdks experimental

# Initialize specific directory without prompts
winapp init ./my-project --use-defaults

# Initialize a .NET project (auto-detected from .csproj)
cd my-dotnet-app
winapp init

# Generate a sparse identity manifest for an existing exe (no SDK install)
winapp init --exe ./bin/Release/net8.0-windows/MyApp.exe --sparse --use-defaults
```

**Tip: Install SDKs after initial setup**

If you ran `init` with `--setup-sdks none` (or skipped SDK installation) and later need the SDKs:

```bash
# Re-run init to install SDKs - preserves existing files (manifest, etc.)
winapp init . --use-defaults --setup-sdks stable
```

Use `--setup-sdks preview` or `--setup-sdks experimental` for preview/experimental SDK versions.

---

### new

Create a new **WinUI** app from an official Windows App SDK `dotnet new` template. Interactive by default; automatically uses defaults in non-interactive environments.

```bash
winapp new [options]
```

**Options:**

- `-t, --template <short-name>` - Template short name (e.g. `winui`, `winui-navview`, `winui-mvvm`, `winui-lib`, `winui-unittest`, or an experimental Reactor template such as `reactor` or `reactor-mvu`). Validated against the installed pack at run time; run `winapp new --list` to see all. Default: `winui` (blank XAML app).
- `-n, --name <name>` - Name for the new app/project (default: derived from `--output`, else `WinUIApp`)
- `-o, --output <path>` - Directory to create the app in (default: `./<name>`)
- `--use-defaults`, `--no-prompt` - Do not prompt; use defaults (blank template, name from `--output`/`--name`, and keep the installed template pack rather than updating it)
- `--force` - Scaffold even if the output directory already contains files
- `--template-version <latest|installed|version>` - WinUI template pack version: `latest` installs the newest published pack, `installed` keeps whatever is already downloaded (no network), or pin an explicit version such as `1.2.3`. Default: install the latest when no pack is present, otherwise prompt to update a stale pack (kept as-is under `--use-defaults`).
- `--list` - List the available WinUI templates and exit (installs the latest pack first if none is installed)
- `--json` - Format output as JSON

**Templates:**

The pack ships two styles of WinUI app. **XAML** templates define the UI in markup with a C# code-behind. **Reactor** templates are pure C# with no XAML, using an MVU (Model-View-Update) pattern. The template list is read live from the installed pack, so it always reflects the version you have — run `winapp new --list` to see the current set. Common templates:

| Short name | Description |
|------------|-------------|
| `winui` | Minimal blank XAML app (MSIX packaging) |
| `winui-navview` | XAML NavigationView starter app |
| `winui-tabview` | XAML TabView starter app |
| `winui-mvvm` | XAML MVVM app (CommunityToolkit.Mvvm) |
| `winui-lib` | WinUI 3 class library |
| `winui-unittest` | Packaged MSTest app; tests run when it's launched |
| `reactor` | **Experimental.** Blank Reactor app — pure C#, no XAML |
| `reactor-mvu` | **Experimental.** Reactor app demonstrating the MVU pattern |
| `reactor-navview` | **Experimental.** Reactor NavigationView starter app |
| `reactor-tabview` | **Experimental.** Reactor TabView starter app |

> **Reactor templates are experimental.** They reference the prerelease `Microsoft.UI.Reactor` packages, whose APIs can change or be removed in a future release. `winapp new` marks them **(Experimental)** in `--list` and in the interactive picker, sets `"Experimental": true` in `--json`, and prints a warning after scaffolding one. They are never chosen as the default template. Reactor also requires the **.NET 10 SDK or newer**; on an older SDK `winapp new` fails up front with the version it needs rather than scaffolding a project you can't build.

Each template's canonical short name is the first alias `dotnet new` lists for it; any listed alias (e.g. `winui3`, `wasdk-single`, `winui-reactor`) is also accepted. When run inside an existing WinUI project, `dotnet new` also surfaces **item** templates (e.g. a blank page), which `winapp new` adds into the current project rather than creating a new one.

**Template pack versioning:**

`winapp new` no longer pins a specific template pack version. If no pack is installed it installs the **latest**. If an older pack is already installed it checks the feed and, when a newer one exists, **prompts** whether to update — except in non-interactive/`--use-defaults` runs, which keep the installed pack. Use `--template-version latest` to always take the newest without prompting, or `--template-version installed` to always use the downloaded pack without a network check. Passing an **explicit** version (e.g. `--template-version 1.2.3`) always installs exactly that version — reinstalling even when a newer pack is already present — so scaffolding is reproducible across machines.

> **A first run may take longer:** Installing or updating the template pack, or restoring missing Windows App SDK NuGet packages used by the selected template, can require additional downloads. This can also happen after a new Windows App SDK version is published. If scaffolding is still running after 10 seconds, `winapp new` updates its status message to indicate that packages may be downloading or restoring.

**What it does:**

- Verifies the .NET SDK is installed (fails fast with guidance if missing — `winapp` does not install toolchains)
- Installs or updates the official WinUI template pack (`Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`) on demand
- Enumerates the available templates from the installed pack and delegates scaffolding to `dotnet new <short-name>`

WinUI app templates already include Windows packaging and identity (`Package.appxmanifest`), so no separate `winapp init` step is required. For app templates, use `winapp run` to build and launch the app. The `winui-lib` template produces a class library to reference from an app project (it has no app manifest). The `winui-unittest` template is a **packaged MSTest app whose tests run when the app is launched** (`winapp run`) — not via `dotnet test`. `winapp new` scaffolds against your installed .NET SDK's target framework and prints the appropriate next step for the template you choose.

Pass the global `--verbose` (`-v`) flag to echo every underlying `dotnet` invocation (pack query, update check, install, `dotnet new list`, scaffold) along with its full output — useful for diagnosing template-pack or scaffolding issues.

**Examples:**

```bash
# Interactive: pick a template, then a name (output defaults to ./<name>)
winapp new

# List the available templates without scaffolding
winapp new --list

# One-shot with a specific template
winapp new --name MyApp --template winui-navview

# Experimental Reactor app (pure C#, no XAML) — requires the .NET 10 SDK
winapp new --name MyApp --template reactor-mvu

# Always use the newest template pack, no prompts
winapp new --name MyApp --template-version latest --use-defaults

# Show the underlying dotnet commands and their output
winapp new --name MyApp --verbose

# Non-interactive (agent) with machine-readable output
winapp new --use-defaults --name MyApp --json
```

---

### restore

Restore packages and regenerate files based on existing `winapp.yaml` configuration.

```bash
winapp restore [base-directory] [options]
```

**Arguments:**

- `base-directory` - Directory to restore (default: current directory). Also selects where `winapp.yaml` and `nuget.config` are read from unless `--config-dir` overrides it.

**Options:**

- `--config-dir <path>` - Directory containing winapp.yaml (default: base-directory)

**What it does:**

- Reads existing `winapp.yaml` configuration
- Downloads/updates SDK packages to specified versions
- Regenerates C++/WinRT headers and binaries
- Stores shareable files in the global cache directory

> [!NOTE]
> For .NET projects there is no `winapp.yaml` — the SDK versions live as `PackageReference` entries in the `.csproj` — so `winapp restore` runs `dotnet restore` for you.

**Examples:**

```bash
# Restore from winapp.yaml in current directory
winapp restore

# Restore a specific project directory (reads ./my-project/winapp.yaml)
winapp restore ./my-project
```

**Custom and private NuGet feeds:**

`winapp init`, `restore`, and `update` download the Windows SDK and Windows App SDK packages through NuGet, honoring your standard [`nuget.config`](https://learn.microsoft.com/nuget/reference/nuget-config-file) hierarchy. Private feeds and mirrors, feed credentials (including credential providers), and a custom `globalPackagesFolder` all work as they do for `dotnet restore`. To restore exclusively from your own mirror, `<clear />` the inherited sources and add just yours:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="contoso" value="https://pkgs.dev.azure.com/contoso/_packaging/winsdk-mirror/nuget/v3/index.json" />
  </packageSources>
</configuration>
```

> [!NOTE]
> For native projects winapp resolves `nuget.config` from the directory it operates on: the `init`/`restore` directory argument, `--config-dir` when given, otherwise the current directory. For **.NET projects** the sources come from the project's own `nuget.config` hierarchy instead, because that is what `dotnet add package` and `dotnet restore` use, so put a private feed's config in the project directory or an ancestor. A `--config-dir` outside that hierarchy is reported and ignored rather than silently selecting versions the project cannot restore. Run these commands only against directories you trust, the same caution that applies to `dotnet restore`. When several sources are configured, use [Package Source Mapping](https://learn.microsoft.com/nuget/consume-packages/package-source-mapping) to pin each package to a feed.

---

### update

Update packages to their latest versions and update the configuration file.

```bash
winapp update [options]
```

**Options:**

- `--setup-sdks <stable|preview|experimental|none>` - SDK installation mode: `stable` (default), `preview`, `experimental`, or `none` (skip SDK installation)

**What it does:**

- Reads existing `winapp.yaml` configuration in the current directory
- Updates all packages to their latest available versions
- Updates the `winapp.yaml` file with new version numbers
- Regenerates C++/WinRT headers and binaries

**Examples:**

```bash
# Update packages to latest versions
winapp update

# Update including experimental packages
winapp update --setup-sdks experimental
```

---

### pack

Create MSIX packages from a project or prepared application directories. Requires a manifest file (`Package.appxmanifest` preferred, `appxmanifest.xml` also supported) to be present in the target directory, in the current directory, or passed with the `--manifest` option. (run `init` or `manifest generate` to create a manifest)

Pass a single `.csproj` to build the project and package its output in one step (**project mode**, see [Packaging a project directly](#packaging-a-project-directly) below). Pass multiple input folders to create an `.msixbundle` for multi-architecture distribution (see [Multi-architecture bundles](#multi-architecture-bundles) below).

```bash
winapp pack <input-folder> [input-folder...] [options]
```

**Arguments:**

- `input-folder` - A single `.csproj` to build and package (project mode), or one or more directories containing the application files to package. Pass multiple folders (e.g., `./publish/x64 ./publish/arm64`) to create an MSIX bundle. For **sparse identity packages**, pass a sparse `appxmanifest.xml` file directly instead of a folder (see [Sparse identity packages](#sparse-identity-packages) below).

**Options:**

- `--output <filename>` - Output file name. For single packages: `<name>_<version>_<arch>.msix` (falling back to `<name>_<version>.msix`, `<name>_<arch>.msix`, or `<name>.msix`). For bundles: `<name>_<version>_<arch1>_<arch2>.msixbundle`.
- `--name <name>` - Package name (default: from manifest)
- `--manifest <path>` - Path to manifest file (`Package.appxmanifest` preferred, `appxmanifest.xml` also supported; default: auto-detect)
- `--cert <path>` - Path to signing certificate (enables auto-signing)
- `--cert-password <password>` - Certificate password (default: "password")
- `--generate-cert` - Generate a new development certificate
- `--install-cert` - Install certificate to machine
- `--publisher <name>` - Publisher for certificate generation. Accepts a full X.500 distinguished name or a bare name (automatically wrapped as `CN=<name>`)
- `--self-contained` - Bundle Windows App SDK runtime
- `--skip-pri` - Skip PRI file generation
- `--executable <path>` - Path to the executable relative to the input folder (also `--exe`). Used to resolve `$targetnametoken$` placeholders in the manifest.

**Project-mode options** (a `.csproj` input only; ignored for folder/bundle/manifest inputs):

- `--configuration <name>` (`-c`) - Build configuration (default: `Debug`)
- `--arch <arch>` - Target architecture: `x64`, `arm64`, or `x86` (default: the current process architecture)
- `--runtime <rid>` (`-r`) - Target .NET runtime identifier (e.g. `win-x64`); uses only the RID's architecture and overrides `--arch`
- `--framework <tfm>` (`-f`) - Target framework moniker for multi-targeted projects
- `--no-build` - Package the existing build output without rebuilding
- `--no-restore` - Skip restoring the project before building
- `--property <name=value>` (`-p`) - MSBuild property, forwarded to build and evaluation (repeatable)

**What it does:**

- Validates and processes Package.appxmanifest files
- Resolves `$placeholder$` tokens in the manifest (see [Manifest placeholders](#manifest-placeholders) below)
- Ensures proper framework dependencies
- Updates side-by-side manifests with registrations
- Automatically discovers and bundles any non-image files referenced in the manifest (e.g., AppExtension `manifest.json`, config files) from the manifest directory or input folder if they are missing from staging
- Automatically discovers third-party WinRT components and registers their activatable classes (see [WinRT component discovery](#winrt-component-discovery) below)
- Handles self-contained WinAppSDK deployment
- Signs package if certificate provided

#### Packaging a project directly

When the input is a single `.csproj`, `winapp pack` builds the project (using the options above) and packages the resulting output — no need to build separately or locate the output folder first. This mirrors `winapp run`'s project mode.

```bash
# Build MyApp in Release for arm64 and package + sign it in one step
winapp pack ./MyApp.csproj -c Release --arch arm64 --cert ./devcert.pfx

# Package an existing build output without rebuilding
winapp pack ./MyApp.csproj --no-build
```

The project must build as a packaged app (`EnableMsixTooling=true` with a `Package.appxmanifest`); a project that builds as an unpackaged app (`WindowsPackageType=None`) has no MSIX manifest to package and `winapp pack` reports an actionable error. Folder, bundle, and sparse-manifest inputs are unchanged.

#### Sparse identity packages

When the input is a **sparse `appxmanifest.xml` file** (one declaring `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>` under `<Properties>`) rather than a folder, `winapp pack` builds an **identity-only** `.msix` — it packages just the manifest, with no application binaries or assets. This is step 2 of the [sparse packaging workflow](guides/sparse.md).

```bash
# Build a signed identity package from a sparse manifest
winapp pack ./sparse/appxmanifest.xml --cert ./devcert.pfx
```

- Output defaults to `<PackageName>.identity.msix` in the current directory (override with `--output`).
- Signing happens only when `--cert` (or `--generate-cert`) is provided.
- If you instead pass a **folder** whose manifest declares `AllowExternalContent`, the existing folder-packaging behavior applies, but `winapp pack` warns if it finds assets (`.png`/`.jpg`/`.ico`) or binaries (`.exe`/`.dll`/`.so`) — for sparse packages these belong at the external location, not inside the `.msix`.

After packing, run [`winapp embed-identity <exe>`](#embed-identity) and register the package in your installer with `Add-AppxPackage -Path <msix> -ExternalLocation <install-dir>`. See the [Sparse Packaging Guide](guides/sparse.md).


#### WinRT component discovery

When packaging, `winapp pack` automatically scans NuGet packages defined in the `winapp.yaml` or `*.csproj` for third-party WinRT components (e.g., Win2D). It parses `.winmd` files to extract activatable class names and locates their implementation DLLs. The discovered entries are registered as follows:

- **Framework-dependent** (default): Activatable classes are added as `<InProcessServer>` entries in the `Package.appxmanifest`
- **Self-contained** (`--self-contained`): Activatable classes are embedded in side-by-side (SxS) manifests within the executable

**Placeholder resolution during packaging:**

If the manifest contains `$targetnametoken$` in the `Executable` attribute:
1. If `--executable` is provided (path relative to the input folder), the placeholder is replaced with the specified value
2. Otherwise, `winapp pack` scans the input folder root for `.exe` files — if exactly one is found, it is used automatically
3. If zero or multiple `.exe` files are found, an error is shown asking you to specify `--executable`

**Examples:**

```bash
# Package directory with auto-detected manifest
winapp pack ./dist

# Package with custom output name and certificate
winapp pack ./dist --output MyApp.msix --cert ./cert.pfx

# Package with generated and installed certificate and self-contained WinAppSDK runtime
winapp pack ./dist --generate-cert --install-cert --self-contained

# Package with explicit executable (resolves $targetnametoken$ in manifest)
winapp pack ./dist --executable MyApp.exe
```

#### Multi-architecture bundles

When multiple input folders are passed, `winapp pack` creates an `.msixbundle` containing one `.msix` per architecture:

```bash
# Create unsigned bundle for Microsoft Store submission
winapp pack ./publish/x64 ./publish/arm64

# Create signed bundle for sideloading
winapp pack ./publish/x64 ./publish/arm64 --cert ./devcert.pfx

# Self-contained bundle
winapp pack ./publish/x64 ./publish/arm64 --self-contained --generate-cert
```

The command auto-detects each folder's architecture from the primary executable's PE header, validates consistency across slices (Identity, Capabilities, Dependencies), and produces a `<Name>_<Version>_<arch1>_<arch2>.msixbundle`.

**Manifest resolution for bundles:**

Each slice in the bundle needs a manifest. The command resolves manifests in this order:

1. **`--manifest <path>`** — If specified, this single manifest is used for all slices. The `ProcessorArchitecture` is automatically updated per-slice to match the detected architecture.

2. **Per-folder manifest** — If each input folder contains a `Package.appxmanifest` (or `appxmanifest.xml`), that folder's manifest is used for its slice.

3. **Current directory fallback** — If a folder has no manifest, the command looks for `Package.appxmanifest` in the current working directory and uses it (with architecture auto-stamped).

In all cases, the manifest is automatically updated: placeholders are resolved, dependencies are injected, and the `ProcessorArchitecture` is force-set to the detected architecture. After resolution, a cross-slice validation ensures that Identity (Name, Version, Publisher), Capabilities, and Dependencies are consistent across all slices — only `ProcessorArchitecture` may differ.
The package version defined in the slices is atributed to the MSIX bundle version, except if it's `0.0.0.0`, in which case a timestamp-based version is automatically generated.

```bash
# Option 1: Single shared manifest (simplest for most projects)
# Place Package.appxmanifest in your project root and run from there
winapp pack ./publish/x64 ./publish/arm64

# Option 2: Explicit manifest path
winapp pack ./publish/x64 ./publish/arm64 --manifest ./src/Package.appxmanifest

# Option 3: Per-folder manifests (useful if slices have different app extensions)
# Each folder already contains its own Package.appxmanifest
winapp pack ./publish/x64 ./publish/arm64
```

---

### create-debug-identity

Create app identity for debugging using [sparse packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps). The exe stays in its original location — Windows associates identity with it via `Add-AppxPackage -ExternalLocation`.

> **When to use this vs `winapp run`:** Use `create-debug-identity` when the exe is **separate from your app code** (e.g., Electron apps where `electron.exe` is in `node_modules`), or when specifically testing sparse package behavior. For most frameworks where the exe is in your build output folder, use [`winapp run`](#run) instead — it registers a full loose layout package and launches the app. See the [Debugging Guide](debugging.md) for a full comparison.

```bash
winapp create-debug-identity [entrypoint] [options]
```

**Arguments:**

- `entrypoint` - Path to executable (.exe) or script that needs identity

**Options:**

- `--manifest <path>` - Path to the app manifest file, either `Package.appxmanifest` or `appxmanifest.xml` (default: auto-detect `Package.appxmanifest` or `appxmanifest.xml` in the current directory)
- `--no-install` - Don't install the package after creation
- `--keep-identity` - Keep the manifest identity as-is, without appending `.debug` to the package name and application ID

**What it does:**

- Modifies executable's side-by-side manifest
- Registers sparse package for identity
- Enables debugging of identity-requiring APIs

**Examples:**

```bash
# Add identity to executable using local manifest
winapp create-debug-identity ./bin/MyApp.exe

# Add identity with custom manifest location
winapp create-debug-identity ./dist/app.exe --manifest ./custom-manifest.xml

# Create identity for hosted app script
winapp create-debug-identity app.py
```

---

### embed-identity

Connect a desktop application to its **sparse identity package** by embedding the `<msix>` element into the app's side-by-side (fusion) manifest. This is step 3 of the [sparse packaging workflow](guides/sparse.md) — it tells Windows which identity package the running exe belongs to.

```bash
winapp embed-identity <target> [options]
```

**Arguments:**

- `target` - The file to update. Auto-detected by extension:
  - **`.exe`** (EXE mode) — embeds the `<msix>` element directly into the exe's side-by-side manifest using `mt.exe`.
  - **`.xml` / `.manifest`** (XML mode) — inserts or replaces the `<msix>` element in an external SxS manifest file (created if it doesn't exist). Rebuild your app afterward so the updated manifest is embedded in the binary.

**Options:**

- `--manifest <path>` - Path to the sparse `appxmanifest.xml` to read identity (packageName, publisher, applicationId) from. When omitted, the command searches a `sparse/` folder beside the target first, then in the current directory, then the target's directory and the current directory, for `appxmanifest.xml`.

**Examples:**

```bash
# EXE mode — embed identity straight into the built exe
winapp embed-identity ./bin/Release/net8.0-windows/MyApp.exe

# XML mode — update a checked-in side-by-side manifest, then rebuild
winapp embed-identity ./app.manifest --manifest ./appxmanifest.xml
```

> This command is idempotent: re-running it replaces any existing `<msix>` element rather than duplicating it.

---

### manifest

Generate and manage Package.appxmanifest files.

#### manifest generate

Generate Package.appxmanifest from templates.

```bash
winapp manifest generate [directory] [options]
```

**Arguments:**

- `directory` - Directory to generate manifest in (default: current directory)

**Options:**

- `--package-name <name>` - Package name (default: folder name)
- `--publisher-name <name>` - Publisher distinguished name (default: CN=\<current user\>). Accepts any valid X.500 DN; bare names are auto-wrapped as CN=\<name\>.
- `--version <version>` - Version (default: "1.0.0.0")
- `--description <text>` - Description (default: "My Application")
- `--entrypoint <path>` - Entry point executable or script
- `--template <type>` - Template type: `packaged` (default) or `sparse`
- `--logo-path <path>` - Path to logo image file
- `--if-exists <Error|Overwrite|Skip>` - Behavior when the manifest file already exists at the target path (default: `Error`)

**Templates:**

- `packaged` - Standard packaged app manifest
- `sparse` - App manifest using [sparse/external location packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)

#### Manifest placeholders

Generated manifests use `$placeholder$` tokens (dollar-sign delimited) that are resolved automatically at packaging time:

| Placeholder | Resolved to | Example |
|-------------|-------------|---------|
| `$targetnametoken$` | Executable name without extension | `Executable="$targetnametoken$.exe"` &rarr; `Executable="MyApp.exe"` |
| `$targetentrypoint$` | `Windows.FullTrustApplication` | Always resolved automatically |

This follows the same convention used by Visual Studio project templates, so manifests are portable across tooling.

**How placeholders are resolved:**

- **`winapp pack`** — During packaging, `$targetnametoken$` is resolved using the `--executable` option or by auto-detecting the single `.exe` in the input folder. If multiple (or zero) `.exe` files are found and `--executable` is not specified, an error is shown.
- **`winapp create-debug-identity`** — When an entrypoint argument is provided, `$targetnametoken$` is resolved from it. Without an entrypoint, the executable placeholder must already be resolved in the manifest.
- **`winapp manifest generate --executable`** — When `--executable` is provided, manifest metadata (version, description) and icons are extracted from the executable, but the generated manifest still uses `$targetnametoken$.exe`; this placeholder is resolved later (e.g. `winapp pack` or `winapp create-debug-identity`).

> **PS:** Keeping `$targetnametoken$` in your checked-in manifest avoids hard-coding executable names and works with both `winapp pack` and Visual Studio builds.

**Examples:**

```bash
# Generate standard manifest interactively
winapp manifest generate

# Generate with all options specified
winapp manifest generate ./src --package-name MyApp --publisher-name "CN=My Company" --if-exists overwrite
```

#### manifest add-alias

Add an execution alias (`uap5:AppExecutionAlias`) to a Package.appxmanifest. This allows launching the packaged app from the command line by typing the alias name.

```bash
winapp manifest add-alias [options]
```

**Options:**

- `--name <alias>` - Alias name (e.g. `myapp.exe`). Default: inferred from the `Executable` attribute in the manifest.
- `--manifest <path>` - Path to Package.appxmanifest (default: search current directory)
- `--app-id <id>` - Application Id to add the alias to (default: first Application element)

**What it does:**

- Reads the manifest and infers the alias from the `Executable` attribute (preserving placeholders like `$targetnametoken$.exe`)
- Adds the `uap5` namespace declaration if not already present
- Adds an `<Extensions>` block with `<uap5:AppExecutionAlias>` inside the target Application element
- If the alias already exists, reports it and exits successfully

**Examples:**

```bash
# Add alias inferred from Executable attribute (e.g. $targetnametoken$.exe)
winapp manifest add-alias

# Add alias with explicit name
winapp manifest add-alias --name myapp.exe

# Add alias to specific manifest
winapp manifest add-alias --manifest ./dist/Package.appxmanifest
```

#### manifest update-assets

Generate all required MSIX image assets from a single source image.

```bash
winapp manifest update-assets <image-path> [options]
```

**Arguments:**

- `image-path` - Path to source image file (PNG, JPG, SVG, ICO, GIF, BMP, etc.)

**Options:**

- `--manifest <path>` - Path to Package.appxmanifest file (default: search current directory)
- `--light-image <path>` - Path to a separate source image for light theme variants

**Description:**

Takes a single source image and generates a comprehensive set of MSIX image assets based on the manifest's asset references:

For each asset referenced in the manifest:
- **5 scale variants** — base (no suffix), `.scale-125`, `.scale-150`, `.scale-200`, `.scale-400`

For the app icon (Square44x44Logo / AppList, 44×44 base):
- **14 plated targetsize variants** — `.targetsize-{16,20,24,30,32,36,40,48,60,64,72,80,96,256}`
- **14 unplated targetsize variants** — `.targetsize-{size}_altform-unplated`

Additionally:
- **app.ico** — Multi-resolution ICO file (16, 24, 32, 48, 256) for shell integration. If an existing `.ico` file is found in the assets directory (e.g. `AppIcon.ico` from a project template), it is replaced in-place rather than creating a duplicate

With `--light-image`:
- **Light theme targetsize variants** — `.targetsize-{size}_altform-lightunplated` (app icon)
- **Light theme scale variants** — `.scale-{factor}_altform-colorful_theme-light` (tiles, store logo)

**SVG support:** SVG files are fully supported as source images. They are rendered as vectors directly at each target size, producing pixel-perfect results at all resolutions.

The command scales images proportionally while maintaining aspect ratio, centering them with transparent backgrounds when needed. Assets are saved to the `Assets` directory relative to the manifest location.

**Examples:**

```bash
# Generate assets with auto-detected manifest
winapp manifest update-assets mylogo.png

# Use an SVG source for best quality at all sizes
winapp manifest update-assets mylogo.svg

# Specify manifest location explicitly
winapp manifest update-assets mylogo.png --manifest ./dist/Package.appxmanifest

# Generate light theme variants from a separate image
winapp manifest update-assets mylogo.png --light-image mylogo-light.png

# Use the same image for both (generates all MRT light theme qualifiers)
winapp manifest update-assets mylogo.png --light-image mylogo.png

# With verbose output
winapp manifest update-assets mylogo.png --verbose
```

---

### run

Create a loose layout package from a build output folder, register it with Windows using the `Windows.Management.Deployment.PackageManager` API, and launch the application — simulating a full MSIX install for debugging. Returns the process ID for debugger attachment.

`winapp run` operates in one of three modes, chosen automatically from the input:

- **Folder mode** — the input is a build-output folder (contains a `Package.appxmanifest`/`AppxManifest.xml`).
- **Project mode** — the input is a `.csproj`, a `.sln`/`.slnx` solution, or a directory containing one. `winapp run` builds the project and launches it, supporting both **packaged** and **unpackaged** WinUI apps. See [Project mode](#project-mode-net-sdk-projects) below.
- **Single-file mode** — the input is a `.cs` [.NET file-based app](#single-file-mode-net-file-based-apps). `winapp run` builds it, generates a manifest from its `#:property` directives, and launches it with package identity.

> [!TIP]
> Mode selection is silent by default. If a directory was treated as a build-output folder when you
> expected it to be built as a project, re-run with `--verbose` — folder mode reports why it was
> chosen (`No .csproj/.sln/.slnx with a runnable app found in '<path>' — running it as a
> build-output folder.`). A directory is only built as a project when a `.csproj`/`.sln`/`.slnx`
> with a runnable app sits at its **top level**; it is not searched recursively.

> **This is the preferred command for debugging with package identity** for most frameworks (.NET, C++, Rust, Flutter, Tauri). Unlike [`create-debug-identity`](#create-debug-identity) which registers a sparse package for a single exe, `winapp run` registers the entire folder as a loose layout package, just like a real MSIX install. See the [Debugging Guide](debugging.md) for common debugging workflows.

```bash
winapp run [<input>] [options]
```

**Arguments:**

- `input` - The app to run: a build-output folder (folder mode), a `.cs` .NET file-based app (single-file mode), a `.csproj` project, a `.sln`/`.slnx` solution, or a directory containing one of those at its top level (project mode; the directory is not searched recursively). Use `.` to build/run the project in the current directory. **Optional — defaults to the current directory when omitted** (matches `dotnet run`).

**Options:**

- `--manifest <path>` - Path to Package.appxmanifest (default: auto-detect from input folder or current directory)
- `--output-appx-directory <path>` - Output directory for the loose layout package (default: `AppX` inside the input folder directory)
- `--args <string>` - Command-line arguments to pass to the application. Alternatively, use `--` followed by arguments to avoid escaping (e.g., `winapp run . -- --flag value`).
- `--no-launch` - Only create the debug identity and register the package without launching the application
- `--with-alias` - Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Rarely needed: an app with `OutputType=Exe` already launches this way by default. winapp adds the required `uap5:ExecutionAlias` to the manifest it stages in the AppX layout, so no change to your checked-in manifest is needed; an alias the app declares itself is used as-is. Cannot be combined with `--no-launch`, `--detach`, `--without-alias`, or `--json`.
- `--without-alias` - Force AUMID activation for an app that would otherwise launch through an execution alias. A console app then runs without a console and prints nothing to this terminal. Cannot be combined with `--with-alias`.
- `--debug-output` - Capture `OutputDebugString` messages and first-chance exceptions from the launched application. Framework noise (WinUI, COM, DirectX) is filtered from console output; the full log file captures everything. If the app crashes, automatically captures a minidump and analyzes it to show the exception type, message, and stack trace with source file:line numbers (resolved from PDBs in the build output folder). Managed (.NET) crashes are analyzed instantly with no external tools. Native (C++/WinRT) crashes show module names and offsets. When the crashed app is a WinUI 3 app (`Microsoft.UI.Xaml.dll` is loaded), an extra stowed-exception triage pass runs automatically to surface the originating HRESULT, its ErrorContext chain, and the full native XAML dispatch stack; the required debugger components are downloaded on first use (see [Debugging](debugging.md#winui-stowed-exception-triage), overridable via the `WINAPP_DBGTOOLS_DIR` environment variable). Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use `--no-launch` instead if you need to attach a different debugger. Cannot be combined with `--no-launch`. Cannot be combined with `--json`.
- `--symbols` - Download PDB symbols from Microsoft Symbol Server for richer native crash analysis with resolved function names. Only used with `--debug-output`. If omitted and a native crash occurs, the output will suggest adding this flag. This flag also improves the WinUI stowed-exception triage stack for WinUI 3 apps. First run downloads symbols and caches them locally; subsequent runs use the cache.
- `--unregister-on-exit` - Unregister the development package after the application exits. Only removes packages registered in development mode. Cannot be combined with `--no-launch`.
- `--detach` - Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with `--json`). Cannot be combined with `--no-launch`, `--debug-output`, `--with-alias`, or `--unregister-on-exit`.
- `--clean` - Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments.
- `--json` - Format output as JSON for programmatic consumption (e.g. CI/automation). Useful with `--detach` to capture the PID. Cannot be combined with `--with-alias` or `--debug-output`.

**Application data persistence:**

By default, `winapp run` preserves your application's data (`LocalState`, `RoamingState`, `Settings`, etc.) when re-deploying. If your app writes data to `ApplicationData.Current.LocalFolder` or `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` within the package context, that data will survive across `winapp run` invocations.

Use `--clean` when you need a fresh start (e.g., to reset corrupted state or test first-run behavior).

**What it does:**

- Locates or generates the Package.appxmanifest
- Creates and registers a debug identity using a loose layout package
- Computes the Application User Model ID (AUMID)
- Launches the application using the registered identity (unless `--no-launch` is specified)
- Prints the process ID (PID) for debugger attachment

**Examples:**

```bash
# Register debug identity and launch app from build output
winapp run ./bin/Debug

# Launch with custom manifest and arguments
winapp run ./dist --manifest ./out/Package.appxmanifest --args "--my-flag value"

# Pass arguments after -- to avoid escaping (equivalent to --args)
winapp run ./bin/Debug -- --my-flag value

# Specify output directory for loose layout package
winapp run ./bin/Release --output-appx-directory ./AppXDebug

# Register identity without launching
winapp run ./bin/Debug --no-launch

# Launch via execution alias (console apps run in current terminal)
winapp run ./bin/Debug --with-alias

# Launch and capture OutputDebugString messages and crash diagnostics
winapp run ./bin/Debug --debug-output

# Download native symbols for richer crash analysis (C++/WinRT crashes)
winapp run ./bin/Debug --debug-output --symbols

# Combine with execution alias to debug console apps inline
winapp run ./bin/Debug --with-alias --debug-output

# Run and automatically clean up registration on exit
winapp run ./bin/Debug --with-alias --unregister-on-exit

# Launch and detach immediately (useful for CI/automation)
winapp run ./bin/Debug --detach

# Detach with JSON output (returns PID for scripting)
winapp run ./bin/Debug --detach --json

# Wipe application data (LocalState, settings) and start fresh
winapp run ./bin/Debug --clean
```

#### Project mode (.NET SDK projects)

When the input is a `.csproj`, a `.sln`/`.slnx` solution, or a directory containing one (including `.`), `winapp run` **builds the project** with `dotnet build` and then launches it. It supports both packaged and unpackaged WinUI apps, and installs the matching-architecture Windows App Runtime the app needs before launching.

**Solution input:** point `winapp run` at a `.sln`/`.slnx` (or a directory containing one — a solution is preferred over loose `.csproj` files) and it resolves the runnable app project, then builds it with `$(SolutionDir)` and the sibling `Solution*` properties defined, so projects that depend on them build as they do in Visual Studio. Resolution rules:

- **Test projects are skipped** when auto-selecting, so a solution containing an app plus its tests resolves to the app with no `--project` needed. (A WinUI test project is itself a packaged app, so output type alone can't distinguish it.)
- **If the only runnable project is a test project**, it runs.
- **If more than one runnable app project exists**, `winapp run` does not guess a startup project — it errors listing the candidates. Use `--project <name>` to choose, which is always honored, including to select a test project.

Packaged vs. unpackaged is detected automatically from the project's effective `WindowsPackageType` MSBuild property (never from manifest presence):

- **Packaged** (`WindowsPackageType=MSIX`, the WinUI packaged default) — builds, then registers the build output as a loose-layout package and launches via AUMID (the same pipeline as folder mode).
- **Unpackaged** (`WindowsPackageType=None`) — builds, ensures the framework-dependent Windows App Runtime is installed, then launches the built `.exe` directly. Force this for a packaged project with `-p WindowsPackageType=None`.

Project mode requires the **.NET SDK 8.0.100 or newer** (for MSBuild `--getProperty`).

**Project-mode options** (ignored in folder mode):

- `-c, --configuration <name>` - Build configuration. Default: `Debug`. *(Also honored in single-file mode.)*
- `--arch <x64|arm64|x86>` - Target architecture. Default: the current process architecture. Determines the build RID and Windows App Runtime architecture, and selects a matching platform-dependent publish profile when required by the effective build. *(Also honored in single-file mode.)*
- `-r, --runtime <rid>` - Target .NET runtime identifier (e.g. `win-x64`). Project mode uses only the RID's architecture, always builds the canonical `win-<arch>`, and rejects non-Windows RIDs (e.g. `linux-x64`). Its architecture overrides `--arch` and can select the required publish profile. *(Also honored in single-file mode, where it overrides a `#:property RuntimeIdentifier` declared by the file.)*
- `-f, --framework <tfm>` - Target framework moniker for multi-targeted projects (e.g. `net10.0-windows10.0.26100.0`). *(Rejected in single-file mode — use `#:property TargetFramework=...`.)*
- `--project <name-or-path>` - When the input is a solution (`.sln`/`.slnx`) or a directory with multiple runnable app projects, selects which project to launch (by project name or path). *(Rejected in single-file mode — a `.cs` file-based app is itself the project.)*
- `--no-build` - Skip building and run the existing build output (still evaluates output properties). *(Also honored in single-file mode.)*
- `--no-restore` - Skip restoring before building. *(Also honored in single-file mode.)*
- `-p, --property <Name=Value>` - MSBuild property, forwarded to both the build and the property evaluation. Repeat `-p` for multiple properties; use `%3B` or `%2C` for a literal semicolon or comma in a value. *(Also honored in single-file mode, where it is the only way to set `TargetFramework`.)*

**Build output & verbosity:** restore and build output stream live. Displayed commands and output redact credentials from authenticated feed URLs. Use the verbosity options below to control what is shown:

| Flag | dotnet verbosity | Adds |
|------|------------------|------|
| *(default)* | `minimal` | — |
| `--verbose` | `minimal` | winapp's build decision traces |
| `--quiet` | `quiet` | — |

Under `--json`, each invocation and its child output go to stderr so stdout stays pure JSON. Under `--quiet`, invocations are suppressed and dotnet's quiet restore/build output is routed to stderr so stdout stays clean.

**Option applicability:** the identity/loose-layout options (`--manifest`, `--output-appx-directory`, `--no-launch`, `--with-alias`, `--unregister-on-exit`, `--clean`, `--executable`) apply to packaged apps only. They are rejected with a clear error for unpackaged apps (which have no MSIX package). Launch/debug options (`--args`/`--`, `--detach`, `--debug-output`, `--symbols`, `--json`) work in both.

**Project-mode examples:**

```bash
# Build and run the project in the current directory (input defaults to ".")
winapp run

# Run a specific project
winapp run ./src/MyApp/MyApp.csproj

# Build and run from a solution (resolves the runnable app project, defines $(SolutionDir))
winapp run ./MyApp.sln

# Pick a startup project when the solution has more than one runnable app
winapp run ./MyApp.sln --project MyApp

# Release build for arm64
winapp run . -c Release --arch arm64

# Force an unpackaged run of a packaged project
winapp run . -p WindowsPackageType=None

# Run the existing build output without rebuilding, and capture crash diagnostics
winapp run . --no-build --debug-output

# Show winapp's build decision traces (dotnet build stays at minimal verbosity)
winapp run . --verbose

# Launch and detach (prints PID), forwarding args to the app
winapp run . --detach -- --my-flag value
```

#### Single-file mode (.NET file-based apps)

.NET 10 lets you run a single `.cs` file with no project file, configuring it with `#:` directives at
the top. Point `winapp run` at that file and it builds the app, generates an appxmanifest for it, and
launches it **with package identity** — so `Windows.ApplicationModel.Package.Current` works, the app
gets a real AUMID and a Start-menu entry, and the APIs that simply require identity (app notifications,
`ApplicationData`, on-device AI) work.

Shell integrations such as protocol handlers, file associations, share targets, and startup tasks need
a declared `<Extensions>` entry, which the generated manifest does not contain. To add one, author your
own manifest — see [Bring your own manifest](#bring-your-own-manifest) below.

```bash
winapp run counter.cs
```

You do not author a manifest. Describe the package with `#:property` directives instead:

```csharp
#:package Microsoft.UI.Reactor@0.1.0-preview.13
#:property OutputType=WinExe
#:property TargetFramework=net10.0-windows10.0.22621.0
#:property UseWinUI=true
#:property RuntimeIdentifier=win-x64

#:property WinAppPackageName=com.contoso.counter
#:property WinAppDisplayName=Contoso Counter
#:property WinAppDescription=Counts things, one click at a time
#:property Version=1.2.3

using static Microsoft.UI.Reactor.Factories;
ReactorApp.Run<MyApp>("Hello");
```

**Manifest properties.** All are optional; each falls back to a sensible default:

| Property | Sets | Default |
|----------|------|---------|
| `WinAppPackageName` | `Identity/@Name` (the package identity) | the file name, sanitized to `[-.A-Za-z0-9]`, plus a short hash of the file's path (`counter.cs` → `counter-a1b2c3d4`) |
| `WinAppDisplayName` | The name shown in Start and Settings | the file name without its extension |
| `WinAppPublisher` | `Identity/@Publisher` | `CN=<your Windows user name>`. A bare name is wrapped as `CN=<name>`. |
| `WinAppVersion` | `Identity/@Version` | `$(Version)`, normalized (see below) |
| `WinAppDescription` | The description shown during install and in Settings | the display name |
| `WinAppCapabilities` | Capabilities to declare, separated by `;` or `,` | none |

**Version.** A package version must be exactly four numbers, each 0–65535. `WinAppVersion` (or, if you
don't set it, the standard `Version` property) is normalized to fit: any `-preview`/`-rc` suffix is
dropped and missing components are filled with zeros, so `#:property Version=1.2.3-preview.4` becomes
`1.2.3.0` and sets your assembly version and package version together. A value that can't be made to
fit — a component above 65535, or more than four components — is **rejected with an error** rather
than silently changed.

##### Capabilities

Your app runs full-trust with identity, which satisfies APIs that only require a packaged app. But some
APIs are gated on a declared capability regardless — the Windows AI APIs are the common case. (Shell
integrations such as protocol handlers and file associations are a third case: those need authored
`<Extensions>` entries, not a capability, so use [your own manifest](#bring-your-own-manifest) for
them.)

```csharp
#:property WinAppCapabilities=systemAIModels
```

That is all Phi Silica and the other on-device model APIs need from the manifest. Declare several by
separating them:

```csharp
#:property WinAppCapabilities=systemAIModels;internetClient;microphone
```

winapp writes each one into the element and XML namespace it actually requires, declares that
namespace, and raises `MaxVersionTested` when the capability needs a newer one. This matters more than
it sounds: capabilities are spread across several different elements, and the same list above becomes
three *different* shapes —

```xml
<systemai:Capability Name="systemAIModels" />
<Capability Name="internetClient" />
<DeviceCapability Name="microphone" />
```

Names winapp knows are written for you. For anything else — the restricted set grows over time —
qualify it yourself with the namespace prefix:

| Prefix | Emits |
|--------|-------|
| `rescap:` | `<rescap:Capability>` — restricted capabilities |
| `uap:`, `uap6:`, `uap7:`, `uap11:` | `<uap*:Capability>` |
| `systemai:` | `<systemai:Capability>` |
| `device:` | `<DeviceCapability>` |
| `app:` | `<Capability>` in the default namespace |

```csharp
#:property WinAppCapabilities=rescap:broadFileSystemAccess
```

An unrecognized bare name is **rejected with an error** naming these prefixes, rather than guessed at —
a capability emitted in the wrong namespace produces a manifest Windows either refuses to register or
accepts while silently not granting it.

##### Bring your own manifest

If you need something the properties don't cover — a protocol handler, a file association, an execution alias — author a manifest
and `winapp run` will use it verbatim instead of generating one. It is picked up from, in order:

1. `--manifest <path>` on the command line.
2. `#:property WinAppManifestPath=<path>` in the `.cs` file.
3. A manifest sitting next to the `.cs` file, named `<filename>.appxmanifest` (for example
   `counter.appxmanifest` beside `counter.cs`).

Only that per-file name is picked up automatically. A `Package.appxmanifest` or `appxmanifest.xml` in
the same folder is deliberately ignored — several `.cs` files can share a folder, and adopting a
shared name would silently run one app under another's identity. To use one manifest for several
files, name it explicitly with `--manifest` or `WinAppManifestPath`.

Otherwise a `Package.appxmanifest` is generated into the build output, along with the default image
assets, and refreshed on every run.

**Options.** Every folder-mode option works: `--no-launch`, `--with-alias`, `--without-alias`, `--detach`,
`--clean`, `--debug-output`, `--symbols`, `--unregister-on-exit`, `--args`/`--`, `--json`, `--executable`,
`--manifest`, `--output-appx-directory`, plus `-c/--configuration`, `--no-build`, `--no-restore`, and
`-p/--property`.

> [!TIP]
> **A console app prints to your terminal by default.** A packaged app launched through AUMID has no
> console, so a console-only app would run correctly and print nothing. winapp avoids that: an app with
> `OutputType=Exe` is launched through an execution alias instead, which inherits this terminal's
> stdin/stdout/stderr. You still get package identity, and you don't have to ask for it:
>
> ```bash
> winapp run counter.cs
> ```
>
> Pass `--without-alias` to force AUMID activation instead — the app then runs without a console and
> prints nothing here. A windowed app (`WinExe`) shows a window, so it keeps AUMID activation; pass
> `--with-alias` if you want one in this terminal anyway. To fix the choice in the file rather than on
> every command line, set the same property a `.csproj` uses:
>
> ```csharp
> #:property WinAppRunUseExecutionAlias=false
> ```

The alias winapp declares is named after the **package family name**, with a `winapp-` prefix — so
`com.contoso.counter` published by `CN=You` gets `winapp-com.contoso.counter_gspb8g6x97k2t.exe`. That
trailing part is the publisher hash Windows derives, so two apps sharing a name under different
publishers still get different aliases. The prefix keeps the name clear of real commands: an app in
`python.cs` gets a `winapp-…` alias, never `python.exe`. If you author your own manifest, the alias you
declare there is used as-is and winapp adds nothing.

That applies to the alias only. Registration itself is keyed on the package *name*, so running a second
app that declares the same `WinAppPackageName` under a different publisher replaces the first
registration rather than sitting alongside it. Give each app its own name if you want both registered
at once.

`winapp run` prints the alias it registered, so you don't have to compute the hash to find it.

The alias is a command on your PATH that lasts as long as the package stays registered. If some other
package already owns the name, winapp says so. When it inferred the alias for you it launches via AUMID
instead, rather than starting the wrong app; when you asked for one explicitly — with `--with-alias` or
`#:property WinAppRunUseExecutionAlias=true` — it fails instead of quietly doing something else.

Two project-mode options do **not** apply, because a file-based app configures itself. They are
rejected with a message naming the directive to use instead:

| Option | Use instead |
|--------|-------------|
| `-f/--framework` | `#:property TargetFramework=net10.0-windows10.0.22621.0` |
| `--project` | nothing — the `.cs` file *is* the project |

`--arch` and `-r/--runtime` work as they do in project mode. When you don't pass either, winapp builds
for **your machine's architecture** — which is what a self-contained Windows App SDK app needs, since
without it the SDK builds `AnyCPU` and fails with `WindowsAppSDKSelfContained requires a supported
Windows architecture`. A `#:property RuntimeIdentifier=win-arm64` in the file is respected; an explicit
`--arch`/`--runtime` overrides it.

**Packaged and unpackaged both work**, detected from the effective `WindowsPackageType` exactly as in
project mode: the default registers a loose layout and launches it with identity, while
`#:property WindowsPackageType=None` builds the app, installs the matching Windows App Runtime, and
launches the `.exe` directly. (A packaged app is launched through its execution alias or through AUMID
activation — see the console note above; that choice is separate from whether it is packaged.) The
identity options (`--no-launch`, `--with-alias`, `--without-alias`, `--clean`, `--unregister-on-exit`,
`--manifest`, `--output-appx-directory`) apply to packaged apps only.

Single-file mode requires the **.NET SDK 10.0.300 or newer**.

**The registration outlives the run.** `winapp run counter.cs` leaves the package registered after the
app exits, exactly like folder and project mode — so `LocalState` survives, and re-running the same
file reuses the same identity rather than piling up registrations. winapp says so the first time it
registers an app, and `winapp unregister` takes the `.cs` itself:

```bash
# Remove the registration (resolves the same identity `winapp run` registered)
winapp unregister counter.cs

# Or remove it as soon as the app exits
winapp run counter.cs --unregister-on-exit
```

`winapp unregister counter.cs` needs no manifest path: it evaluates the file's `#:property` values the
same way `run` does, and removes only a package registered from *that* file's build output. A
same-named app registered from a different folder is refused unless you pass `--force`. If the run used
an option that shapes the identity or the layout, pass the same one to `unregister`:

```bash
winapp run counter.cs -p WinAppPackageName=com.contoso.alt
winapp unregister counter.cs -p WinAppPackageName=com.contoso.alt

winapp run counter.cs -c Release --arch arm64
winapp unregister counter.cs -c Release --arch arm64
```

`-p` overrides the file's own directives, and a `Directory.Build.props` beside the `.cs` can key
`WinAppPackageName` off `$(Configuration)` or `$(RuntimeIdentifier)` — so each of these can change which
package gets registered.

Once the SDK's temp output has been cleaned, `winapp unregister counter.cs` can no longer confirm the
registration came from that file and will skip it — use `winapp unregister --prune` to clear
registrations whose files are gone, or `--force` to remove a specific one anyway. If the run used
`--output-appx-directory`, pass the same directory to `unregister` so it can recognize the layout.

The same applies to a custom output path: ownership is confirmed from the SDK's standard
`<root>\bin\<configuration>` layout, so a run built with `-p OutputPath=<somewhere-else>` cannot be
matched to its source file. `unregister` skips it rather than guessing at a wider directory — name the
layout with `--output-appx-directory`, or use `--force`.

**Single-file examples:**

```bash
# Build and run a file-based app with package identity
winapp run counter.cs

# Register identity without launching (e.g. to attach Visual Studio)
winapp run counter.cs --no-launch

# Release build, detached, printing the PID as JSON
winapp run counter.cs -c Release --detach --json

# Capture OutputDebugString output and crash diagnostics
winapp run counter.cs --debug-output

# Forward arguments to the app
winapp run counter.cs -- --verbose --input data.json

# Wipe the app's LocalState and start fresh
winapp run counter.cs --clean

# Remove the package it registered
winapp unregister counter.cs
```

> [!NOTE]
> The default identity includes a short hash of the file's path — `counter.cs` becomes something like
> `counter-a1b2c3d4` — so two `counter.cs` files in different folders are different apps and keep their
> own settings and `LocalState`. The hash is derived from the path, so it survives edits and re-runs and
> only changes if you move the file. Set `#:property WinAppPackageName=<name>` to choose a stable
> identity yourself; it is normalized to what `Identity/@Name` allows — characters outside
> `[-.A-Za-z0-9]` are dropped, names shorter than 3 characters are padded with `1`, and the result is
> capped at 50 characters, so `My App` registers as `MyApp`. Either way the Start menu and Settings show
> your `WinAppDisplayName` (default: the file name), not the identity. Identity is always scoped to your
> user account, so it never collides with another user on the same machine.

**MSBuild properties (NuGet package):**

When using the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package, `dotnet run` automatically invokes `winapp run`.

Everything written after `dotnet run` is passed to **your application**, exactly as it would be without the package. Configure the launcher with the MSBuild properties below:

```powershell
# Goes to your app. `--` is optional here, but required when the flag is also a
# `dotnet run` option (--configuration, --framework, --project, -c, -f, -r, ...),
# otherwise the SDK claims it and your app never sees it.
dotnet run --devtools
dotnet run -- --devtools
dotnet run -- --configuration Release

# Configures WinApp; --devtools still reaches your app
dotnet run -p:WinAppRunDetach=true --devtools
```

The following MSBuild properties can be set in your `.csproj` to control behavior:

| Property | Default | Description |
|----------|---------|-------------|
| `EnableWinAppRunSupport` | `true` | Enable/disable the run support functionality |
| `WinAppLaunchArgs` | (empty) | Arguments to pass to the app on launch |
| `WinAppRunUseExecutionAlias` | inferred from the app | Launch via execution alias instead of AUMID activation. Left unset, winapp infers it: a console app uses an alias so its output reaches the terminal, a windowed app uses AUMID. Set `true` or `false` to decide it yourself. |
| `WinAppRunNoLaunch` | `false` | Only register identity without launching |
| `WinAppRunDebugOutput` | `false` | Capture `OutputDebugString` messages and first-chance exceptions. Only one debugger can attach at a time (prevents VS/VS Code). Use `WinAppRunNoLaunch` instead to attach a different debugger. |
| `WinAppRunDetach` | `false` | Return immediately after launching instead of waiting for the app to exit. Prints the PID. |
| `WinAppRunUnregisterOnExit` | `false` | Unregister the development package after the app exits |
| `WinAppRunClean` | `false` | Remove the existing package's application data (LocalState, settings) before re-deploying |
| `WinAppRunSymbols` | `false` | Download symbols from the Microsoft Symbol Server for richer native crash analysis. Only has an effect with `WinAppRunDebugOutput`. |
| `WinAppRunExecutable` | (empty) | Executable path relative to the build-output folder. Use when the manifest contains `$targetnametoken$` and the output folder has more than one `.exe`. |
| `WinAppRunArgs` | (empty) | Raw arguments appended to the `winapp run` command line, for options with no dedicated property (for example `--verbose`). Appended after every property above. |

**Mutually exclusive settings.** `WinAppRunNoLaunch` and `WinAppRunDetach` each describe a different
launch behavior, so they conflict with the other launch properties and with each other. Setting a
conflicting pair fails the run with `--X and --Y cannot be used together`:

| Property | Cannot be combined with |
|----------|-------------------------|
| `WinAppRunNoLaunch` | `WinAppRunDetach`, `WinAppRunDebugOutput`, `WinAppRunUnregisterOnExit` |
| `WinAppRunDetach` | `WinAppRunNoLaunch`, `WinAppRunDebugOutput`, `WinAppRunUnregisterOnExit` |

`WinAppRunUseExecutionAlias` is deliberately **not** in that list, in either direction. `false` asks
for AUMID activation, which no-launch and detach already use; `true` is simply not applied when
either is set, because an execution alias needs a tracked, running process. So a project that checks
in `<WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>` still runs cleanly under
`dotnet run -p:WinAppRunDetach=true`, launching via AUMID rather than failing.

`WinAppRunUseExecutionAlias`, `WinAppRunDebugOutput`, and `WinAppRunUnregisterOnExit` can be combined
with each other. `WinAppRunClean`, `WinAppRunSymbols`, `WinAppRunExecutable`, and `WinAppLaunchArgs`
have no restrictions. `WinAppRunArgs` adds no restriction of its own, but a switch passed through it
is checked like any other, so `WinAppRunArgs="--detach"` still conflicts with `WinAppRunNoLaunch`.

```xml
<PropertyGroup>
  <WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>
  <WinAppRunDebugOutput>true</WinAppRunDebugOutput>
</PropertyGroup>
```

---

### unregister

Unregister a sideloaded development package. Only removes packages that were registered in development mode (e.g., via `winapp run` or `create-debug-identity`). Store-installed or MSIX-installed packages are never removed.

```bash
winapp unregister [input] [options]
```

**Arguments:**

- `input` - Path to a .NET file-based app (a single `.cs`) whose package should be unregistered. Its identity is resolved the same way `winapp run` resolves it — from an authored manifest if the app has one, otherwise from its `#:property` values — so no manifest path is needed. Omit to use `--manifest` or auto-detect a manifest in the current directory. Cannot be combined with `--manifest`, which names the package a different way and can resolve to a different one.

**Options:**

- `--manifest <path>` - Path to Package.appxmanifest (default: auto-detect from current directory)
- `--force` - Skip the ownership check and unregister even if the package was registered from a different project tree, or if its install location cannot be resolved. With `--prune`, also skips the confirmation prompt. **Candidates are matched by `Identity/@Name` alone**, so `--force` also removes a same-named package from a *different publisher*, along with its application data — for registrations whose files are gone, prefer `--prune`, which preserves application data.
- `--prune` - Remove every development-mode registration whose files are gone. Cannot be combined with an input, `--manifest`, `--property`, `--configuration`, `--arch`, `--runtime`, or `--output-appx-directory`.
- `-p, --property <Name=Value>` - MSBuild property used when resolving a `.cs` file-based app's identity. Repeatable. Pass the same identity-affecting properties the run used (e.g. `-p WinAppPackageName=...`), since a command-line property overrides the file's own `#:property` directives. Only applies to a `.cs` input.
- `-c, --configuration <name>` - Build configuration used when resolving a `.cs` file-based app's identity. Default: `Debug`. Pass the same configuration the run used: a `Directory.Build.props` beside the `.cs` can set `WinAppPackageName` or `WinAppManifestPath` conditionally on `$(Configuration)`. Only applies to a `.cs` input.
- `--arch <x64|arm64|x86>` - Target architecture used when resolving a `.cs` file-based app's identity. Default: the current process architecture. Pass the same architecture the run used, since identity can also be keyed off `$(RuntimeIdentifier)`. Only applies to a `.cs` input.
- `-r, --runtime <rid>` - Target .NET runtime identifier (e.g. `win-x64`) used when resolving a `.cs` file-based app's identity. Only its architecture is used, and it overrides `--arch`. Only applies to a `.cs` input.
- `--output-appx-directory <path>` - The AppX layout directory the package was registered from. Only needed when the run used `--output-appx-directory`, since nothing on the package records which run option produced its layout.
- `--json` - Format output as JSON

**What it does:**

- Determines the package name — from the `.cs` file's resolved identity, or by reading the manifest
- Searches for both `{name}` and `{name}.debug` packages (the debug variant is created by `create-debug-identity`)
- Verifies each package was registered in development mode (`IsDevelopmentMode == true`)
- Verifies the package belongs to the app you named (unless `--force`) — its install location must sit under a directory you identified: the `.cs` file's own build output, the manifest's directory, the current directory, or an explicit `--output-appx-directory`. A package whose install location cannot be resolved (its files were deleted) is **skipped**, because identity alone is not proof of ownership: two apps that both set `#:property WinAppPackageName=counter` register the same identity from different folders. Use `--prune` to clear registrations whose files are gone.
- Unregisters matching packages

**Cleaning up dead registrations (`--prune`):**

A registration outlives its files. Delete a build output, project tree, or (for a file-based app) let
Windows clean `%LOCALAPPDATA%\Temp`, and the package stays registered: Windows keeps the identity and
its Start menu entry, but activation **silently does nothing**. These accumulate invisibly.

```bash
# List dev registrations whose files are gone, then confirm before removing
winapp unregister --prune

# Skip the prompt (required for non-interactive/CI use)
winapp unregister --prune --force
```

Only development-mode registrations are considered, and each is removed by its full package name, so a
same-named package still installed from a live location is untouched. The prompt exists because a
missing install location is *usually* a deleted folder but also describes a package registered from a
disconnected network share or removable drive — review the list before confirming.

**Examples:**

```bash
# Unregister from current directory (auto-detects manifest)
winapp unregister

# Unregister a .NET file-based app by its source file
winapp unregister counter.cs

# Unregister with explicit manifest
winapp unregister --manifest ./Package.appxmanifest

# Force unregister even if registered from a different project tree
winapp unregister --force

# Remove every dev registration whose files are gone
winapp unregister --prune

# JSON output for scripting
winapp unregister --json
```

---

### cert

Generate, inspect, and install development certificates.

#### cert generate

Generate development certificates for package signing.

```bash
winapp cert generate [options]
```

**Options:**

- `--manifest <Package.appxmanifest>` - Extract publisher information from Package.appxmanifest 
- `--publisher <name>` - Publisher for the certificate. Accepts a full X.500 distinguished name (e.g., `CN=Contoso, O=Contoso Ltd, C=US`) or a bare name which is automatically wrapped as `CN=<name>`
- `--output <path>` - Output certificate file path (supports absolute and relative paths)
- `--password <password>` - Certificate password (default: "password")
- `--valid-days <valid-days>` - Number of days the certificate is valid (default: 365)
- `--install` - Install the certificate to the local machine store after generation
- `--if-exists <Error|Overwrite|Skip>` - Set behavior if the certificate file already exists (default: Error)
- `--export-cer` - Export a `.cer` file (public key only) alongside the `.pfx`. Useful for distributing the public certificate separately for trust installation.
- `--json` - Format output as JSON for programmatic consumption. Errors are also returned as JSON (`{"error": "..."}`).

#### cert info

Display certificate details from a PFX file. Useful for verifying a certificate matches your manifest before signing.

```bash
winapp cert info <cert-path> [options]
```

**Arguments:**

- `cert-path` - Path to the certificate file (PFX)

**Options:**

- `--password <password>` - Password for the PFX file (default: "password")
- `--json` - Format output as JSON

#### cert install

Install certificate to machine certificate store.

```bash
winapp cert install <cert-path> [options]
```

**Arguments:**

- `cert-path` - Path to certificate file to install

**Examples:**

```bash
# Generate certificate for specific publisher
winapp cert generate --publisher "CN=My Company" --output ./mycert.pfx

# Generate certificate and export public key .cer file
winapp cert generate --publisher "CN=My Company" --export-cer

# Generate certificate with JSON output (for scripting)
winapp cert generate --publisher "CN=My Company" --json

# View certificate details
winapp cert info ./mycert.pfx

# View certificate details as JSON
winapp cert info ./mycert.pfx --json

# Install certificate to machine
winapp cert install ./mycert.pfx
```

---

### sign

Sign MSIX packages and executables with certificates.

```bash
winapp sign <file-path> <cert-path> [options]
```

**Arguments:**

- `file-path` - Path to MSIX package or executable to sign
- `cert-path` - Path to the signing certificate (.pfx)

**Options:**

- `--password <password>` - Certificate password (default: "password")
- `--timestamp <url>` - RFC 3161 timestamp server URL

**Examples:**

```bash
# Sign MSIX package
winapp sign MyApp.msix ./mycert.pfx

# Sign executable with a non-default certificate password
winapp sign ./bin/MyApp.exe ./mycert.pfx --password mypassword
```

---

### az-sign

Code-sign a file (exe, MSIX, or MSIX bundle) using [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/) — a cloud-managed signing identity, so no private key (PFX) ever lives on the local machine.

```bash
winapp az-sign <file-path> [options]
```

**Arguments:**

- `file-path` - Path to the file to sign (exe, msix, or msixbundle)

**Options:**

- `--subscription`, `-s` - Azure subscription ID to use. If not provided and multiple subscriptions exist, you will be prompted
- `--resource-group`, `-r` - Resource group to narrow down signing accounts
- `--account` - Signing account name. Must be used with `--resource-group`
- `--profile`, `-p` - Certificate profile name. Must be used with `--account`
- `--metadata-file`, `-m` - Path to an existing `metadata.json`. Skips resource discovery and account/profile selection prompts and signs directly. A non-interactive Azure credential should already be available; the CLI can otherwise fall back to an interactive tenant prompt or `az login`, but the npm programmatic API is always non-interactive and fails instead of prompting

**Authentication:**

`az-sign` uses Azure's standard credential chain (`DefaultAzureCredential`). For CI/CD, set `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, and `AZURE_CLIENT_SECRET` (or use GitHub Actions OIDC / managed identity). An existing Azure CLI session (`az login`, including the `azure/login` GitHub Action) is also honored in any environment. Only when no credentials are found *and* the session is interactive will `az-sign` launch `az login` for you.

**Prerequisites:**

- An Azure Code Signing account and a certificate profile (created in the Azure portal after identity validation), plus the **Code Signing Certificate Profile Signer** role assigned to your identity. For more guidance, visit [Azure Artifact Signing quickstart docs.](https://learn.microsoft.com/azure/artifact-signing/quickstart)
- A machine-wide **x64 .NET 8 (or later) runtime** installed. The Azure signing client library is a managed assembly that `signtool.exe` loads in a separate process; winapp's own self-contained runtime does not satisfy it. Install it from https://dotnet.microsoft.com/download if signing fails with a runtime-load error.
- The **Microsoft Visual C++ Redistributable (x64)**. The Azure signing client library depends on the VC++ runtime, and because winapp downloads the raw NuGet package rather than the official client-tools installer, this dependency is **not** installed automatically. A clean machine can load-fail even with .NET and SignTool present. Install the latest x64 redistributable from https://aka.ms/vs/17/release/vc_redist.x64.exe if signing fails with a `0xc000007b`, "The application was unable to start correctly", or missing-DLL error from the dlib.

> **Least-privilege CI:** Auto-discovery (listing subscriptions, resource groups, accounts, and profiles) needs read access at a parent scope. To avoid *every* collection-listing call, pass all four of `--subscription`, `--resource-group`, `--account`, and `--profile`: `az-sign` then validates the account and profile with direct resource reads (a GET on each named resource) instead of enumerating the parent collection, so a principal scoped to just that account and profile is sufficient. Omitting any one of them re-introduces a listing call — for example, leaving out `--subscription` makes `az-sign` list the subscriptions your identity can access — which a narrowly-scoped principal may not be permitted to do. A principal scoped only to a single certificate profile can skip validation entirely by passing a pre-generated `--metadata-file` (which specifies the account endpoint and profile directly).

**Examples:**

```bash
# Interactive — discover/select subscription, account, and profile
winapp az-sign ./app.msix

# Fully specified — no prompting (ideal for CI/CD)
winapp az-sign ./app.msix --subscription <sub-id> --resource-group <rg> --account <account> --profile <profile>

# Reuse an existing metadata.json (skips resource discovery and selection; authentication may still prompt)
winapp az-sign ./app.msix --metadata-file ./metadata.json
```

---

### create-external-catalog

Generate a `CodeIntegrityExternal.cat` catalog file containing hashes of executable files from specified directories. This catalog is used with the [TrustedLaunch](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-trustedlaunch-trustedlaunch) flag in MSIX sparse package manifests ([AllowExternalContent](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-uap10-allowexternalcontent)) to allow execution of external files not included in the package itself.

This is similar to how `signtool.exe` creates `AppxMetadata\CodeIntegrity.cat` when signing an MSIX package, but generates an external catalog for use with [sparse/external location packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps).

```bash
winapp create-external-catalog <input-folder> [options]
```

**Arguments:**

- `input-folder` - One or more directories containing executable files to process. Separate multiple directories with semicolons (e.g., `"dir1;dir2"`)

**Options:**

- `--recursive`, `-r` - Include files from subdirectories
- `--use-page-hashes` - Include page hashes when generating the catalog (produces a larger catalog with per-page hash data)
- `--compute-flat-hashes` - Include flat file hashes when generating the catalog
- `--if-exists <Error|Overwrite|Skip>` - Behavior when the output file already exists (default: `Error`)
- `--output`, `-o` - Output catalog file path. If not specified, `CodeIntegrityExternal.cat` is created in the current directory. If a directory is specified, the default filename is appended.

**What it does:**

- Scans specified directories for executable files (PE binaries with code sections)
- Generates a Catalog Definition File (CDF) with hashes of all found executables
- Uses Windows CryptoCAT APIs to produce the `.cat` catalog file
- Non-executable files (e.g., `.txt`, `.dll` without code sections) are automatically skipped

**Examples:**

```bash
# Generate catalog for all executables in a directory
winapp create-external-catalog ./bin

# Include files in subdirectories
winapp create-external-catalog ./bin --recursive

# Specify a custom output path
winapp create-external-catalog ./bin --output ./dist/CodeIntegrityExternal.cat

# Overwrite existing catalog
winapp create-external-catalog ./bin --if-exists Overwrite

# Skip generation if catalog already exists
winapp create-external-catalog ./bin --if-exists Skip

# Include page hashes (for stricter code integrity validation)
winapp create-external-catalog ./bin --use-page-hashes

# Process multiple directories
winapp create-external-catalog "./bin;./lib" --recursive

# Combine multiple options
winapp create-external-catalog ./bin --recursive --use-page-hashes --compute-flat-hashes --output ./dist/CodeIntegrityExternal.cat --if-exists Overwrite
```

**When to use:**

Use this command when building a sparse MSIX package that uses TrustedLaunch to verify external executables. The typical workflow is:

1. `winapp manifest generate --template sparse` — Create a sparse manifest with `AllowExternalContent`
2. `winapp create-external-catalog ./bin` — Generate the code integrity catalog for your app's executables  
3. `winapp pack` — Package the manifest, assets, and catalog into an MSIX

---

### tool

Access Windows SDK tools directly. Uses tools available in [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/)

```bash
winapp tool <tool-name> [tool-arguments]
```

**Available tools:**

- `makeappx` - Create and manipulate app packages
- `signtool` - Sign files and verify signatures
- `mt` - Manifest tool for side-by-side assemblies
- And other Windows SDK tools from [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/)

**Examples:**

```bash
# Use signtool to verify signature
winapp tool signtool verify /pa MyApp.msix
```

**Signature verification**

Build tools are downloaded from NuGet and then executed, so winapp checks each one for a valid Microsoft Authenticode signature immediately before running it. This applies to every command that shells out to an SDK tool, including `tool`, `package`, and `sign`. A tool that fails the check is not run:

```text
'mt.exe' is not validly signed by Microsoft, so it was not run (C:\...\mt.exe).
```

A failure here means the file on disk is not what Microsoft published — most often a corrupt or partial download. Delete the package from the NuGet cache and run the command again so winapp re-downloads it.

---

### store

Run a Microsoft Store Developer CLI command. This command will download the Microsoft Store Developer CLI if not already downloaded. Learn more about the [Microsoft Store Developer CLI](https://aka.ms/msstoredevcli).

```bash
winapp store [args...]
```

**Arguments:**

- `args...` – Arguments to pass directly to the `msstore` CLI. See [MSStore CLI documentation](https://aka.ms/msstoredevcli/docs) for available commands and options.

**What it does:**

- Ensures the Microsoft Store Developer CLI (`msstore`) is downloaded and available on your system.
- Forwards all arguments to the `msstore` CLI.
- Runs the command showing output directly in your terminal.

**Examples:**

```bash
# List all apps in your Microsoft Partner Center account
winapp store app list

# Publish a package to the Microsoft Store
winapp store publish ./myapp.msix --appId <your-app-id>
```

---

### get-winapp-path

Get paths to installed Windows SDK components.

```bash
winapp get-winapp-path [options]
```

**What it returns:**

- Paths to `.winapp` workspace directory
- Package installation directories
- Generated header locations

---

### find-ui

Search **WinUI** controls and samples for a working code example. WinUI-only: the corpus is the [WinUI 3 Gallery](https://github.com/microsoft/WinUI-Gallery) and the [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows) (plus a few curated core patterns) — it does **not** cover WPF, WinForms, or other UI frameworks. A third source, the [microsoft-ui-reactor ReactorGallery](https://github.com/microsoft/microsoft-ui-reactor), is **opt-in**: it is excluded from a normal search and only searched when you pass `--source reactor` (its C#-only declarative samples don't paste into a standard XAML app, so reach for it only when building a Reactor/MVU project).

```bash
winapp find-ui "<query>" [options]
```

The Gallery, Toolkit, and Reactor corpora ship **inside the CLI**, so `find-ui` works with no network access — including on a first run in an agent sandbox or behind a corporate proxy that blocks `raw.githubusercontent.com`. When GitHub *is* reachable the CLI refreshes from it and caches the result per-user under `<global .winapp>/cache/find-ui`; the built-in corpus is only a floor, never a ceiling. Cached data is refreshed at most every 24 hours, or on demand with `--refresh`.

The built-in corpus is re-fetched from GitHub every time a stable release is built, and a refresh that fails **stops the release build** rather than quietly shipping older data — the baker fetches through the same code path `--refresh` uses, so a failure there means the live refresh is broken too and is worth investigating before shipping. A release can still be cut against the previously committed corpus, but only as an explicit override. When results are served from the built-in copy, `find-ui` says so on stderr and `--json` output carries `"corpus": "embedded"` (other values: `"network"` for a fresh fetch, `"cache"` for the local cache).

**Options:**

- `--id <id>` - Fetch the code (Gallery/Toolkit return XAML and/or C#; Reactor is C#-only) plus prerequisite notes for one or more scenario ids from a prior search (e.g. `gallery-tabview-1`). Repeatable. **Ids are case-insensitive** — `GALLERY-TABVIEW-1` resolves the same as `gallery-tabview-1`.
- `--list` - List every discoverable control/sample id instead of searching (Gallery + Toolkit + core; the opt-in Reactor source is excluded).
- `--source <gallery|toolkit|reactor|core>` - Restrict search results to a single source. (Search only — not valid with `--list`/`--id`.) **Reactor is opt-in** — it is excluded from a normal search, so `--source reactor` is the only way to search it.
- `--max <N>` - Maximum number of matched controls to return (default: 3). Applies to search only; ignored with `--list`/`--id`.
- `--refresh` - Bypass the local cache and re-fetch the WinUI corpus from GitHub.
- `--json` - Emit structured JSON (agent-friendly). For search, each match carries `source`, `control`, `score`, `description`, and a `scenarios` array whose entries hold the per-scenario `id` and `header`; for `--id`, full code. Under `--json` **every** failure — including argument/parser errors such as a non-integer `--max` — is emitted as a flat `{"error": "..."}` object on stdout with a non-zero exit code, so output stays machine-readable.

**Workflow:** search compactly to find the right control and its scenario ids, then fetch the full code for the best match with `--id`.

**Examples:**

```bash
# Find a control by intent (compact results with scenario ids)
winapp find-ui "tabbed layout"

# Restrict to the Windows Community Toolkit
winapp find-ui "settings card" --source toolkit

# Restrict to Reactor (opt-in; C#-only declarative WinUI — Reactor projects only)
winapp find-ui "flex layout" --source reactor

# Fetch the full XAML + C# for a specific scenario
winapp find-ui --id gallery-tabview-1

# Agent-friendly structured output
winapp find-ui "color picker" --json

# Browse everything, or force a corpus refresh
winapp find-ui --list
winapp find-ui "navigation view" --refresh
```

---

### node generate-bindings

*(Available in NPM package only)* Generate JS bindings for Windows App SDK APIs. The bindings are declared by a `"winapp": { "jsBindings": {...} }` namespace in **`package.json`** and written to `.winapp/bindings/`.

```bash
npx winapp node generate-bindings [options]
```

**Options:**

- `--verbose`, `-v` - Enable verbose per-file codegen output
- `--quiet`, `-q` - Suppress progress and informational output

**What it does:**

- Reads the `winapp.jsBindings` block from `package.json` and the `winmds.lock.json` written by the last `winapp restore`, then emits typed `.js` + `.d.ts` bindings into `.winapp/bindings/`
- Does **not** modify `package.json` — it is a passive regenerator. Adding the `winapp.jsBindings` block and the `@microsoft/dynwinrt` runtime dependency happens during [`winapp init`](#init) when JS bindings are enabled; this command fails fast if the block is absent
- Warns (but does not write) if `@microsoft/dynwinrt` is missing from your dependencies — run `npm install` after `init` has added it

> [!NOTE]
> Bindings are **npm-only** — they require invocation via `npx winapp` (the `@microsoft/winappcli` npm package); the standalone winget CLI does not surface them. Run [`winapp init`](#init) interactively and opt in, or use `winapp init . --use-defaults --add-js-bindings`, before using this command to regenerate bindings. If you edit `winapp.yaml`, run `npx winapp restore` to refresh Windows dependencies before regenerating.

**Examples:**

```bash
# Regenerate JS bindings in the current project
npx winapp node generate-bindings

# Regenerate after editing winapp.jsBindings, with verbose output
npx winapp node generate-bindings --verbose
```

> See the [JS bindings guide](guides/electron/js-file-picker.md) for the end-to-end workflow and the `winapp.jsBindings` configuration options.

---

### node create-addon

*(Available in NPM package only)* Generate native C++ or C# addon templates with Windows SDK and Windows App SDK integration.

```bash
npx winapp node create-addon [options]
```

**Options:**

- `--name <name>` - Addon name (default: "nativeWindowsAddon")
- `--template` - Select type of addon. Options are `cs` or `cpp` (default: `cpp`)
- `--verbose` - Enable verbose output

**What it does:**

- Creates addon directory with template files
- Generates binding.gyp and addon.cc with Windows SDK examples
- Installs required npm dependencies (nan, node-addon-api, node-gyp)
- Adds build script to package.json

**Examples:**

```bash
# Generate addon with default name
npx winapp node create-addon

# Generate custom named addon
npx winapp node create-addon --name myWindowsAddon
```

---

### node add-electron-debug-identity

*(Available in NPM package only)* Add app identity to Electron development process by using sparse packaging. Requires a Package.appxmanifest (create one with `winapp init` or `winapp manifest generate` if you don't have one).

> [!IMPORTANT]  
> There is a known issue with sparse packaging Electron applications which causes the app to crash on start or not render the web content. The issue has been fixed in Windows but it has not propagated to external Windows devices yet. If you are seeing this issue after calling `add-electron-debug-identity`, you can [disable sandboxing in your Electron app](https://www.electronjs.org/docs/latest/tutorial/sandbox#disabling-chromiums-sandbox-testing-only) for debug purposes with the `--no-sandbox` flag. This issue does not affect full MSIX packaging.
<br /><br />
To undo the Electron debug identity, use `winapp node clear-electron-debug-identity`.

```bash
npx winapp node add-electron-debug-identity [options]
```

**Options:**

| Option | Description |
|--------|-------------|
| `--manifest <path>` | Path to custom Package.appxmanifest (default: Package.appxmanifest in current directory) |
| `--no-install` | Do not install or modify dependencies; only configure the Electron debug identity |
| `--keep-identity` | Keep the manifest identity as-is, without appending `.debug` to the package name and application ID |
| `--verbose` | Enable verbose output |

**What it does:**

- Registers debug identity for electron.exe process
- Enables testing identity-requiring APIs in Electron development
- Uses existing Package.appxmanifest for identity configuration

**Examples:**

```bash
# Add identity to Electron development process
npx winapp node add-electron-debug-identity

# Use a custom manifest file
npx winapp node add-electron-debug-identity --manifest ./custom/Package.appxmanifest
```

---

### node clear-electron-debug-identity

*(Available in NPM package only)* Remove package identity from the Electron debug process by restoring the original electron.exe from backup.

```bash
npx winapp node clear-electron-debug-identity [options]
```

**Options:**

| Option | Description |
|--------|-------------|
| `--verbose` | Enable verbose output |

**What it does:**

- Restores electron.exe from the backup created by `add-electron-debug-identity`
- Removes the backup files after restoration
- Returns Electron to its original state without package identity

**Examples:**

```bash
# Remove identity from Electron development process
npx winapp node clear-electron-debug-identity
```

---

### Global Options

All commands support these global options:

- `--verbose`, `-v` - Enable verbose output for detailed logging
- `--quiet`, `-q` - Suppress progress messages
- `--help`, `-h` - Show command help

---

### Global Cache Directory

Winapp creates a directory to cache files that can be shared between multiple projects.

By default, winapp creates a directory at `$UserProfile/.winapp` as the global cache directory.

To use a different location, set the `WINAPP_CLI_CACHE_DIRECTORY` environment variable.

In **cmd**:
```cmd
REM Set a custom location for winapp's global cache
set WINAPP_CLI_CACHE_DIRECTORY=d:\temp\.winapp
```

In **PowerShell** and **pwsh**:
```pwsh
# Set a custom location for winapp's global cache
$env:WINAPP_CLI_CACHE_DIRECTORY=d:\temp\.winapp
```

Winapp will create this directory automatically when you run commands like `init` or `restore`.

### Update Checks

The winapp CLI periodically checks for new versions and displays a one-line notice when an update is available. This check runs in the background and adds no latency to commands.

Update checks are automatically disabled in CI environments (GitHub Actions, Azure Pipelines, etc.).

To manually disable update checks, set the `WINAPP_CLI_UPDATE_CHECK` environment variable to `0`.

In **cmd**:
```cmd
set WINAPP_CLI_UPDATE_CHECK=0
```

In **PowerShell** and **pwsh**:
```pwsh
$env:WINAPP_CLI_UPDATE_CHECK = "0"
```

To make this permanent:
```powershell
[System.Environment]::SetEnvironmentVariable('WINAPP_CLI_UPDATE_CHECK', '0', 'User')
```

### UI workflow identity

`winapp ui` commands that drive the physical desktop always take cooperative turns, so two workflows
running at once cannot steal each other's focus or dismiss each other's menus. That arbitration needs
no setup and cannot be switched off.

What is optional is *continuity*. By default each command is a self-contained one-shot that releases
the desktop as soon as it finishes. To keep the desktop across several commands, give them all the
same workflow id:

```pwsh
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString()
```

Use the *same* value for cooperating processes (for example a recording and the clicks it should
capture) and *different* values for independent workflows. Every command without an id is its own
one-shot workflow, even when several are launched from one shell, so hosts that start a fresh shell
per command must inject the same explicit value into each one. The value is opaque, is never treated
as a credential, and is only ever persisted as a SHA-256 hash. See
[UI Automation → Coordinating concurrent UI workflows](ui-automation.md#coordinating-concurrent-ui-workflows).

### ui

Inspect and interact with running Windows app UIs using UI Automation (UIA).

```bash
winapp ui [command] [options]
```

**Commands:**
- `status` - Connect to app and show info
- `inspect` - View element tree
- `search` - Find elements by selector
- `get-property` - Read element properties
- `get-text` / `get-value` - Read value/text from element (TextPattern, ValuePattern, or Name)
- `screenshot` - Capture window/element as PNG (auto-captures dialogs separately)
- `record` - Record a window/element region to an H.264 MP4 video (Windows Graphics Capture + Media Foundation)
- `invoke` - Activate element (click, toggle, expand)
- `click` - Click element via mouse simulation (for controls that don't support invoke)
- `hover` - Move mouse to element to trigger tooltips, flyouts, and hover states (default dwell: 800ms)
- `drag` - Drag the mouse from one point to another, by element selector or screen `x,y` coordinates (reorder, resize, sliders, drag-and-drop)
- `touch` - Inject synthetic touch gestures (tap, double-tap, long-press, swipe, pinch, stretch) at an element center or screen `x,y` coordinates
- `pen` - Inject synthetic pen/stylus input — taps and ink strokes with configurable pressure, tilt, and eraser mode
- `send-keys` - Send synthetic keyboard input (named keys, combos, raw vk=0xNN, or literal text) to a window
- `set-value` - Set value on editable element (text, number); falls back to LegacyIAccessible `put_accValue` for TextPattern-only rich-edit controls
- `focus` - Move keyboard focus
- `scroll-into-view` - Scroll element visible
- `wait-for` - Wait for element state
- `list-windows` - List all windows for an app
- `get-focused` - Report the currently focused element

**Options:**
- `-a, --app <app>` - Target app (name, title, or PID)
- `-w, --window <hwnd>` - Target window by HWND (stable)

#### ui record

Record a window or element region to an H.264 MP4.

```bash
# Record a window for 10 seconds at 15 fps
winapp ui record -a Calculator --duration-sec 10 --fps 15 -o demo.mp4

# Record until Ctrl+C, downscaled so the longest edge is 1280px
winapp ui record -a "My App" --duration-sec 0 --max-edge 1280 -o capture.mp4

# Record just one element's region
winapp ui record -a "My App" btn-save-1234 -o button.mp4

# Keep an agent-readable timeline alongside the MP4
winapp ui record -a Calculator --frames --duration-sec 10 --fps 10 -o demo.mp4
```

**Record options:**
- `--duration-sec <n>` - Recording length in seconds. `0` records until Ctrl+C (default `0`).
- `--fps <n>` - Frames per second to capture (default `15`).
- `--max-edge <px>` - Downscale so the longest edge is at most this many pixels (`0` = no downscale).
- `--capture-screen` - Capture from the screen so overlays/popups are included (may capture occluding windows).
- `-o, --output <path>` - Output `.mp4` path (defaults to `recording-<timestamp>-<guid>.mp4`).
- `--frames` - Write timestamped JPEGs, `frames.ndjson`, and `manifest.json` to `<output-name>.frames`. Supports 1-30 fps and `--max-edge` 64-4096 (default 1280), with a 1 GiB frame-data cap.

With `--json`, the final result includes the output path, dimensions, codec, capture mode, cadence,
stop reason, optional `frameArtifacts`, and warnings.

> **Known limitation:** recording a *specific element* inside a popup that renders in its own
> top-level window (WinUI/XAML flyout, teaching tip, tooltip) may capture the underlying main
> window instead. Record the whole window, or use `ui screenshot --capture-screen` for popup
> stills. Tracked in [#646](https://github.com/microsoft/winappCli/issues/646).

For full documentation, see [docs/ui-automation.md](ui-automation.md).






