# Creating a Phi Silica Addon

This guide shows you how to create a C# native addon that calls the Phi Silica AI API in your Electron app. Phi Silica is a small language model that runs locally on Windows 11 devices with NPUs (Neural Processing Units).

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md)
- **Copilot+ PC** - Phi Silica requires a device with an NPU (Neural Processing Unit)

> [!NOTE]
> If you are not on a Copilot+ PC, you can still follow this guide to learn the addon creation process. The code will gracefully handle devices without NPU support by returning a message indicating the model is not available.

## Step 1: Create a C# Native Addon

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
# Build the C# addon
npm run build-csAddon
```

> **Note:** You can also create a C++ addon using `npx winapp node create-addon` (without the `--template` flag). C++ addons use [node-addon-api](https://github.com/nodejs/node-addon-api) and provide direct access to Windows APIs with maximum performance. See the [full command documentation](../../usage.md#node-create-addon) for more options.

## Step 2: Add AI Capabilities with Phi Silica

Let's add a real Windows App SDK API - we'll use the **Phi Silica** AI model to summarize text directly on-device.

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

## Step 3: Build the C# Addon

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

## Step 4: Test the Windows API

Now let's verify the addon works by calling it from the main process. Open `src/index.js` and follow these steps:

### 4.1. Load the C# Addon

Add this with your other `require` statements at the top of the file:

```javascript
const csAddon = require('../csAddon/dist/csAddon.node');
```

### 4.2. Create a Test Function

Add this function somewhere in your file (after the require statements):

```javascript
const callPhiSilica = async () => {
  console.log('Summarizing with Phi Silica: ')
  const result = await csAddon.Addon.summarizeText("The Windows App Development CLI is a powerful tool that bridges cross-platform development with Windows-native capabilities.");
  console.log('Summary:', result);
};
```

### 4.3. Call the Function

Add this line at the end of the `createWindow()` function to test the API when the app starts:

```javascript
callPhiSilica();
```

When you run the app, the summary will be printed to the console. From here, you can integrate the addon into your app however you'd like - whether that's exposing it through a preload script to the renderer process, calling it from IPC handlers, or using it directly in the main process.

## Step 5: Add Required Capability

Before you can use the Phi Silica API, you need to declare the required capability in your app manifest. Open `appxmanifest.xml` and add the `systemAIModels` capability inside the `<Capabilities>` section:

```xml
<Capabilities>
  <rescap:Capability Name="runFullTrust" />
  <rescap:Capability Name="systemAIModels" />
</Capabilities>
```

> **💡 Tip:** Different Windows APIs require different capabilities. Always check the API documentation to see what capabilities are needed. Common ones include `microphone`, `webcam`, `location`, and `bluetooth`.

## Step 6: Update Debug Identity

Whenever you modify `appxmanifest.xml` or change assets referenced in the manifest (like app icons), you need to update your app's debug identity. Run:

```bash
npx winapp node add-electron-debug-identity
```

This command:
1. Reads your `appxmanifest.xml` to get app details and capabilities
2. Registers `electron.exe` in your `node_modules` with a temporary identity
3. Enables you to test identity-required APIs without full MSIX packaging

> **📝 Note:** This command is already part of the `postinstall` script we added in the setup guide, so it runs automatically after `npm install`. However, you need to run it manually whenever you:
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

See [development environment setup](setup.md) for workaround.
</details>

## Next Steps

Congratulations! You've successfully created a native addon that calls Windows AI APIs! 🎉

Now you're ready to:
- **[Package Your App for Distribution](packaging.md)** - Create an MSIX package that you can distribute

Or explore other guides:
- **[Creating a WinML Addon](winml-addon.md)** - Learn how to use Windows Machine Learning
- **[Getting Started Overview](../../electron-get-started.md)** - Return to the main guide

### Additional Resources

- **[winapp CLI Documentation](../../usage.md)** - Full CLI reference
- **[Sample Electron App](../../../samples/electron/)** - Complete working example
- **[AI Dev Gallery](https://aka.ms/aidevgallery)** - Sample gallery of all AI APIs 
- **[Windows App SDK Samples](https://github.com/microsoft/WindowsAppSDK-Samples)** - Collection of Windows App SDK samples
- **[node-api-dotnet](https://github.com/microsoft/node-api-dotnet)** - C# ↔ JavaScript interop library
