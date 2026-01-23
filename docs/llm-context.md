# winapp CLI Context for LLMs

> Auto-generated from CLI v0.1.11.4 (schema version )
> 
> This file provides structured context about the winapp CLI for AI assistants and LLMs.
> For the raw JSON schema, see [cli-schema.json](cli-schema.json).

## Overview

CLI for generating and managing appxmanifest.xml, image assets, test certificates, Windows (App) SDK projections, package identity, and packaging. For use with any app framework targeting Windows

**Installation:**
- WinGet: `winget install Microsoft.WinAppCli --source winget`
- npm: `npm install -g @microsoft/winappcli` (for electron projects)

## Command Reference

### `winapp cert`

Manage development certificates for code signing. Use 'cert generate' to create a self-signed certificate for testing, or 'cert install' (requires elevation) to trust an existing certificate on this machine.

#### `winapp cert generate`

Create a self-signed development certificate (PFX) for signing for testing. The certificate publisher must match the Publisher in your AppxManifest.xml. Not for production use - obtain a trusted certificate for distribution.

**Options:**
- `--if-exists` - Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) (default: `Error`)
- `--install` - Install the certificate to the local machine store after generation
- `--manifest` - Path to appxmanifest.xml file to extract publisher information from
- `--output` - Output path for the generated PFX file
- `--password` - Password for the generated PFX file (default: `password`)
- `--publisher` - Publisher name for the generated certificate. If not specified, will be inferred from manifest.
- `--quiet` / `-q` - Suppress progress messages
- `--valid-days` - Number of days the certificate is valid (default: `365`)
- `--verbose` / `-v` - Enable verbose output

#### `winapp cert install`

Add a certificate to the Trusted People store so Windows trusts packages signed with it. Required before installing MSIX packages signed with development certificates. Needs administrator privileges.

**Arguments:**
- `<cert-path>` *(required)* - Path to the certificate file (PFX or CER)

**Options:**
- `--force` - Force installation even if the certificate already exists
- `--password` - Password for the PFX file (default: `password`)
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp create-debug-identity`

Create and install a temporary package to enable package identity during debugging. Requires appxmanifest.xml in current directory or passed via flag. This lets you test Windows APIs and features that require package identity without creating a full MSIX package. Re-run after changing appxmanifest.xml or assets.

**Arguments:**
- `<entrypoint>` - Path to the .exe that will need to run with identity, or entrypoint script.

**Options:**
- `--manifest` - Path to the appxmanifest.xml
- `--no-install` - Do not install the package after creation.
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp get-winapp-path`

Print the path to the .winapp directory. Use --global for the shared cache location, or omit for the project-local .winapp folder. Useful for build scripts that need to reference installed packages.

**Options:**
- `--global` - Get the global .winapp directory instead of local
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp init`

Set up a new Windows app project. Interactive by default, use --use-defaults to skip prompts. Creates appxmanifest.xml with default assets, generates a devcert.pfx development certificate, creates winapp.yaml for version management, and downloads Windows SDK and Windows App SDK packages and generates projections. This is the recommended way to start - it combines what 'manifest generate' and 'cert generate' do individually. For existing projects, use 'restore' to reinstall packages from winapp.yaml.

**Arguments:**
- `<base-directory>` - Base/root directory for the winapp workspace, for consumption or installation.

**Options:**
- `--config-dir` - Directory to read/store configuration (default: current directory)
- `--config-only` - Only handle configuration file operations (create if missing, validate if exists). Skip package installation, certificate generation, and other workspace setup steps.
- `--ignore-config` / `--no-config` - Don't use configuration file for version management
- `--no-cert` - Skip development certificate generation
- `--no-gitignore` - Don't update .gitignore file
- `--quiet` / `-q` - Suppress progress messages
- `--setup-sdks` - SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)
- `--use-defaults` / `--no-prompt` - Do not prompt, and use default of all prompts
- `--verbose` / `-v` - Enable verbose output
### `winapp manifest`

Create and modify appxmanifest.xml files for package identity and MSIX packaging. Use 'manifest generate' to create a new manifest, or 'manifest update-assets' to regenerate app icons from a source image.

#### `winapp manifest generate`

Create a new appxmanifest.xml file. Use this when you need a manifest without running full 'init' setup, or to regenerate a manifest with different settings. Supports packaged apps (full MSIX), sparse packages (desktop app with identity), and hosted apps (scripts running under a host like Python/Node).

**Arguments:**
- `<directory>` - Directory to generate manifest in

**Options:**
- `--description` - Human-readable app description shown during installation and in Windows Settings (default: `My Application`)
- `--entrypoint` / `--executable` - Entry point of the application (e.g., executable path / name, or .py/.js script if template is HostedApp). Default: <package-name>.exe
- `--if-exists` - Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) (default: `Error`)
- `--logo-path` - Path to logo image file
- `--package-name` - Package name (default: folder name)
- `--publisher-name` - Publisher CN (default: CN=<current user>)
- `--quiet` / `-q` - Suppress progress messages
- `--template` - Manifest template type: 'packaged' (full MSIX app, default), 'sparse' (desktop app with package identity for Windows APIs), or 'hostedapp' (script running under Python/Node host) (default: `Packaged`)
- `--verbose` / `-v` - Enable verbose output
- `--version` - App version in Major.Minor.Build.Revision format (e.g., 1.0.0.0). (default: `1.0.0.0`)

#### `winapp manifest update-assets`

Generate new assets for images referenced in an appxmanifest.xml from a single source image. Source image should be at least 400x400 pixels.

**Arguments:**
- `<image-path>` *(required)* - Path to source image file

**Options:**
- `--manifest` - Path to AppxManifest.xml file (default: search current directory)
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp package`

Build an MSIX installer package from your app's output directory. Combines makeappx packaging with optional code signing. The input folder should contain your compiled app.

**Aliases:** `pack`

**Arguments:**
- `<input-folder>` *(required)* - Input folder with package layout

**Options:**
- `--cert` - Path to signing certificate (will auto-sign if provided)
- `--cert-password` - Certificate password (default: password) (default: `password`)
- `--generate-cert` - Generate a new development certificate
- `--install-cert` - Install certificate to machine
- `--manifest` - Path to AppX manifest file (default: auto-detect from input folder or current directory)
- `--name` - Package name (default: from manifest)
- `--output` - Output msix file name for the generated package (defaults to <name>.msix)
- `--publisher` - Publisher name for certificate generation
- `--quiet` / `-q` - Suppress progress messages
- `--self-contained` - Bundle Windows App SDK runtime for self-contained deployment
- `--skip-pri` - Skip PRI file generation
- `--verbose` / `-v` - Enable verbose output
### `winapp restore`

Reinstall packages defined in winapp.yaml. Use this after cloning a project or when packages are missing. Requires an existing winapp.yaml file (created by 'init'). Does not update package versions - use 'update' for that.

**Arguments:**
- `<base-directory>` - Base/root directory for the winapp workspace

**Options:**
- `--config-dir` - Directory to read configuration from (default: current directory)
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp sign`

Code-sign a package or executable with a certificate. Use a development certificate for testing, or a trusted certificate for distribution.

**Arguments:**
- `<file-path>` *(required)* - Path to the file/package to sign
- `<cert-path>` *(required)* - Path to the certificate file (PFX format)

**Options:**
- `--password` - Certificate password (default: `password`)
- `--quiet` / `-q` - Suppress progress messages
- `--timestamp` - Timestamp server URL
- `--verbose` / `-v` - Enable verbose output
### `winapp tool`

Execute Windows SDK build tools (makeappx.exe, signtool.exe, makepri.exe, etc.). Automatically downloads and caches Build Tools if not present. Pass the tool name followed by its arguments.

**Aliases:** `run-buildtool`

**Options:**
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp update`

Check for newer versions of packages in winapp.yaml and update them. Also refreshes build tools cache. Requires an existing winapp.yaml file (created by 'init'). Use --setup-sdks to control whether to use stable, preview, or experimental SDK versions.

**Options:**
- `--quiet` / `-q` - Suppress progress messages
- `--setup-sdks` - SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)
- `--verbose` / `-v` - Enable verbose output

## Common Workflows

### New Project Setup
1. `winapp init .` - Initialize workspace with appxmanifest.xml, image assets, test certicate, and optionally SDK projections in the .winapp folder. (run with `--use-defaults` to make it non-interactive)
2. Edit `appxmanifest.xml` if need to modify properties, set capabilities, or other configurations
3. Build your app
4. `winapp create-debug-identity <exe-path>` - to generate package identity from generated appxmanifest.xml before running the app so the exe has package identity
5. Run the app
4. `winapp pack <output-folder-to-package> --cert .\devcert.pfx` - Create signed MSIX (--cert is optional)

### Existing Project (Clone/CI)
1. `winapp restore` - Reinstall packages and generate C++ projections from `winapp.yaml`
2. Build and package as normal

### Update SDK Versions
1. `winapp update` - Check for and install newer SDK versions
2. Rebuild your app

### Debug with Package Identity
For apps that need Windows APIs requiring identity (push notifications, etc.):
1. Ensure a appxmanifest.xml is present, either via `winapp init` or `winapp manifest generate`
2. `winapp create-debug-identity ./myapp.exe` - generate package identity from generated appxmanifest.xml before running the app so the exe has package identity
3. Run your app - it now has package identity

### Electron Apps
1. `winapp init` - Set up workspace (run with --use-defaults to make it non-interactive)
2. `winapp node create-addon --template cs` - Generate native C# addon for Windows APIs (`--template cpp` for C++ addon)
3. `winapp node add-electron-debug-identity` - Enable identity for debugging
4. `npm start` to launch app normally, but now with identity
4. For production, create production files with the prefered packager and run `winapp pack <generated-production-files> --cert .\devcert.pfx`

## Prerequisites & State

| Command | Requires | Creates/Modifies |
|---------|----------|------------------|
| `init` | Nothing | `winapp.yaml`, `.winapp/`, `appxmanifest.xml`, `Assets/`, `.devcert.pfx` |
| `restore` | `winapp.yaml` | `.winapp/packages/` |
| `update` | `winapp.yaml` | Updates versions in `winapp.yaml` |
| `manifest generate` | Nothing | `appxmanifest.xml`, `Assets/` |
| `cert generate` | Nothing (or `appxmanifest.xml` for publisher inference) | `*.pfx` file |
| `package` | App build output + `appxmanifest.xml` (+ `devcert.pfx` for optional signing) | `*.msix` file |
| `create-debug-identity` | `appxmanifest.xml` + exe | Registers sparse package with Windows |

## Machine-Readable Schema

For programmatic access to the complete CLI structure including all options, types, and defaults:

```bash
winapp --cli-schema
```

This outputs JSON that can be parsed by tools and LLMs. See [cli-schema.json](cli-schema.json).

