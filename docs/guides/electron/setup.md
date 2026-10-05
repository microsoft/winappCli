<!-- mslearn: true -->
<!-- description: Set up your development environment for adding Windows APIs to an Electron app with the winapp CLI, including SDK install and project init. -->
# Setting Up the Development Environment

This guide walks you through setting up your Electron development environment for Windows API development. You'll install the necessary tools, initialize your project, and configure Windows SDKs.

## Prerequisites

Before you begin, ensure you have:

- **Windows 11** 
- **Node.js 22.13 or later** - `winget install OpenJS.NodeJS --source winget`. Check your version with `node --version`, and upgrade if it is older.
- **.NET SDK v10** - `winget install Microsoft.DotNet.SDK.10 --source winget`
- **Visual Studio with the Native Desktop Workload** - `winget install --id Microsoft.VisualStudio.Community --source winget --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive --wait"`

## Step 1: Create a New Electron App

We'll start with a fresh Electron app using Electron Forge, which provides excellent tooling and packaging support. If you are starting from an existing app, you can skip this step.

```bash
npm create electron-app@latest my-windows-app
cd my-windows-app
```

When prompted by Electron Forge:
- **Bundler**: Select **None** (recommended — native addons work without extra configuration)
- **Language**: Select **JavaScript** (this guide uses JS; TypeScript works too)
- **Electron version**: Select **latest**
- **Initialize git**: Your preference

Verify the app runs:

```bash
npm start
```

You should see the default Electron Forge window. Close it and let's add Windows capabilities!

## Step 2: Install winapp CLI

The Electron workflow requires the **npm package** (`@microsoft/winappcli`) rather than the standalone CLI installed from winget. The npm package includes Node.js-specific helpers (like `add-electron-debug-identity` and `create-addon`) that are not available in the native CLI. If you already have winapp installed from winget, that's fine — the npm package adds Node.js-specific tools as a project dependency and won't conflict with your system installation.

```bash
npm install --save-dev @microsoft/winappcli
```

## Step 3: Initialize the project for Windows development

The `winapp init` command sets up everything you need in one go: app manifest, assets, and SDKs.

Run the following command and follow the prompts:

```bash
npx winapp init .
```

When prompted:
- **Package name**: Press Enter to accept the default (my-windows-app)
- **Publisher name**: Press Enter to accept the default or enter your name
- **Version**: Press Enter to accept 1.0.0.0
- **Entry point**: Press Enter to accept the default (my-windows-app.exe)
- **Setup SDKs**: Select "Stable SDKs"
- **Add JS/TypeScript bindings**: Press Enter to accept the default (**Yes**) to generate JS bindings for Windows App SDK APIs

> [!NOTE]
> `--use-defaults` and non-interactive init skip JS bindings unless you pass `--add-js-bindings`. Run interactive `npx winapp init .` to opt in at the prompt, or use `npx winapp init . --use-defaults --add-js-bindings` for automation.

### What Does `winapp init` Do?

This command sets up everything you need for Windows development:

1. **Creates `.winapp/` folder** containing:
   - Headers and libraries from the **Windows SDK**
   - Headers and libraries from the **Windows App SDK**
   - NuGet packages with the required binaries

2. **Generates `Package.appxmanifest`** - The app manifest required for app identity and MSIX packaging

3. **Creates `Assets/` folder** - Contains app icons and visual assets for your app

4. **Creates `winapp.yaml`** - Tracks SDK versions and project configuration

5. **Installs Windows App SDK runtime** - Required runtime components for modern APIs

6. **Enables Developer Mode in Windows** - Required for debugging our application

7. **Generates JS bindings** - When you opt in, it:
   - Writes the `winapp.jsBindings` block to `package.json`
   - Adds `@microsoft/dynwinrt-codegen` to `devDependencies` — the build-time tool that produces the bindings (pinned to the registry's `latest` version on first run, then left alone)
   - Adds `@microsoft/dynwinrt` to `dependencies` — the runtime that the generated bindings import at execution time (version is chosen by the installed codegen to guarantee ABI compatibility)
   - Generates JS bindings for Windows App SDK APIs into `.winapp/bindings/`

> [!NOTE]
> The `.winapp/` folder is automatically added to `.gitignore` and should not be checked in to source.

You can open `Package.appxmanifest` to further customize properties like the display name, publisher, and capabilities.

> [!TIP]
> **About the Windows SDKs:**
>
> - **[Windows SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/)** - A development platform that lets you build Win32/desktop apps. It's designed around Windows APIs that are coupled to particular versions of the OS. Use this to access core Win32 APIs like file system, networking, and system services.
> 
> - **[Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/)** - A new development platform that lets you build modern desktop apps that can be installed across Windows versions (down to Windows 10 1809). It provides a convenient, OS-decoupled abstraction around the rich catalogue of Windows OS APIs. The Windows App SDK includes WinUI 3 and provides access to modern features like AI capabilities (Phi Silica), notifications, window management, and more that receive regular updates independent of Windows OS releases.
>
> Learn more: [What's the difference between the Windows App SDK and the Windows SDK?](https://learn.microsoft.com/windows/apps/get-started/windows-developer-faq#what-s-the-difference-between-the-windows-app-sdk-and-the-windows-sdk)

## Step 4: Add Restore to Your Build Pipeline

To ensure the Windows SDKs are available when other developers clone your project or in CI/CD pipelines, add a `postinstall` script to your `package.json`:

```json
{
  "scripts": {
    "postinstall": "winapp restore && winapp node add-electron-debug-identity"
  }
}
```

This script automatically runs after `npm install` and does two things:

1. **`winapp restore`** - Downloads and restores all Windows SDK packages to the `.winapp/` folder
2. **`winapp node add-electron-debug-identity`** - Registers your Electron app with debug identity (more on this in the next steps)

> [!IMPORTANT]
> **Electron 42 and newer**: As of Electron 42, the binary is no longer downloaded automatically during `npm install` ([release notes](https://github.com/electron/electron/releases/tag/v42.0.0)). You must download it explicitly before `add-electron-debug-identity` runs:
>
> ```json
> {
>   "scripts": {
>     "postinstall": "npx --no-install install-electron && winapp restore && winapp node add-electron-debug-identity"
>   }
> }
> ```
>
> The `--no-install` flag ensures `npx` only runs the `install-electron` bin shipped by your installed Electron package and never silently downloads anything from the registry. If you pin to Electron < 42, omit this step — the binary is already in place after `npm install`.

Now run `npm install` to trigger the postinstall script and configure the Windows environment:

```bash
npm install
```

> [!NOTE]
> The `postinstall` script runs automatically after every `npm install`. This means the Windows environment will be configured automatically whenever someone clones your project and runs `npm install`.

<details>
<summary><b>💡 Cross-Platform Development (click to expand)</b></summary>

If you're building a cross-platform Electron app and have developers working on macOS or Linux, you'll want to conditionally run the Windows-specific setup. Here's the recommended approach:

Create `scripts/postinstall.js`:
```javascript
if (process.platform === 'win32') {
  const { execSync } = require('child_process');
  const fs = require('fs');
  const path = require('path');

  // Electron 42+ ships an `install-electron` bin and skips the postinstall download.
  // Run it only when present, and use `--no-install` so npx never silently fetches
  // anything from the registry.
  const installElectronBin = path.join(
    'node_modules', '.bin',
    process.platform === 'win32' ? 'install-electron.cmd' : 'install-electron'
  );
  const steps = [];
  if (fs.existsSync(installElectronBin)) {
    steps.push('npx --no-install install-electron');
  }
  steps.push(
    'npx winapp restore',
    'npx winapp cert generate --if-exists skip',
    'npx winapp node add-electron-debug-identity'
  );

  try {
    execSync(steps.join(' && '), { stdio: 'inherit' });
  } catch (error) {
    console.warn('Warning: Windows-specific setup failed. If you are not developing Windows features, you can ignore this.');
  }
} else {
  console.log('Skipping Windows-specific setup on non-Windows platform.');
}
```

Then update `package.json`:
```json
{
  "scripts": {
    "postinstall": "node scripts/postinstall.js"
  }
}
```

This ensures Windows-specific setup only runs on Windows machines, allowing developers on other platforms to work on the project without errors.

</details>

## Step 5: Understanding Debug Identity

The `npm install` you ran in Step 4 triggered the `postinstall` script, which ran `winapp node add-electron-debug-identity`. This gives your app a temporary debug identity so you can test Windows APIs that require app identity during development.

### What Does Debug Identity Do?

This command:
1. Reads your `Package.appxmanifest` to get app details and capabilities
2. Registers `electron.exe` in your `node_modules` with a temporary identity
3. Enables you to test identity-required APIs without creating a full MSIX package

The debug identity was applied automatically when you ran `npm install` in Step 4. Going forward, it will be reapplied whenever anyone runs `npm install`.

### When to Manually Update Debug Identity

You need to run this command manually whenever you modify `Package.appxmanifest` (change capabilities, identity, or properties) or any of the linked assets (icons, mcp.json, etc)

```bash
npx winapp node add-electron-debug-identity
```

### Testing Your Setup

You can now test your Electron app with the debug identity applied:

```bash
npm start
```

You should see a **desktop application window** open (not a browser tab) — this is how Electron apps run.

<details>
<summary><b>⚠️ Known Issue: App Crashes or Blank Window (click to expand)</b></summary>

There is a known Windows bug with sparse packaging Electron applications which causes the app to crash on start or not render web content. The issue has been fixed in Windows but has not yet propagated to all devices.

**Symptoms:**
- App crashes immediately after launch
- Window opens but shows blank/white screen
- Web content fails to render

**Workaround:**

Add the `--no-sandbox` flag to your start script in `package.json`. This works around the issue by disabling Chromium's sandbox, which is safe for development purposes.

```json
{
  "scripts": {
    "start": "electron-forge start -- --no-sandbox"
  }
}
```

**Important:** This issue does **not** affect full MSIX packaging - only debug identity during development.

**To undo debug identity** (if needed for troubleshooting):
```bash
npx winapp node clear-electron-debug-identity
```

This restores the original Electron executable without the debug identity.

</details>

## Next Steps

Now that your development environment is set up, you're ready to call Windows APIs from JavaScript or create native addons when you need native code:

- **[Calling Windows APIs from JavaScript](js-file-picker.md)** - Use JS bindings to call Windows App SDK APIs, and extend them with Windows SDK APIs via `winapp.jsBindings`
- **[Showing a Notification from JavaScript](js-notification.md)** - Use JS bindings to show a Windows App SDK notification without a native addon
- **[Calling Phi Silica from JavaScript](js-phi-silica.md)** - Use JS bindings to summarize text with the local language model
- **[Running WinML from JavaScript](js-winml.md)** - Use JS bindings with `onnxruntime-node` for ONNX inference
- **[Creating a Phi Silica Addon](phi-silica-addon.md)** - Learn how to create a C# addon that calls the Phi Silica AI API
- **[Creating a WinML Addon](winml-addon.md)** - Learn how to create a C# addon that uses Windows Machine Learning
- **[Packaging for Distribution](packaging.md)** - Create an MSIX package for distribution

Or return to the **[Getting Started Overview](index.md)**.
