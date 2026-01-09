# Setting Up the Development Environment

This guide walks you through setting up your Electron development environment for Windows API development. You'll install the necessary tools, initialize your project, and configure Windows SDKs.

## Prerequisites

Before you begin, ensure you have:

- **Windows 11** 
- **Node.js** - `winget install OpenJS.NodeJS`
- **.NET SDK v10** - `winget install Microsoft.DotNet.SDK.10`
- **Visual Studio with the Native Desktop Workload** - `winget install --id Microsoft.VisualStudio.Community --source winget --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive --wait"`

## Step 1: Create a New Electron App

We'll start with a fresh Electron app using Electron Forge, which provides excellent tooling and packaging support. If you are starting from an existing app, you can skip this step.

```bash
npm create electron-app@latest my-windows-app
cd my-windows-app
```

This creates a new Electron app with the following structure:
```
my-windows-app/
├── src/
│   ├── index.js          # Main process
│   ├── index.html        # UI
│   └── preload.js        # Preload script
├── package.json
└── forge.config.js       # Electron Forge configuration
```

Verify the app runs:

```bash
npm start
```

You should see the default Electron Forge window. Close it and let's add Windows capabilities!

## Step 2: Install WinAppCLI

```bash
npm install --save-dev @microsoft/winappcli
```

## Step 3: Initialize the project for Windows development

Now we'll initialize your project with the Windows SDKs and required assets.

```bash
npx winapp init
```

### What Does `winapp init` Do?

This command sets up everything you need for Windows development:

1. **Creates `.winapp/` folder** containing:
   - Headers and libraries from the **Windows SDK**
   - Headers and libraries from the **Windows App SDK**
   - NuGet packages with the required binaries

2. **Generates `appxmanifest.xml`** - The app manifest required for app identity and MSIX packaging

3. **Creates `Assets/` folder** - Contains app icons and visual assets for your app

4. **Generates `devcert.pfx`** - A development certificate for signing packages

5. **Creates `winapp.yaml`** - Tracks SDK versions and project configuration

6. **Installs Windows App SDK runtime** - Required runtime components for modern APIs

7. **Enables Dev Mode in Windows** - Required for debuging our application

The `.winapp/` folder and `devcert.pfx` are automatically added to `.gitignore` since they can be regenerated and should not be checked in to source.

> **💡 About the Windows SDKs:**
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
    "postinstall": "winapp restore && winapp cert generate --if-exists skip && winapp node add-electron-debug-identity"
  }
}
```

This script automatically runs after `npm install` and does three things:

1. **`winapp restore`** - Downloads and restores all Windows SDK packages to the `.winapp/` folder
2. **`winapp cert generate --if-exists skip`** - Generates a development certificate (if one doesn't exist)
3. **`winapp node add-electron-debug-identity`** - Registers your Electron app with debug identity (more on this in the next steps)

Now whenever someone runs `npm install`, the Windows environment is automatically configured!

<details>
<summary><b>💡 Cross-Platform Development (click to expand)</b></summary>

If you're building a cross-platform Electron app and have developers working on macOS or Linux, you'll want to conditionally run the Windows-specific setup. Here's the recommended approach:

Create `scripts/postinstall.js`:
```javascript
if (process.platform === 'win32') {
  const { execSync } = require('child_process');
  try {
    execSync('npx winapp restore && npx winapp cert generate --if-exists skip && npx winapp node add-electron-debug-identity', {
      stdio: 'inherit'
    });
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

The `postinstall` script in Step 4 includes the `winapp node add-electron-debug-identity` command, which enables you to test Windows APIs that require app identity during development.

### What Does Debug Identity Do?

This command:
1. Reads your `appxmanifest.xml` to get app details and capabilities
2. Registers `electron.exe` in your `node_modules` with a temporary identity
3. Enables you to test identity-required APIs without creating a full MSIX package

The debug identity is automatically applied when you run `npm install` thanks to the `postinstall` script.

### When to Manually Update Debug Identity

You need to run this command manually whenever you modify `appxmanifest.xml` (change capabilities, identity, or properties) or any of the linked assets (icons, mcp.json, etc)

```bash
npx winapp node add-electron-debug-identity
```

### Testing Your Setup

You can now test your Electron app with the debug identity applied:

```bash
npm start
```

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
1. Navigate to `node_modules\electron\dist\` in your project
2. Delete `electron.exe`
3. Rename `electron.backup.exe` to `electron.exe`

This restores the original Electron executable without the debug identity.

</details>

## Next Steps

Now that your development environment is set up, you're ready to create native addons and call Windows APIs:

- **[Creating a Phi Silica Addon](phi-silica-addon.md)** - Learn how to create a C# addon that calls the Phi Silica AI API
- **[Creating a WinML Addon](winml-addon.md)** - Learn how to create a C# addon that uses Windows Machine Learning
- **[Packaging for Distribution](packaging.md)** - Create an MSIX package for distribution

Or return to the **[Getting Started Overview](../../electron-get-started.md)**.
