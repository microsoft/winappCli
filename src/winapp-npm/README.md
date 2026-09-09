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

**Node.js/Electron Specific:**

- [`node create-addon`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#node-create-addon) - Generate native C# or C++ addons
- [`node generate-bindings`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#node-generate-bindings) - Regenerate JS bindings for Windows App SDK APIs after editing `winapp.jsBindings`
- [`node add-electron-debug-identity`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#node-add-electron-debug-identity) - Add identity to Electron processes

The full CLI usage can be found here: [Documentation](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md)

### Programmatic API

The package also exports typed async functions for all CLI commands and utility helpers, so you can use them directly from TypeScript/JavaScript without spawning a CLI process:

```typescript
import { init, packageApp, certGenerate } from '@microsoft/winappcli';

await init({ useDefaults: true });
await certGenerate({ install: true });
await packageApp({ inputFolder: './dist', cert: './devcert.pfx' });
```

Full programmatic API reference: [NPM API Documentation](https://github.com/microsoft/WinAppCli/blob/main/docs/npm-usage.md)

> **Note — the programmatic API runs the CLI non-interactively.** The wrapper functions capture output and give the native process piped stdin, so commands that would normally prompt cannot do so. For `azSign` in particular this means you must pass either a `metadataFile` or a fully specified identity (`subscription`, `resourceGroup`, `account`, and `profile`), and a non-interactive Azure credential must already be available (for example `AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET`, OIDC, a managed identity, or an existing `az login` session). Calls that would otherwise require a selection prompt or an interactive `az login` fail instead of prompting.

#### Cancelling a call

Every command option object accepts a `signal`. It cancels the whole native invocation and rejects
with an `AbortError`:

```typescript
const controller = new AbortController();
setTimeout(() => controller.abort(), 30_000);

await uiClick({ app: 'notepad', selector: 'btn-save-c3d4', signal: controller.signal });
```

On Windows the child is force-terminated, so the CLI's own cleanup may not run. That is safe —
Windows releases the process's coordination handles and other `winapp ui` processes reclaim its queue
entry — but if the abort lands after the command already had the desktop, UI side effects may already
have happened, and aborting an active recording can leave partial output with no graceful MP4
finalization.

#### Driving UI from several workflows

`winapp ui` commands that touch the physical desktop always arbitrate for it — that is on by default
and cannot be turned off, so two agents can never type into each other's windows.

What is opt-in is *continuity*. Pass the same `workflowId` to every call that belongs to one logical
workflow and they keep the desktop reserved between invocations for a short idle grace, may overlap
with each other (a recording and the clicks it is recording), and are never interleaved with another
workflow's input:

```typescript
const workflowId = crypto.randomUUID();

await uiClick({ app: 'notepad', selector: 'btn-file-a1b2', workflowId });
await uiSendKeys({ app: 'notepad', keys: 'hello', workflowId });
```

`workflowId` is applied to the spawned child only — the wrapper never mutates `process.env`, so it
cannot leak into unrelated concurrent calls. Setting `WINAPP_UI_WORKFLOW_ID` in the environment works
too and is inherited by every child.

Without a `workflowId` each call is a self-contained one-shot: it still waits its turn, but releases
the desktop the moment it finishes. That also means a no-`workflowId` `uiRecord` blocks every other
workflow for its whole duration — to record and click at the same time, give both calls the same
`workflowId`.

See [UI Automation → Coordinating concurrent UI workflows](https://github.com/microsoft/WinAppCli/blob/main/docs/ui-automation.md#coordinating-concurrent-ui-workflows).

`uiRecord` still requires a finite positive `durationSec`: `signal` can only stop a recording by
killing it, which does not produce a valid MP4.

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
