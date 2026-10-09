# Windows App Development CLI

> **Status: Public Preview** - The Windows App Development CLI is experimental and in active development. We'd love your feedback! Share your thoughts by creating an [issue](https://github.com/microsoft/WinAppCli/issues).

The Windows App Development CLI is a single command-line interface for managing Windows SDKs, packaging, generating app identity, manifests, certificates, and using build tools with any app framework. The NPM package extends the CLI with Electron specific tools.
<br/><br/>
This CLI gives you access to:

- **Modern Windows APIs** - [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/) and Windows SDK with automatic setup and code generation
- **App Identity** - Debug and test by adding app identity without full packaging in a snap
- **MSIX Packaging** - App packaging with signing and Store readiness
- **Developer Tools** - Manifests, certificates, assets, and build integration

Perfect for:

- **Electron/cross-platform developers** wanting native Windows features or targeting Windows
- **Developers testing and deploying** adding app identity for development or packaging for deployment
- **CI/CD pipelines** automating Windows app builds

## Get started

Checkout our getting started guide for step by step instructions: [Electron guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/index.md).

## 📋 Usage

Install as a development dependency:

```bash
npm install @microsoft/winappcli --save-dev
```

once installed, call it with npx.

```bash
npx winapp --help
```

### Commands Overview

**Setup Commands:**

- [`new`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#new) - Create a new WinUI app from an official Windows App SDK template
- [`init`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#init) - Initialize project with Windows SDK and App SDK
- [`restore`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#restore) - Restore packages and dependencies (also runs the bindings step when the `winapp.jsBindings` namespace is declared in `package.json`)
- [`update`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#update) - Update packages and dependencies to latest versions

**App Identity & Debugging:**

- [`package`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#package) - Create MSIX packages from directories
- [`create-debug-identity`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#create-debug-identity) - Add temporary app identity for debugging
- [`manifest`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#manifest) - Generate and manage AppxManifest.xml files

**Certificates & Signing:**

- [`cert`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#cert) - Generate and install development certificates
- [`sign`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#sign) - Sign MSIX packages and executables
- [`az-sign`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#az-sign) - Sign packages and executables with Azure Trusted Signing

**Development Tools:**

- [`tool`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#tool) - Access Windows SDK tools
- [`get-winapp-path`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#get-winapp-path) - Get paths to installed SDK components
- [`config`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#config) - Show or change your per-user winapp settings

**Node.js/Electron Specific:**

- [`node create-addon`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#node-create-addon) - Generate native C# or C++ addons
- [`node generate-bindings`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#node-generate-bindings) - Regenerate JS bindings for Windows App SDK APIs after editing `winapp.jsBindings`
- [`node add-electron-debug-identity`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#node-add-electron-debug-identity) - Add identity to Electron processes

The full CLI usage can be found here: [Documentation](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md)

### Programmatic API

The package also exports typed async functions for all CLI commands and utility helpers, so you can use them directly from TypeScript/JavaScript without managing child processes yourself:

```typescript
import { init, packageApp, certGenerate } from '@microsoft/winappcli';

await init({ useDefaults: true });
await certGenerate({ install: true });
await packageApp({ inputFolder: './dist', cert: './devcert.pfx' });
```

For results, errors, cancellation, and UI workflows, see the [NPM programmatic guide](https://github.com/microsoft/WinAppCli/blob/main/docs/npm-usage.md). Your editor's completion and the installed TypeScript declarations provide the full API for your package version.

Command wrappers run non-interactively. See the guide for
[required inputs](https://github.com/microsoft/WinAppCli/blob/main/docs/npm-usage.md#calls-are-non-interactive),
[cancellation](https://github.com/microsoft/WinAppCli/blob/main/docs/npm-usage.md#cancel-a-call), and
[coordinated UI calls and bounded recordings](https://github.com/microsoft/WinAppCli/blob/main/docs/npm-usage.md#coordinate-ui-calls-and-record-a-bounded-video).

## 🔧 Feedback

- [File an issue, feature request or bug](https://github.com/microsoft/WinAppCli/issues): please ensure that you are not filing a duplicate issue
- Send feedback to <windowsdevelopertoolkit@microsoft.com>: Do you love this tool? Are there features or fixes you want to see? Let us know!

We are actively working on improving Node and Python support. These features are experimental and we are aware of several issues with these app types.

## 🧾 Samples

[Electron sample](https://github.com/microsoft/WinAppCli/blob/main/samples/electron/README.md): a default Electron Forge generated application + initialized a winapp project with appxmanifest, assets + native addon + C# addon + generates cert

## Support

Need help or have questions about the Windows App Development CLI? Visit our **[Support Guide](https://github.com/microsoft/WinAppCli/blob/main/SUPPORT.md)** for information about our issue templates and triage process.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
