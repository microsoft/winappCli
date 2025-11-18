# Getting Started: Adding Windows APIs to Your Electron App

This guide walks you through adding Windows-native capabilities to an Electron application using the Windows App Development CLI. You'll learn how to call modern Windows APIs from your Electron app, test with app identity, and package for distribution.

## What You'll Build

By the end of this guide, you'll have an Electron app that:
- ✅ Calls modern Windows APIs (Windows SDK and Windows App SDK)
- ✅ Uses a C# native addon with AI capabilities (Phi Silica)
- ✅ Runs with app identity for testing protected APIs
- ✅ Packages as a signed MSIX for distribution

## Prerequisites

Before you begin, ensure you have:

- **Copilot+ PC / Windows 11** 
- **Node.js** - `winget install OpenJS.NodeJS`
- **.NET SDK v10** - `Microsoft.DotNet.SDK.10`
- **Visual Studio with the Native Desktop Workload** - `winget install --id Microsoft.VisualStudio.Community --source winget --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive --wait"`

> [!NOTE]
> Phi Silica requires a Copilot+ PC to run and that is why it is a requirement for this guide. If you are not on a Copilot+ PC, you can still use this guide for any Windows API.

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

Download the latest npm package (.tgz) from the [WinAppCLI releases page](https://github.com/microsoft/WinAppCli/releases/latest) and install it manually:

```bash
# Download the latest .tgz file from releases, then install it
npm install --save-dev ./path/to/microsoft-winappcli-[version].tgz
```

> **Note:** We are working to publish the package to npm. Once published, you'll be able to install it directly with `npm install --save-dev @microsoft/winappcli`.

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
3. **`winapp node add-electron-debug-identity`** - Registers your Electron app with debug identity (more on this below)

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

## Step 5: Create a C# Native Addon

Now for the exciting part - let's create a native addon that calls Windows APIs! We'll use a C# template that leverages [node-api-dotnet](https://github.com/microsoft/node-api-dotnet) to bridge JavaScript and C#.

```bash
npx winapp node create-addon --template cs
```

This creates a `csAddon/` folder with:
- `addon.cs` - Your C# code that will call Windows APIs
- `csAddon.csproj` - Project file with references to Windows SDK and Windows App SDK
- `README.md` - Documentation on how to use the addon

The command also adds a `build-csAddon` script to your `package.json` for building the addon:
```json
{
  "scripts": {
    "build-csAddon": "dotnet publish ./csAddon/csAddon.csproj -c Release"
  }
}
```

The template automatically includes references to both SDKs, so you can immediately start calling Windows APIs!

Let's verify everything is set up correctly by building the addon:

```bash
# Ensure SDKs are restored
npx winapp restore

# Build the C# addon
npm run build-csAddon
```

> **Note:** You can also create a C++ addon using `npx winapp node create-addon` (without the `--template` flag). C++ addons use [node-addon-api](https://github.com/nodejs/node-addon-api) and provide direct access to Windows APIs with maximum performance. See the [full command documentation](usage.md#node-create-addon) for more options.

## Step 6: Add AI Capabilities with Phi Silica

Let's add a real Windows App SDK API - we'll use the **Phi Silica** AI model to summarize text directly on-device. Phi Silica is a small language model that runs locally on Windows 11 devices with NPUs (Neural Processing Units).

Open `csAddon/addon.cs` and add this code:

```csharp
using System;
using System.Threading.Tasks;
using Microsoft.JavaScript.NodeApi;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;

namespace csAddon
{
    [JSExport]
    public class Addon
    {
        /// <summary>
        /// Summarizes the provided text using the Phi Silica AI model.
        /// </summary>
        /// <param name="text">The text to summarize</param>
        /// <returns>A summary of the input text</returns>
        [JSExport]
        public static async Task<string> SummarizeText(string text)
        {
            try
            {
                var readyState = LanguageModel.GetReadyState();
                if (readyState is AIFeatureReadyState.Ready or AIFeatureReadyState.NotReady)
                {
                    if (readyState == AIFeatureReadyState.NotReady)
                    {
                        await LanguageModel.EnsureReadyAsync();
                    }

                    using LanguageModel languageModel = await LanguageModel.CreateAsync();
                    TextSummarizer textSummarizer = new TextSummarizer(languageModel);

                    var result = await textSummarizer.SummarizeParagraphAsync(text);

                    return result.Text;
                }

                return "Model is not available";
            }
            catch (Exception ex)
            {
                return $"Error calling Phi Silica API: {ex.Message}";
            }
        }
    }
}
```

> **📝 Note:** Phi Silica requires Windows 11 with an NPU-equipped device (Copilot+ PC). If you don't have compatible hardware, the API will return a message indicating the model is not available. You can still complete this tutorial and package the app - it will gracefully handle devices without NPU support.

## Step 7: Build the C# Addon

Now build the addon again:

```bash
npm run build-addon
```

This compiles your C# code using **Native AOT** (Ahead-of-Time compilation), which:
- Creates a `.node` binary (native addon format)
- Trims unused code for smaller bundle size
- Requires **no .NET runtime** on target machines
- Provides native performance

The compiled addon will be in `csAddon/bin/Release/net10.0/win-<arch>/publish/csAddon.node` .

## Step 8: Test the Windows API

Now let's verify the addon works by calling it from the main process. Open `src/index.js` and follow these steps:

### 8.1. Load the C# Addon

Add this with your other `require` statements at the top of the file:

```javascript
const csAddon = require('../csAddon/dist/csAddon.node');
```

### 8.2. Create a Test Function

Add this function somewhere in your file (after the require statements):

```javascript
const callPhiSilica = async () => {
  console.log('Summarizing with Phi Silica: ')
  const result = await csAddon.Addon.summarizeText("The Windows App Development CLI is a powerful tool that bridges cross-platform development with Windows-native capabilities.");
  console.log('Summary:', result);
};
```

### 8.3. Call the Function

Add this line at the end of the `createWindow()` function to test the API when the app starts:

```javascript
callPhiSilica();
```

When you run the app, the summary will be printed to the console. From here, you can integrate the addon into your app however you'd like - whether that's exposing it through a preload script to the renderer process, calling it from IPC handlers, or using it directly in the main process.

## Step 9: Add Required Capability

Before you can use the Phi Silica API, you need to declare the required capability in your app manifest. Open `appxmanifest.xml` and add the `systemAIModels` capability inside the `<Capabilities>` section:

```xml
<Capabilities>
  <rescap:Capability Name="runFullTrust" />
  <rescap:Capability Name="systemAIModels" />
</Capabilities>
```

> **💡 Tip:** Different Windows APIs require different capabilities. Always check the API documentation to see what capabilities are needed. Common ones include `microphone`, `webcam`, `location`, and `bluetooth`.

## Step 10: Update Debug Identity

Whenever you modify `appxmanifest.xml` or change assets referenced in the manifest (like app icons), you need to update your app's debug identity. Run:

```bash
npx winapp node add-electron-debug-identity
```

This command:
1. Reads your `appxmanifest.xml` to get app details and capabilities
2. Registers `electron.exe` in your `node_modules` with a temporary identity
3. Enables you to test identity-required APIs without full MSIX packaging

> **📝 Note:** This command is already part of the `postinstall` script we added in Step 4, so it runs automatically after `npm install`. However, you need to run it manually whenever you:
> - Modify `appxmanifest.xml` (change capabilities, identity, or properties)
> - Update app assets (icons, logos, etc.)
> - Reinstall or update dependencies

Now run your app:

```bash
npm start
```

Check the console output - you should see the Phi Silica summary printed!

<details>
<summary><b>⚠️ Known Issue: App Crashes or Blank Window (click to expand)</b></summary>

There is a known Windows bug with sparse packaging Electron applications which causes the app to crash on start or not render web content. The issue has been fixed in Windows but has not yet propagated to all devices.

**Symptoms:**
- App crashes immediately after launch
- Window opens but shows blank/white screen
- Web content fails to render

**Workaround:**

The `--no-sandbox` flag in the start script above works around this issue by disabling Chromium's sandbox. This is safe for development purposes.

```json
{
  "scripts": {
    "start": "electron-forge start -- --no-sandbox"
  }
}
```

**Important:** This issue does **not** affect full MSIX packaging - only debug identity during development.

**To undo debug identity** (if needed for troubleshooting): Delete your `node_modules` folder and run `npm install` again. The `postinstall` script will reapply it when you're ready.

</details>

## Step 12: Package Your App for Distribution

Now we're ready to create an MSIX package for distribution! 

> **📝 Note:** Before packaging, make sure to configure your build tool (Electron Forge, webpack, etc.) to exclude temporary files from the final build:
> - `.winapp/` folder
> - `winapp.yaml`
> - Certificate files (`.pfx`)
> - Debug symbols (`.pdb`)
> - C# build artifacts (`obj/`, `bin/` folders)
> 
> Also ensure you include required assets like `appxmanifest.xml` and the `Assets/` folder.

> **⚠️ Important:** Verify that your `appxmanifest.xml` matches your packaged app structure:
> - The `Executable` attribute should point to the correct .exe file in your packaged output


### Build Your Electron App

First, build your C# addon and package your Electron app:

```bash
# Build the C# addon in Release mode
npm run build-csAddon

# Package with Electron Forge (or your preferred packager)
npx electron-forge package
```

This will create a packaged version of your app in the `./out/` folder. The exact folder name will depend on your app name and architecture (e.g., `my-windows-app-win32-x64`).

### Create the MSIX Package

Now use WinAppCLI to create and sign an MSIX package from your packaged app:

```bash
npx winapp pack ./out/<your-app-folder> --output ./out --cert ./devcert.pfx --manifest appxmanifest.xml
```

Replace `<your-app-folder>` with the actual folder name created by Electron Forge (e.g., `my-windows-app-win32-x64` for x64 or `my-windows-app-win32-arm64` for ARM64).

**What this command does:**
- `pack` - Creates an MSIX package from the specified directory
- `--output ./out` - Saves the MSIX file to the `./out` folder
- `--cert ./devcert.pfx` - Signs the package with your development certificate
- `--manifest appxmanifest.xml` - Copies the appxmanifest.xml and all referenced assets to the final folder for packaging

The MSIX package will be created as `./out/<your-app-name>.msix`.

> **💡 Tip:** You can add these commands to your `package.json` scripts for convenience:
> ```json
> {
>   "scripts": {
>     "package-msix": "npm run build-csAddon && npx electron-forge package && npx winapp pack ./out/my-windows-app-win32-x64 --output ./out --cert ./devcert.pfx --manifest appxmanifest.xml"
>   }
> }
> ```
> Just make sure to update the path to match your actual output folder name.

### Install and Test the MSIX

First, install the development certificate (one-time setup):

```bash
# Run as Administrator:
npx winapp cert install .\devcert.pfx
```

Now install the MSIX package. Double click the msix file or run the following command:

```bash
Add-AppxPackage .\out\my-windows-app.msix
```

Your app will appear in the Start Menu! Launch it and test the AI summarization feature.

## Next Steps

Congratulations! You've successfully created a Windows app with Electron that calls native Windows APIs! 🎉

### Explore More Windows APIs

Now that you have the foundation, you can explore other Windows capabilities:

- **[Notifications](https://learn.microsoft.com/windows/apps/design/shell/tiles-and-notifications/adaptive-interactive-toasts)** - Rich toast notifications with buttons and inputs
- **[File Pickers](https://learn.microsoft.com/windows/apps/develop/ui-input/file-pickers)** - Native file and folder selection dialogs
- **[Background Tasks](https://learn.microsoft.com/windows/uwp/launch-resume/support-your-app-with-background-tasks)** - Run code when your app is closed
- **[Windows Hello](https://learn.microsoft.com/windows/security/identity-protection/hello-for-business/)** - Biometric authentication
- **[Nearby Sharing](https://learn.microsoft.com/windows/apps/windows-app-sdk/api/winrt/microsoft.windows.applicationmodel.datatransfer.sharepicker)** - Share content with nearby devices

### Additional Resources

- **[WinAppCLI Documentation](./usage.md)** - Full CLI reference
- **[Sample Electron App](../samples/electron/)** - Complete working example
- **[AI Dev Gallery](https://aka.ms/aidevgallery)** - Sample gallery of all AI APIs 
- **[Windows App SDK Samples](https://github.com/microsoft/WindowsAppSDK-Samples)** - Collection of Windows App SDK samples
- **[node-api-dotnet](https://github.com/microsoft/node-api-dotnet)** - C# ↔ JavaScript interop library

### Get Help

- **Found a bug?** [File an issue](https://github.com/microsoft/WinAppCli/issues)

Happy coding! 🚀
