# CLI Documentation and Usage

### init

Initialize a directory with Windows SDK, Windows App SDK, and required assets for modern Windows development.

```bash
winsdk init [base-directory] [options]
```

**Arguments:**

- `base-directory` - Base/root directory for the winsdk workspace (default: current directory)

**Options:**

- `--config-dir <path>` - Directory to read/store configuration (default: current directory)
- `--prerelease` - Include prerelease packages from NuGet
- `--ignore-config`, `--no-config` - Don't use configuration file for version management
- `--no-gitignore` - Don't update .gitignore file
- `--yes`, `--no-prompt` - Assume yes to all prompts
- `--no-cert` - Skip development certificate generation
- `--config-only` - Only handle configuration file operations, skip package installation

**What it does:**

- Creates `winsdk.yaml` configuration file
- Downloads Windows SDK and Windows App SDK packages
- Generates C++/WinRT headers and binaries
- Creates development certificate and AppxManifest.xml
- Sets up build tools and enables developer mode
- Updates .gitignore to exclude generated files

**Examples:**

```bash
# Initialize current directory
winsdk init

# Initialize with prerelease packages
winsdk init --prerelease

# Initialize specific directory with auto-yes
winsdk init ./my-project --yes
```

---

### restore

Restore packages and regenerate files based on existing `winsdk.yaml` configuration.

```bash
winsdk restore [options]
```

**Options:**

- `--config-dir <path>` - Directory containing winsdk.yaml (default: current directory)
- `--prerelease` - Include prerelease packages from NuGet

**What it does:**

- Reads existing `winsdk.yaml` configuration
- Downloads/updates SDK packages to specified versions
- Regenerates C++/WinRT headers and binaries

**Examples:**

```bash
# Restore from winsdk.yaml in current directory
winsdk restore

# Restore with prerelease packages
winsdk restore --prerelease
```

---

### update

Update packages to their latest versions and update the configuration file.

```bash
winsdk update [options]
```

**Options:**

- `--config-dir <path>` - Directory containing winsdk.yaml (default: current directory)
- `--prerelease` - Include prerelease packages from NuGet

**What it does:**

- Reads existing `winsdk.yaml` configuration
- Updates all packages to their latest available versions
- Updates the `winsdk.yaml` file with new version numbers
- Regenerates C++/WinRT headers and binaries

**Examples:**

```bash
# Update packages to latest versions
winsdk update

# Update including prerelease packages
winsdk update --prerelease
```

---

### package

Create MSIX packages from prepared application directories. Requires Appxmanifest file to be present in the directory (run `init` or `manifest generate` to create a manifest)

```bash
winsdk package <input-folder> [options]
```

**Arguments:**

- `input-folder` - Directory containing the application files to package

**Options:**

- `--output <filename>` - Output MSIX file name (default: `<name>.msix`)
- `--name <name>` - Package name (default: from manifest)
- `--manifest <path>` - Path to AppxManifest.xml (default: auto-detect)
- `--cert <path>` - Path to signing certificate (enables auto-signing)
- `--cert-password <password>` - Certificate password (default: "password")
- `--generate-cert` - Generate a new development certificate
- `--install-cert` - Install certificate to machine
- `--publisher <name>` - Publisher name for certificate generation
- `--self-contained` - Bundle Windows App SDK runtime
- `--skip-pri` - Skip PRI file generation

**What it does:**

- Validates and processes AppxManifest.xmls
- Ensures proper framework dependencies
- Updates side-by-side manifests with registrations
- Handles self-contained WinAppSDK deployment
- Signs package if certificate provided

**Examples:**

```bash
# Package directory with auto-detected manifest
winsdk package ./dist

# Package with custom output name and certificate
winsdk package ./dist --output MyApp.msix --cert ./cert.pfx

# Package with generated and installed certificate and self-contained runtime
winsdk package ./dist --generate-cert --install-cert --self-contained
```

---

### create-debug-identity

Create app identity for debugging without full MSIX packaging using [external location/sparse packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps).

```bash
winsdk create-debug-identity [entrypoint] [options]
```

**Arguments:**

- `entrypoint` - Path to executable (.exe) or script that needs identity

**Options:**

- `--manifest <path>` - Path to AppxManifest.xml (default: `./appxmanifest.xml`)
- `--no-install` - Don't install the package after creation
- `--location <path>` - Root path of the application (default: parent directory of executable)

**What it does:**

- Modifies executable's side-by-side manifest
- Registers sparse package for identity
- Enables debugging of identity-requiring APIs

**Examples:**

```bash
# Add identity to executable using local manifest
winsdk create-debug-identity ./bin/MyApp.exe

# Add identity with custom manifest location
winsdk create-debug-identity ./dist/app.exe --manifest ./custom-manifest.xml

# Create identity for hosted app script
winsdk create-debug-identity app.py
```

---

### manifest

Generate and manage AppxManifest.xml files.

#### manifest generate

Generate AppxManifest.xml from templates.

```bash
winsdk manifest generate [directory] [options]
```

**Arguments:**

- `directory` - Directory to generate manifest in (default: current directory)

**Options:**

- `--package-name <name>` - Package name (default: folder name)
- `--publisher-name <name>` - Publisher CN (default: CN=\<current user\>)
- `--version <version>` - Version (default: "1.0.0.0")
- `--description <text>` - Description (default: "My Application")
- `--entrypoint <path>` - Entry point executable or script
- `--template <type>` - Template type: `packaged` (default) or `hostedapp`
- `--logo-path <path>` - Path to logo image file
- `--yes`, `-y` - Skip interactive prompts

**Templates:**

- `packaged` - Standard packaged app manifest
- `sparse` - App manifest using [sparse/external location packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)
- `hostedapp` - Hosted app manifest for Python/Node.js scripts

**Examples:**

```bash
# Generate standard manifest interactively
winsdk manifest generate

# Generate hosted app manifest for Python script
winsdk manifest generate --template hostedapp --entrypoint app.py

# Generate with all options specified
winsdk manifest generate ./src --package-name MyApp --publisher-name "CN=My Company" --yes
```

---

### cert

Generate and install development certificates.

#### cert generate

Generate development certificates for code signing.

```bash
winsdk cert generate [options]
```

**Options:**

- `--publisher <name>` - Publisher name for certificate
- `--output <path>` - Output certificate file path
- `--password <password>` - Certificate password (default: "password")

#### cert install

Install certificate to machine certificate store.

```bash
winsdk cert install <cert-path> [options]
```

**Arguments:**

- `cert-path` - Path to certificate file to install

**Examples:**

```bash
# Generate certificate for specific publisher
winsdk cert generate --publisher "CN=My Company" --output ./mycert.pfx

# Install certificate to machine
winsdk cert install ./mycert.pfx
```

---

### sign

Sign MSIX packages and executables with certificates.

```bash
winsdk sign <file-path> [options]
```

**Arguments:**

- `file-path` - Path to MSIX package or executable to sign

**Options:**

- `--cert <path>` - Path to signing certificate
- `--cert-password <password>` - Certificate password (default: "password")

**Examples:**

```bash
# Sign MSIX package
winsdk sign MyApp.msix --cert ./mycert.pfx

# Sign executable
winsdk sign ./bin/MyApp.exe --cert ./mycert.pfx --cert-password mypassword
```

---

### tool

Access Windows SDK tools directly.

```bash
winsdk tool <tool-name> [tool-arguments]
```

**Available tools:**

- `makeappx` - Create and manipulate app packages
- `signtool` - Sign files and verify signatures
- `mt` - Manifest tool for side-by-side assemblies
- And other Windows SDK tools

**Examples:**

```bash
# Use signtool to verify signature
winsdk tool signtool verify /pa MyApp.msix
```

---

### get-winsdk-path

Get paths to installed Windows SDK components.

```bash
winsdk get-winsdk-path [options]
```

**What it returns:**

- Paths to `.winsdk` workspace directory
- Package installation directories
- Generated header locations

---

### node create-addon

*(Node.js/Electron only)* Generate native C++ addon templates with Windows SDK integration.

```bash
npx winsdk node create-addon [options]
```

**Options:**

- `--name <name>` - Addon name (default: "nativeWindowsAddon")
- `--verbose` - Enable verbose output

**What it does:**

- Creates addon directory with template files
- Generates binding.gyp and addon.cc with Windows SDK examples
- Installs required npm dependencies (nan, node-addon-api, node-gyp)
- Adds build script to package.json

**Examples:**

```bash
# Generate addon with default name
npx winsdk node create-addon

# Generate custom named addon
npx winsdk node create-addon --name myWindowsAddon
```

---

### node add-electron-debug-identity

*(Node.js/Electron only)* Add app identity to Electron development process.

```bash
npx winsdk node add-electron-debug-identity [options]
```

**What it does:**

- Registers debug identity for electron.exe process
- Enables testing identity-requiring APIs in Electron development
- Uses existing AppxManifest.xml for identity configuration

**Examples:**

```bash
# Add identity to Electron development process
npx winsdk node add-electron-debug-identity
```

---

### Global Options

All commands support these global options:

- `--verbose`, `-v` - Enable verbose output for detailed logging
- `--quiet`, `-q` - Suppress progress messages
- `--help`, `-h` - Show command help
