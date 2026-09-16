<!-- mslearn: true -->
<!-- ms.topic: overview -->
<!-- description: Command-line interface for managing Windows SDKs, packaging, app identity, manifests, certificates, and build tools for any app framework. -->
# Windows App Development CLI (winapp CLI)

> [!IMPORTANT]
> The Windows App Development CLI is currently in **public preview**. Features and commands may change before the final release. Share your feedback by [creating an issue](https://github.com/microsoft/WinAppCli/issues).

The Windows App Development CLI (winapp CLI) is a single command-line interface for managing Windows SDKs, packaging, generating app identity, manifests, certificates, and using build tools with any app framework. This tool bridges the gap between cross-platform development and Windows-native capabilities.

Whether you're building with .NET/Win32, CMake, Electron, or Rust, this CLI gives you access to:

- **Modern Windows APIs** - [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/) and Windows SDK with automatic setup and code generation
- **Package Identity** - Debug and test by adding package identity without full packaging
- **MSIX Packaging** - App packaging with signing and Store readiness
- **Developer Tools** - Manifests, certificates, assets, and build integration

## Why package identity?

Many Windows APIs require your app to have package identity. With identity, your app gains access to features like notifications, OS integration, and on-device AI. For a full list of what package identity unlocks and help choosing the right packaging model, see [Packaging overview](https://learn.microsoft.com/windows/apps/package-and-deploy/packaging/).

## Installation

### WinGet

The easiest way to install the CLI is via WinGet (Windows Package Manager):

```powershell
winget install Microsoft.winappcli --source winget
```

### NPM

For Electron projects, install via NPM:

```powershell
npm install @microsoft/winappcli --save-dev
```

### GitHub Actions / Azure DevOps

For CI/CD pipelines, use the [`setup-WinAppCli`](https://github.com/microsoft/setup-WinAppCli) action to automatically install the CLI on your runners/agents.

### Manual download

Download the latest build from [GitHub Releases](https://github.com/microsoft/WinAppCli/releases/latest).

## Verify installation

Once installed, verify the installation by calling the CLI:

```bash
winapp --help
```

Or if using Electron/Node.js:

```bash
npx winapp --help
```

## Supported frameworks

winapp CLI works with a variety of app frameworks:

| Framework | Guide |
|-----------|-------|
| .NET / WPF / WinForms | [Get started with .NET](guides/dotnet.md) |
| C++ (CMake) | [Get started with C++](guides/cpp.md) |
| Electron | [Get started with Electron](guides/electron/index.md) |
| Rust | [Get started with Rust](guides/rust.md) |
| Tauri | [Get started with Tauri](guides/tauri.md) |
| Flutter | [Get started with Flutter](guides/flutter.md) |

Additional guides:
- [Packaging an EXE/CLI](guides/packaging-cli.md): step-by-step guide for packaging an existing EXE/CLI as MSIX
- [Sparse packaging](guides/sparse.md): give an unpackaged app package identity with an identity-only (sparse) MSIX and external content
- [Shell Completion](guides/shell-completion.md): enable tab completion for commands, options, and values in PowerShell, bash, zsh, and fish
- [Security guidance](security.md): what development certificates and Developer Mode change on your machine, how to handle `devcert.pfx`, and how to sign for production

## Commands overview

| Category | Commands |
|----------|----------|
| **Setup** | [init](usage.md#init), [restore](usage.md#restore), [update](usage.md#update) |
| **Identity & Debugging** | [run](usage.md#run), [create-debug-identity](usage.md#create-debug-identity), [embed-identity](usage.md#embed-identity), [unregister](usage.md#unregister) |
| **Packaging** | [pack](usage.md#pack) |
| **Manifests** | [manifest generate](usage.md#manifest-generate), [manifest update-assets](usage.md#manifest-update-assets), [manifest add-alias](usage.md#manifest-add-alias) |
| **Certificates & Signing** | [cert generate](usage.md#cert-generate), [cert install](usage.md#cert-install), [sign](usage.md#sign), [create-external-catalog](usage.md#create-external-catalog) |
| **Utilities** | [tool](usage.md#tool), [store](usage.md#store), [get-winapp-path](usage.md#get-winapp-path), [complete](usage.md#shell-completion) |
| **UI Automation** | [ui](usage.md#ui) |
| **Node.js/Electron** | [node create-addon](usage.md#node-create-addon), [node add-electron-debug-identity](usage.md#node-add-electron-debug-identity), [node clear-electron-debug-identity](usage.md#node-clear-electron-debug-identity) |

For the full CLI reference, see [CLI reference](usage.md).

## Open source

winapp CLI is open source. You can find the source code, file issues, and contribute on [GitHub](https://github.com/microsoft/WinAppCli).

## Related topics

- [CLI reference](usage.md)
- [Debugging with package identity](debugging.md)
- [Security guidance](security.md)
- [UI automation](ui-automation.md)
- [Windows Sandbox execution](sandbox-execution.md)
- [NPM programmatic API](npm-usage.md)
- [Framework guides](guides/dotnet.md)
- [Get started with Electron](guides/electron/index.md)
- [Windows App SDK documentation](/windows/apps/windows-app-sdk/)
- [MSIX packaging documentation](/windows/msix/)
