# CLI Documentation and Usage

### init

Initialize a directory with Windows SDK, Windows App SDK, and required assets for modern Windows development.

```bash
winapp init [base-directory] [options]
```

**Arguments:**

- `base-directory` - Base/root directory for the app/workspace (default: current directory)

**Options:**

- `--config-dir <path>` - Directory to read/store configuration (default: current directory)
- `--setup-sdks` - SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)
- `--ignore-config`, `--no-config` - Don't use configuration file for version management
- `--no-gitignore` - Don't update .gitignore file
- `--use-defaults`, `--no-prompt` - Do not prompt, and use default of all prompts
- `--no-cert` - Skip development certificate generation
- `--config-only` - Only handle configuration file operations, skip package installation

**What it does:**

- Creates `winapp.yaml` configuration file
- Downloads Windows SDK and Windows App SDK packages
- Generates C++/WinRT headers and binaries
- Creates development certificate and AppxManifest.xml
- Sets up build tools and enables developer mode
- Updates .gitignore to exclude generated files
- Stores sharable files in the global cache directory

**Examples:**

```bash
# Initialize current directory
winapp init

# Initialize with experimental packages
winapp init --setup-sdks experimental

# Initialize specific directory without promts
winapp init ./my-project --use-defaults
```

---

### restore

Restore packages and regenerate files based on existing `winapp.yaml` configuration.

```bash
winapp restore [options]
```

**Options:**

- `--config-dir <path>` - Directory containing winapp.yaml (default: current directory)

**What it does:**

- Reads existing `winapp.yaml` configuration
- Downloads/updates SDK packages to specified versions
- Regenerates C++/WinRT headers and binaries
- Stores sharable files in the global cache directory

**Examples:**

```bash
# Restore from winapp.yaml in current directory
winapp restore
```

---

### update

Update packages to their latest versions and update the configuration file.

```bash
winapp update [options]
```

**Options:**

- `--config-dir <path>` - Directory containing winapp.yaml (default: current directory)
- `----setup-sdks` - SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)

**What it does:**

- Reads existing `winapp.yaml` configuration
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

Create MSIX packages from prepared application directories. Requires appxmanifest.xml file to be present in the target directory, in the current directory, or passed with the `--manifest` option. (run `init` or `manifest generate` to create a manifest)

```bash
winapp pack <input-folder> [options]
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

- Validates and processes AppxManifest.xml files
- Ensures proper framework dependencies
- Updates side-by-side manifests with registrations
- Handles self-contained WinAppSDK deployment
- Signs package if certificate provided

**Examples:**

```bash
# Package directory with auto-detected manifest
winapp pack ./dist

# Package with custom output name and certificate
winapp pack ./dist --output MyApp.msix --cert ./cert.pfx

# Package with generated and installed certificate and self-contained WinAppSDK runtime
winapp pack ./dist --generate-cert --install-cert --self-contained
```

---

### create-debug-identity

Create app identity for debugging without full MSIX packaging using [external location/sparse packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps).

```bash
winapp create-debug-identity [entrypoint] [options]
```

**Arguments:**

- `entrypoint` - Path to executable (.exe) or script that needs identity

**Options:**

- `--manifest <path>` - Path to AppxManifest.xml (default: `./appxmanifest.xml`)
- `--no-install` - Don't install the package after creation

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

### manifest

Generate and manage AppxManifest.xml files.

#### manifest generate

Generate AppxManifest.xml from templates.

```bash
winapp manifest generate [directory] [options]
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
- `--if-exists <Error|Overwrite|Skip>` - Set behavior if the certificate file already exists (default: Error)

**Templates:**

- `packaged` - Standard packaged app manifest
- `sparse` - App manifest using [sparse/external location packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)
- `hostedapp` - Hosted app manifest for Python/Node.js scripts

**Examples:**

```bash
# Generate standard manifest interactively
winapp manifest generate

# Generate hosted app manifest for Python script
winapp manifest generate --template hostedapp --entrypoint app.py

# Generate with all options specified
winapp manifest generate ./src --package-name MyApp --publisher-name "CN=My Company" --if-exists overwrite
```

#### manifest update-assets

Generate all required MSIX image assets from a single source image.

```bash
winapp manifest update-assets <image-path> [options]
```

**Arguments:**

- `image-path` - Path to source image file (PNG, JPG, GIF, etc.)

**Options:**

- `--manifest <path>` - Path to AppxManifest.xml file (default: search current directory)

**Description:**

Takes a single source image and automatically generates all 12 required MSIX image assets at the correct dimensions:

- Square44x44Logo.png (44×44)
- Square44x44Logo.scale-200.png (88×88)
- Square44x44Logo.targetsize-24_altform-unplated.png (24×24)
- Square150x150Logo.png (150×150)
- Square150x150Logo.scale-200.png (300×300)
- Wide310x150Logo.png (310×150)
- Wide310x150Logo.scale-200.png (620×300)
- SplashScreen.png (620×300)
- SplashScreen.scale-200.png (1240×600)
- StoreLogo.png (50×50)
- LockScreenLogo.png (24×24)
- LockScreenLogo.scale-200.png (48×48)

The command scales images proportionally while maintaining aspect ratio, centering them with transparent backgrounds when needed. Assets are saved to the `Assets` directory relative to the manifest location.

**Examples:**

```bash
# Generate assets with auto-detected manifest
winapp manifest update-assets mylogo.png

# Specify manifest location explicitly
winapp manifest update-assets mylogo.png --manifest ./dist/appxmanifest.xml

# With verbose output
winapp manifest update-assets mylogo.png --verbose
```

---

### cert

Generate and install development certificates.

#### cert generate

Generate development certificates for package signing.

```bash
winapp cert generate [options]
```

**Options:**

- `--manifest <appxmanifest.xml>` - Extract publisher information from appxmanifest.xml 
- `--publisher <name>` - Publisher name for certificate
- `--output <path>` - Output certificate file path
- `--password <password>` - Certificate password (default: "password")
- `--valid-days <valid-days>` - Number of days the certificate is valid (default: 365)
- `--install` - Install the certificate to the local machine store after generation
- `--if-exists <Error|Overwrite|Skip>` - Set behavior if the certificate file already exists (default: Error)

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

# Install certificate to machine
winapp cert install ./mycert.pfx
```

---

### sign

Sign MSIX packages and executables with certificates.

```bash
winapp sign <file-path> [options]
```

**Arguments:**

- `file-path` - Path to MSIX package or executable to sign

**Options:**

- `--cert <path>` - Path to signing certificate
- `--cert-password <password>` - Certificate password (default: "password")

**Examples:**

```bash
# Sign MSIX package
winapp sign MyApp.msix --cert ./mycert.pfx

# Sign executable
winapp sign ./bin/MyApp.exe --cert ./mycert.pfx --cert-password mypassword
```

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

*(Available in NPM package only)* Add app identity to Electron development process by using sparse packaging. Requires an appxmanifest.xml (create one with `winapp init` or `winapp manifest generate` if you don't have one).

> [!IMPORTANT]  
> There is a known issue with sparse packaging Electron applications which causes the app to crash on start or not render the web content. The issue has been fixed in Windows but it has not propagated to external Windows devices yet. If you are seeing this issue after calling `add-electron-debug-identity`, you can [disable sandboxing in your Electron app](https://www.electronjs.org/docs/latest/tutorial/sandbox#disabling-chromiums-sandbox-testing-only) for debug purposes with the `--no-sandbox` flag. This issue does not affect full MSIX packaging.
<br /><br />
To undo the Electron debug identity, the easiest approach is to clear your node_modules folder (or just the electron folder) and run `npm install` again.

```bash
npx winapp node add-electron-debug-identity [options]
```

**Options:**

| Option | Description |
|--------|-------------|
| `--manifest <path>` | Path to custom appxmanifest.xml (default: appxmanifest.xml in current directory) |
| `--verbose` | Enable verbose output |

**What it does:**

- Registers debug identity for electron.exe process
- Enables testing identity-requiring APIs in Electron development
- Uses existing AppxManifest.xml for identity configuration

**Examples:**

```bash
# Add identity to Electron development process
npx winapp node add-electron-debug-identity

# Use a custom manifest file
npx winapp node add-electron-debug-identity --manifest ./custom/appxmanifest.xml
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

In **Powershell** and **pwsh**:
```pwsh
# Set a custom location for winapp's global cache
$env:WINAPP_CLI_CACHE_DIRECTORY=d:\temp\.winapp
```

Winapp will create this directory automatically when you run commands like `init` or `restore`.