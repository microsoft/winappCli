# Windows SDK Package

A comprehensive Node.js package for downloading and managing Windows SDK and BuildTools for native addon development. Automatically downloads SDK packages from NuGet and provides easy access to Windows development tools.

## ✨ Features

- 🚀 **Automated SDK Setup**: Download and extract Windows SDK packages from NuGet
- 🔧 **CppWinRT Integration**: Automatically generate projection headers
- 📦 **Smart Package Management**: Version-specific folder organization with automatic latest version detection
- 🛠️ **BuildTools Integration**: Easy access to Windows SDK BuildTools with automatic PATH management
- 🎯 **Architecture Detection**: Automatic detection and selection of appropriate architecture
- 🔄 **Experimental Version Support**: Option to include pre-release/experimental versions
- 📱 **CLI Interface**: Clean command-line interface for setup and tool execution
- ⚡ **Auto-Installation**: Packages are automatically set up on install

## 📦 Installation

```bash
npm install windows-sdks
```

The SDK packages will be automatically downloaded during installation. If you need to set them up manually later:

```bash
winsdk setup
```

## 🚀 Quick Start

### Command Line Usage

```bash
# Setup all SDKs and generate CppWinRT headers (done automatically on install)
winsdk setup

# Run Windows build tools with automatic PATH management
winsdk tool mt.exe -manifest app.manifest -outputresource:app.exe
winsdk tool signtool.exe sign /fd SHA256 /f cert.pfx app.exe
winsdk tool makeappx.exe pack /o /d "./msix" /p "./dist/app.msix"

# Get help
winsdk help

# Check version
winsdk version
```

### Programmatic Usage

```javascript
const { 
  setupSDKs, 
  downloadAllSDKPackages, 
  runCppWinRT,
  execSyncWithBuildTools,
  getBuildToolPath 
} = require('windows-sdks');

// Complete setup (download SDKs + generate CppWinRT headers)
await setupSDKs();

// Just download SDK packages
await downloadAllSDKPackages();

// Run build tools with automatic PATH management
await execSyncWithBuildTools('mt.exe -manifest app.manifest -outputresource:app.exe');

// Get path to a specific tool
const mtPath = await getBuildToolPath('mt.exe');
```

## 📖 API Reference

### CLI Commands

#### `winsdk setup`
Download all SDK packages and generate CppWinRT projection headers.

#### `winsdk tool <command> [args...]`
Run Windows build tools with automatic PATH management.

**Examples:**
```bash
winsdk tool mt.exe -manifest app.manifest -outputresource:app.exe
winsdk tool signtool.exe sign /fd SHA256 /f cert.pfx app.exe
winsdk tool makeappx.exe pack /o /d "./msix" /p "./dist/app.msix"
```

### JavaScript API

#### `setupSDKs(options)`

Complete SDK setup: downloads packages and generates CppWinRT headers.

**Options:**
- `outputDir` - Directory for packages (default: `.winsdk/packages` in project root)
- `cppWinRTOutputDir` - Output directory for generated headers (default: `.winsdk/generated/include` in project root)
- `skipExisting` - Skip packages already downloaded (default: `true`)
- `verbose` - Show progress messages (default: `true`)

```javascript
await setupSDKs({
  outputDir: './my-sdks',
  cppWinRTOutputDir: './generated-headers',
  verbose: true
});
```

#### `downloadAllSDKPackages(options)`

Download all SDK packages to a specified directory.

**Options:**
- `outputDir` - Directory to download packages to (default: `.winsdk/packages` in project root)
- `skipExisting` - Skip packages already downloaded (default: `true`)
- `keepDownloads` - Keep .nupkg files after extraction (default: `false`)
- `verbose` - Show progress messages (default: `true`)

**Returns:** Object with package names as keys and download results as values.

#### `downloadAndExtractNuGetPackage(packageName, extractPath, options)`

Download and extract any NuGet package.

**Parameters:**
- `packageName` - Name of the NuGet package
- `extractPath` - Directory to extract to (optional, defaults to `.winsdk/packages` in project root)
- `options` - Configuration options

**Options:**
- `version` - Specific version to download (defaults to latest stable)
- `downloadPath` - Directory for .nupkg file (defaults to extractPath)
- `keepDownload` - Keep .nupkg file after extraction (default: `false`)
- `verbose` - Show progress messages (default: `true`)
- `includeExperimental` - Include experimental/pre-release versions (default: `false`)

**Returns:** Object with `{ version, path }` of extracted package.

```javascript
// Download latest stable version
const result = await downloadAndExtractNuGetPackage('Newtonsoft.Json');
console.log(`Downloaded ${result.version} to ${result.path}`);

// Download specific experimental version
await downloadAndExtractNuGetPackage('Microsoft.WindowsAppSDK', './packages', {
  version: '1.7.250127003-experimental3',
  includeExperimental: true,
  verbose: true
});

// Download latest including experimental versions
await downloadAndExtractNuGetPackage('SomePackage', './packages', {
  includeExperimental: true
});
```

#### `getPackagePath(packageName, extractPath, version)`

Get the path of a downloaded NuGet package.

**Parameters:**
- `packageName` - Name of the package
- `extractPath` - Base directory (optional, defaults to `.winsdk/packages` in project root)
- `version` - Specific version (optional, returns latest if not specified)

**Returns:** Full path to package or `null` if not found.

```javascript
// Get latest downloaded version
const latestPath = getPackagePath('Microsoft.WindowsAppSDK');

// Get specific version
const specificPath = getPackagePath('Microsoft.WindowsAppSDK', './.winsdk/packages', '1.4.231008000');
```

#### `execSyncWithBuildTools(command, options)`

Execute a command with BuildTools bin path added to PATH environment.

**Parameters:**
- `command` - The command to execute
- `options` - Options passed to execSync (optional)

**Returns:** Buffer with command output.

#### `getBuildToolPath(toolName)`

Get the full path to a specific BuildTools executable.

**Parameters:**
- `toolName` - Name of the tool (e.g., 'mt.exe', 'signtool.exe')

**Returns:** Full path to the executable.

#### `ensureBuildTools()`

Ensure BuildTools are available and return the bin path.

**Returns:** Path to the BuildTools bin directory.

### NuGet Utility Functions

#### `getNuGetPackageVersions(packageName)`

Get all available versions of a NuGet package.

**Returns:** Array of version strings.

#### `getLatestVersion(versions, includeExperimental)`

Get the latest version from an array of versions.

**Parameters:**
- `versions` - Array of version strings
- `includeExperimental` - Include experimental versions (default: `false`)

**Returns:** Latest version string.

#### `compareVersions(a, b)`

Compare two version strings.

**Returns:** -1, 0, or 1 for version comparison.

## 📁 Package Management

### Automatic Organization

Packages are automatically organized in NuGet's standard format:

```
.winsdk/
└── packages/
    ├── Microsoft.Windows.CppWinRT.2.0.230706.1/
    ├── Microsoft.Windows.CppWinRT.2.0.240405.15/
    ├── Microsoft.WindowsAppSDK.1.4.231008000/
    ├── Microsoft.WindowsAppSDK.1.5.240311000/
    └── Microsoft.Windows.SDK.BuildTools.10.0.26100.1/
```

### Version Support

- **Stable Versions**: Default behavior, filters out pre-release versions
- **Experimental Versions**: Use `includeExperimental: true` to include versions with `-` (e.g., `1.7.250127003-experimental3`)
- **Specific Versions**: Download any specific version by name
- **Latest Detection**: Automatically finds the latest downloaded version

## 🔧 Included SDK Packages

The package automatically downloads and manages these Windows SDK packages:

- `Microsoft.Windows.CppWinRT` - C++/WinRT projection headers
- `Microsoft.Windows.SDK.BuildTools` - Windows SDK build tools
- `Microsoft.WindowsAppSDK` - Windows App SDK
- `Microsoft.Windows.ImplementationLibrary` - Windows Implementation Library
- `Microsoft.Windows.SDK.CPP` - Windows SDK C++ headers
- `Microsoft.Windows.SDK.CPP.x64` - Windows SDK C++ headers (x64)
- `Microsoft.Windows.SDK.CPP.arm64` - Windows SDK C++ headers (ARM64)

## 🛠️ Available Build Tools

The BuildTools package includes many useful tools:

### **Manifest & Resources**
- `mt.exe` - Manifest Tool
- `rc.exe` - Resource Compiler
- `makecat.exe` - Catalog File Maker

### **Code Signing**
- `signtool.exe` - Digital Signature Tool
- `MakeCert.exe` - Certificate Creation Tool

### **Packaging**
- `makeappx.exe` - APPX/MSIX Package Tool
- `makepri.exe` - Package Resource Index Tool

### **Development Tools**
- `midl.exe` - MIDL Compiler
- `uuidgen.exe` - UUID Generator
- `tracewpp.exe` - WPP Tracing

## 📁 Directory Structure

After setup, you'll have:

```
your-project/
├── .winsdk/
│   ├── packages/                   # Downloaded SDK packages (NuGet format)
│   │   ├── Microsoft.Windows.CppWinRT.2.0.240405.15/
│   │   ├── Microsoft.WindowsAppSDK.1.5.240311000/
│   │   └── ...
│   └── generated/
│       └── include/                # Generated CppWinRT headers
│           ├── winrt/
│           └── ...
├── package.json
└── ...
```

## 🏗️ Architecture Support

The utility automatically detects your system architecture and uses the appropriate tools:

- **x64**: 64-bit Windows (most common)
- **x86**: 32-bit Windows 
- **arm64**: ARM64 Windows

If your preferred architecture isn't available, it gracefully falls back to compatible alternatives.

## 💻 System Requirements

- Node.js 14.0.0 or higher
- Windows operating system
- PowerShell (for package extraction)

## 🔄 Examples

### Basic Setup

```javascript
const { setupSDKs } = require('windows-sdks');

async function setupProject() {
  try {
    await setupSDKs({
      verbose: true
    });
    console.log('✅ Project setup complete!');
  } catch (error) {
    console.error('❌ Setup failed:', error.message);
  }
}

setupProject();
```

### Using with Build Scripts

Add to your `package.json`:

```json
{
  "scripts": {
    "setup-sdks": "winsdk setup",
    "build:manifest": "winsdk tool mt.exe -manifest app.manifest -outputresource:app.exe",
    "build:sign": "winsdk tool signtool.exe sign /fd SHA256 /f cert.pfx app.exe",
    "build:package": "winsdk tool makeappx.exe pack /o /d \"./msix\" /p \"./dist/app.msix\"",
    "prebuild": "npm run setup-sdks",
    "build": "node-gyp build"
  }
}
```

### Working with Experimental Versions

```javascript
const { downloadAndExtractNuGetPackage, getLatestVersion, getNuGetPackageVersions } = require('windows-sdks');

// Get all versions including experimental
const versions = await getNuGetPackageVersions('Microsoft.WindowsAppSDK');
const latestExperimental = getLatestVersion(versions, true);

// Download latest experimental version
await downloadAndExtractNuGetPackage('Microsoft.WindowsAppSDK', './packages', {
  includeExperimental: true,
  verbose: true
});

// Download specific experimental version
await downloadAndExtractNuGetPackage('Microsoft.WindowsAppSDK', './packages', {
  version: '1.7.250127003-experimental3',
  verbose: true
});
```

### Custom Package Management

```javascript
const { downloadAndExtractNuGetPackage, getPackagePath } = require('windows-sdks');

// Download any NuGet package
const result = await downloadAndExtractNuGetPackage('Newtonsoft.Json', './vendor');
console.log(`Downloaded to: ${result.path}`);

// Check if package exists
const packagePath = getPackagePath('Newtonsoft.Json', './vendor');
if (packagePath) {
  console.log(`Package found at: ${packagePath}`);
} else {
  console.log('Package not found');
}
```

## 🚀 Migration Guide

### From Manual SDK Management

**Before:**
```javascript
const mtExePath = "C:\\Program Files\\Windows Kits\\10\\bin\\10.0.19041.0\\x64\\mt.exe";
execSync(`"${mtExePath}" -manifest app.manifest -outputresource:app.exe`);
```

**After:**
```bash
# CLI approach
winsdk tool mt.exe -manifest app.manifest -outputresource:app.exe
```

```javascript
// Programmatic approach
const { execSyncWithBuildTools } = require('windows-sdks');
await execSyncWithBuildTools('mt.exe -manifest app.manifest -outputresource:app.exe');
```

## 📝 License

MIT

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 🐛 Support

If you encounter any issues or have questions, please [open an issue](https://github.com/microsoft/winsdk/issues) on GitHub.
