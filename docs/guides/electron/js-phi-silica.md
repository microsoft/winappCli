<!-- mslearn: true -->
<!-- description: Call the on-device Phi Silica language model from Electron JavaScript using winapp JS bindings, with no native addon or node-gyp build step. -->
# Call Phi Silica from JavaScript (JS bindings)

This guide shows how to call the Windows App SDK Phi Silica text summarization API directly from Electron JavaScript by using generated JS bindings.

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md).
- Tested on a Copilot+ PC. Phi Silica requires hardware that supports the local language model.

## Step 1: Add the AI capability

Phi Silica requires the `systemAIModels` capability. It lives in its **own** `systemai` namespace — not `rescap` — so declare that namespace on `<Package>` and mark it ignorable, then add the capability:

```xml
<Package
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  xmlns:systemai="http://schemas.microsoft.com/appx/manifest/systemai/windows10"
  IgnorableNamespaces="rescap systemai">
  ...
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <systemai:Capability Name="systemAIModels" />
  </Capabilities>
</Package>
```

It also needs `TargetDeviceFamily/@MaxVersionTested` to be at least `10.0.26226.0`.

After changing the manifest, refresh debug identity:

```bash
npx winapp node add-electron-debug-identity
```

## Step 2: Call Phi Silica from the Electron main process

Import the generated bindings through `#winapp/bindings`, create a `TextSummarizer`, and call it at the end of `createWindow()` to verify everything works:

> **Requires `@microsoft/dynwinrt-codegen` ≥ `0.1.0-preview.8`** — see [Get started with Electron](index.md#2-call-windows-apis-from-javascript) for older-project fallbacks.

```js
// src/index.js (Electron main, CommonJS)
const {
  AIFeatureReadyState,
  LanguageModel,
  TextSummarizer,
} = require('#winapp/bindings');

const callPhiSilica = async () => {
  console.log('Summarizing with Phi Silica:');

  const readyState = LanguageModel.getReadyState();
  if (readyState !== AIFeatureReadyState.Ready && readyState !== AIFeatureReadyState.NotReady) {
    console.log('Summary: Model is not available');
    return;
  }
  if (readyState === AIFeatureReadyState.NotReady) {
    await LanguageModel.ensureReadyAsync();
  }

  const languageModel = await LanguageModel.createAsync();
  try {
    const summarizer = TextSummarizer.createInstance(languageModel);
    const result = await summarizer.summarizeParagraphAsync(
      "The Windows App Development CLI lets Electron apps call Windows-native APIs from JavaScript. Package identity enables features such as notifications and background tasks."
    );
    console.log('Summary:', result.text);
  } finally {
    languageModel.close();
  }
};

const createWindow = () => {
  // ... existing window creation code ...

  // Test the Phi Silica AI API
  callPhiSilica();
};
```

> [!NOTE]
> `getReadyState()` returns one of `Ready`, `NotReady`, `DisabledByUser`, or `NotSupportedOnCurrentSystem`. Calling `ensureReadyAsync()` triggers the system to download/initialize the model if it isn't ready yet.

## Step 3: Run it

```bash
npm start
```

Check the console output — you should see the Phi Silica summary printed!

If the model is unavailable on the device, the function logs `"Summary: Model is not available"`. Make sure you're running on supported hardware (Copilot+ PC) and that the manifest includes `systemAIModels`.

From here, you can integrate the summarizer into your app however you'd like — exposing it through a preload script to the renderer, wiring it to IPC handlers, or calling it directly in the main process.

## Next Steps

Congratulations! You're now calling Phi Silica directly from JavaScript — no C# addon, no `node-gyp` build step. 🎉

Now you're ready to:
- **[Package Your App for Distribution](packaging.md)** — produce an MSIX you can ship (the `@microsoft/dynwinrt` runtime is already in your `dependencies`).

Or explore other guides:
- **[Show a Notification from JavaScript](js-notification.md)** — show a Windows App SDK notification through JS bindings.
- **[Call Windows APIs from JavaScript](js-file-picker.md)** — pick a file and read its image dimensions using JS bindings.
- **[Run WinML from JavaScript](js-winml.md)** — use Windows App SDK ML provider discovery with `onnxruntime-node`.
- **[Creating a Phi Silica Addon](phi-silica-addon.md)** — native C# addon counterpart.
- **[Getting Started Overview](index.md)** — return to the main guide.

### Additional Resources

- **[winapp CLI Documentation](../../usage.md)** — full CLI reference (`init`, `restore`, `node generate-bindings`).
- **[Sample Electron App](../../../samples/electron/)** — complete working example, including JS bindings.
- **[@microsoft/dynwinrt](https://github.com/microsoft/dynwinrt)** — the runtime that powers the generated bindings.
- **[@microsoft/dynwinrt-codegen](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen)** — the code generator.
