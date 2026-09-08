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

Create MSIX packages from prepared application directories. Requires a manifest file (`Package.appxmanifest` preferred, `appxmanifest.xml` also supported) to be present in the target directory, in the current directory, or passed with the `--manifest` option. (run `init` or `manifest generate` to create a manifest)

Pass multiple input folders to create an `.msixbundle` for multi-architecture distribution (see [Multi-architecture bundles](#multi-architecture-bundles) below).

```bash
winapp pack <input-folder> [input-folder...] [options]
```

**Arguments:**

- `input-folder` - One or more directories containing the application files to package. Pass multiple folders (e.g., `./publish/x64 ./publish/arm64`) to create an MSIX bundle. For **sparse identity packages**, pass a sparse `appxmanifest.xml` file directly instead of a folder (see [Sparse identity packages](#sparse-identity-packages) below).

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

**What it does:**

- Validates and processes Package.appxmanifest files
- Resolves `$placeholder$` tokens in the manifest (see [Manifest placeholders](#manifest-placeholders) below)
- Ensures proper framework dependencies
- Updates side-by-side manifests with registrations
- Automatically discovers and bundles any non-image files referenced in the manifest (e.g., AppExtension `manifest.json`, config files) from the manifest directory or input folder if they are missing from staging
- Automatically discovers third-party WinRT components and registers their activatable classes (see [WinRT component discovery](#winrt-component-discovery) below)
- Handles self-contained WinAppSDK deployment
- Signs package if certificate provided

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

`winapp run` operates in one of two modes, chosen automatically from the input:

- **Folder mode** — the input is a build-output folder (contains a `Package.appxmanifest`/`AppxManifest.xml`).
- **Project mode** — the input is a `.csproj`, a `.sln`/`.slnx` solution, or a directory containing one. `winapp run` builds the project and launches it, supporting both **packaged** and **unpackaged** WinUI apps. See [Project mode](#project-mode-net-sdk-projects) below.

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

- `input` - The app to run: a build-output folder (folder mode), a `.csproj` project, a `.sln`/`.slnx` solution, or a directory containing one of those at its top level (project mode; the directory is not searched recursively). Use `.` to build/run the project in the current directory. **Optional — defaults to the current directory when omitted** (matches `dotnet run`).

**Options:**

- `--manifest <path>` - Path to Package.appxmanifest (default: auto-detect from input folder or current directory)
- `--output-appx-directory <path>` - Output directory for the loose layout package (default: `AppX` inside the input folder directory)
- `--args <string>` - Command-line arguments to pass to the application. Alternatively, use `--` followed by arguments to avoid escaping (e.g., `winapp run . -- --flag value`).
- `--no-launch` - Only create the debug identity and register the package without launching the application
- `--with-alias` - Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a `uap5:ExecutionAlias` in the manifest (use `winapp manifest add-alias` to add one). Cannot be combined with `--no-launch`. Cannot be combined with `--json`.
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

- `-c, --configuration <name>` - Build configuration. Default: `Debug`.
- `--arch <x64|arm64|x86>` - Target architecture. Default: the current process architecture. Determines both the build RID and the architecture of the Windows App Runtime that gets installed.
- `-r, --runtime <rid>` - Target .NET runtime identifier (e.g. `win-x64`). Project mode uses only the RID's architecture, always builds the canonical `win-<arch>`, and rejects non-Windows RIDs (e.g. `linux-x64`). Its architecture overrides `--arch`.
- `-f, --framework <tfm>` - Target framework moniker for multi-targeted projects (e.g. `net10.0-windows10.0.26100.0`).
- `--project <name-or-path>` - When the input is a solution (`.sln`/`.slnx`) or a directory with multiple runnable app projects, selects which project to launch (by project name or path).
- `--no-build` - Skip building and run the existing build output (still evaluates output properties).
- `--no-restore` - Skip restoring the project before building.
- `-p, --property <Name=Value>` - MSBuild property, forwarded to both the build and the property evaluation. Repeatable (e.g. `-p WindowsPackageType=None`).

**Build output & verbosity:** the project is built in two steps — a `dotnet build` whose output **streams live** to your console, followed by a fast property-evaluation pass. winapp prints the exact `dotnet build …` invocation before the output, and streams warnings even on a successful build. Verbosity:

| Flag | dotnet verbosity | Adds |
|------|------------------|------|
| *(default)* | `minimal` | — |
| `--verbose` | `minimal` | winapp's build decision traces |
| `--quiet` | `quiet` | — |

Under `--json` or `--quiet` the invocation and build output go to stderr so stdout stays pure JSON / clean.

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
| `WinAppRunUseExecutionAlias` | `false` | Launch via execution alias instead of AUMID activation |
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
| `WinAppRunNoLaunch` | `WinAppRunDetach`, `WinAppRunUseExecutionAlias`, `WinAppRunDebugOutput`, `WinAppRunUnregisterOnExit` |
| `WinAppRunDetach` | `WinAppRunNoLaunch`, `WinAppRunUseExecutionAlias`, `WinAppRunDebugOutput`, `WinAppRunUnregisterOnExit` |

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
winapp unregister [options]
```

**Options:**

- `--manifest <path>` - Path to Package.appxmanifest (default: auto-detect from current directory)
- `--force` - Skip the install-location directory check and unregister even if the package was registered from a different project tree
- `--json` - Format output as JSON

**What it does:**

- Reads the package name from the manifest
- Searches for both `{name}` and `{name}.debug` packages (the debug variant is created by `create-debug-identity`)
- Verifies each package was registered in development mode (`IsDevelopmentMode == true`)
- Verifies the package's install location is under the current directory tree (unless `--force`)
- Unregisters matching packages

**Examples:**

```bash
# Unregister from current directory (auto-detects manifest)
winapp unregister

# Unregister with explicit manifest
winapp unregister --manifest ./Package.appxmanifest

# Force unregister even if registered from a different project tree
winapp unregister --force

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
