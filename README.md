<p align="center">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="./docs/images/hero-dark.png">
      <source media="(prefers-color-scheme: light)" srcset="./docs/images/hero-light.png">
      <img  src="./docs/images/hero-dark.png">
    </picture>
</p>

<h3 align="center">
  <a href="#-installation">Installation</a>
  <span> . </span>
  <a href="#-usage">Usage</a>
  <span> . </span>
  <a href="./docs/usage.md">Documentation</a>
  <span> . </span>
  <a href="#-windows-identity-tool">GUI (App)</a>
  <span> . </span>
  <a href="#feedback">Feedback</a>
</h3>
<br/><br/>
The Windows Development CLI bridges the gap between cross-platform development and Windows-native capabilities, while also making packaging and app identity a "one-click" implementation.
<br/><br/>

Whether you're building with Electron, .NET/Win32, CMake or Python, this CLI gives you access to:

- **Modern Windows APIs** - Windows App SDK and Windows SDK with automatic setup and code generation
- **App Identity** - Debug and test by adding app identity without full packaging in a snap
- **MSIX Packaging** -  App packaging with signing and Store readiness
- **Developer Tools** - Manifests, certificates, assets, and build integration
<br/><br/>

Perfect for:

- **Electron/cross-platform developers** wanting native Windows features or targeting Windows
- **Developers testing and deploying** adding app identity for development or packaging for deployment
- **CI/CD pipelines** automating Windows app builds
</div>

## 📦 Installation

The easiest way to use the CLI is to download automated nightly build from GitHub Releases.

**[👉 Download Latest Build](https://github.com/microsoft/winsdk/releases/latest)**

**Available Options:**

| Package | Description | Use Case |
|---------|-------------|----------|
| **`binaries-[version].zip`** | 📦 Standalone Binaries | Portable, no install needed - great for CI/CD |
| **`microsoft-winsdk-[version].tgz`** | 📚 NPM Package | For Node.js/Electron projects |

### Adding to Path

The easiest way to use the CLI globally is to add it to the PATH. **Add to Path**:

Windows Search → Edit the system environment variables → Environment Variables → Path → Edit → New → Add the location (folder) of WinSdk.cli.exe

## 📋 Usage

Once installed (see [Installation](#installation) above), verify the installation by calling the CLI:

```bash
winsdk --help
```

or if using Electron/NodeJS

```bash
npx winsdk --help
```

### Commands Overview

**Setup Commands:**

- [`init`](./docs/usage.md#init) - Initialize project with Windows SDK and App SDK
- [`restore`](./docs/usage.md#restore) - Restore packages and dependencies
- [`update`](./docs/usage.md#update) - Update packages and dependencies to latest versions

**App Identity & Debugging:**

- [`package`](./docs/usage.md#package) - Create MSIX packages from directories
- [`create-debug-identity`](./docs/usage.md#create-debug-identity) - Add temporary app identity for debugging
- [`manifest`](./docs/usage.md#manifest) - Generate and manage AppxManifest.xml files

**Certificates & Signing:**

- [`cert`](./docs/usage.md#cert) - Generate and install development certificates
- [`sign`](./docs/usage.md#sign) - Sign MSIX packages and executables

**Development Tools:**

- [`tool`](./docs/usage.md#tool) - Access Windows SDK tools
- [`get-winsdk-path`](./docs/usage.md#get-winsdk-path) - Get paths to installed SDK components

**Node.js/Electron Specific:**

- [`node create-addon`](./docs/usage.md#node-create-addon) - Generate native C++ addons
- [`node add-electron-debug-identity`](./docs/usage.md#node-add-electron-debug-identity) - Add identity to Electron processes

The full CLI usage can be found here: [Documentation](/docs/usage.md)

## 🧪 Windows Identity Tool

This is an **experimental** app (GUI) that wraps the CLI and provides an intuitive, drag-and-drop experience with the following features:

- Supports .NET (Winforms, WPF..etc) apps, Python scripts/folders, MSIX
- Drop in a WinForms, WPF executable (.exe) to add development/debug app identity (via external location/sparse packaging) in a single click!
- Drop in a WinForms, WPF folder to package your app (MSIX) in a single click
- Drop in an MSIX to sign and register it locally in a single click
- Drop in a Python (.py) file to add debug identity in a single click

<div align="center">
  <table>
    <tr>
      <td width="50%">
        <img src="./docs/images/identity-gui-tool.png" alt="Windows Identity Tool Interface" width="100%" />
      </td>
      <td width="50%">
        <img src="./docs/images/identity-gui-tool-options.png" alt="Windows Identity Tool Options" width="100%" />
      </td>
    </tr>
  </table>
</div>

### Install the GUI Tool

**[👉 Download Latest Build (.exe)](https://github.com/microsoft/winsdk/releases/latest)**

Alternatively, you can clone and build this repository. Run Identity.GUI.Experimental in Visual Studio to build and run the app.

## Feedback

- Send feedback to <windowsdevelopertoolkit@microsoft.com>: Do you love this tool? Are there features or fixes you want to see? Let us know!
- [File a bug](https://github.com/microsoft/WindowsDevCLI/issues): please ensure that you are not filing a duplicate issue

## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit [Contributor License Agreements](https://cla.opensource.microsoft.com).

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.