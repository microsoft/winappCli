<!-- mslearn: true -->
<!-- description: Open a native Windows file picker and inspect images from Electron JavaScript using winapp JS bindings, with no native addon or node-gyp build. -->
# Call Windows APIs from JavaScript (JS bindings)

This guide shows you how to call Windows APIs — both Windows App SDK and Windows SDK — directly from your Electron app's JavaScript, with no native addon and no `node-gyp` / MSBuild step. You'll open a native file picker (Windows App SDK), then inspect the picked image with Windows SDK file and imaging APIs added through `winapp.jsBindings`.

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md).

## Step 1: Confirm your bindings

Setup generated a `.winapp/bindings/` directory next to your sources — one `.js` + `.d.ts` pair per emitted Windows App SDK class, plus an `index.js` that re-exports them all:

```
.winapp/bindings/
├── index.js                  # entry — re-exports every emitted class
├── index.d.ts                # TS bundle
├── FileOpenPicker.js         # one pair of files per emitted class
├── FileOpenPicker.d.ts
├── PickerLocationId.js
├── PickerLocationId.d.ts
└── …
```

## Step 2: Add Windows SDK APIs to your bindings

The default bindings cover Windows App SDK APIs only. To open and decode the picked image, we also need two Windows SDK classes:

- `Windows.Storage.StorageFile` — to wrap a file path.
- `Windows.Graphics.Imaging.BitmapDecoder` — to read its dimensions.

Open `package.json` and add an `additionalWinmds` array inside the `winapp.jsBindings` block that `winapp init` created:

```jsonc
// package.json
{
  "winapp": {
    "jsBindings": {
      "additionalWinmds": [
        { "namespace": "Windows.Storage", "classes": ["StorageFile"] },
        { "namespace": "Windows.Graphics.Imaging", "classes": ["BitmapDecoder"] }
      ]
    }
  }
}
```

Then regenerate the bindings:

```bash
npx winapp node generate-bindings
```

`StorageFile.js`, `BitmapDecoder.js`, and the enum files they depend on (`FileAccessMode.js`, `BitmapPixelFormat.js`, …) now appear in `.winapp/bindings/`.

> [!NOTE]
> dynwinrt-codegen automatically pulls in dependent types you need to call these classes (for example `IRandomAccessStream`, returned by `StorageFile.openAsync`), so cherry-picking just the entry-point classes is usually enough.

## Step 3: Call Windows APIs from your Electron code

All generated classes are exported through `#winapp/bindings`:

> [!IMPORTANT]
> **Requires `@microsoft/dynwinrt-codegen` ≥ `0.1.0-preview.8`** — see [Get started with Electron](index.md#2-call-windows-apis-from-javascript) for older-project fallbacks.

```js
// src/index.js (Electron main, CommonJS)
const { app, BrowserWindow, ipcMain } = require('electron');
const {
  // Windows App SDK (default bindings)
  FileOpenPicker,
  PickerLocationId,
  PickerViewMode,
  // Windows SDK (added via additionalWinmds in Step 2)
  StorageFile,
  FileAccessMode,
  BitmapDecoder,
} = require('#winapp/bindings');

async function pickAndInspectImage(mainWindow) {
  // FileOpenPicker needs the parent window's HWND wrapped in a WindowId struct.
  // Electron's getNativeWindowHandle() returns an 8-byte buffer on 64-bit Windows.
  const hwnd = mainWindow.getNativeWindowHandle().readBigUInt64LE(0);

  const picker = FileOpenPicker.createInstance({ value: hwnd });
  picker.viewMode = PickerViewMode.Thumbnail;
  picker.suggestedStartLocation = PickerLocationId.PicturesLibrary;
  picker.fileTypeFilter.replaceAll(['.png', '.jpg', '.jpeg', '.gif']);

  const result = await picker.pickSingleFileAsync();
  if (!result?.path) return null; // User cancelled.

  // Use Windows SDK APIs to inspect the picked image.
  const file = await StorageFile.getFileFromPathAsync(result.path);
  const stream = await file.openAsync(FileAccessMode.Read);
  const decoder = await BitmapDecoder.createAsync(stream);

  return {
    path: result.path,
    width: decoder.pixelWidth,
    height: decoder.pixelHeight,
  };
}

// Expose it to the renderer via IPC so a button click can trigger the flow.
ipcMain.handle('pick-and-inspect-image', (event) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  return pickAndInspectImage(win);
});
```

Then bridge it into the renderer through your preload script:

```js
// src/preload.js
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('winapp', {
  pickAndInspectImage: () => ipcRenderer.invoke('pick-and-inspect-image'),
});
```

Finally, add a button to your renderer and call `window.winapp.pickAndInspectImage()` when it's clicked:

```html
<!-- src/index.html -->
<button id="pick">Pick an image</button>
<p id="result"></p>

<script>
  document.getElementById('pick').addEventListener('click', async () => {
    const info = await window.winapp.pickAndInspectImage();
    document.getElementById('result').textContent = info
      ? `${info.path} (${info.width}×${info.height})`
      : 'Cancelled';
  });
</script>
```

## Step 4: Run it

Before the file picker will work, you need to ensure your app runs with identity. Run:

```bash
npx winapp node add-electron-debug-identity
```

> [!NOTE]
> This command is already part of the `postinstall` script we added in the setup guide, so it runs automatically after `npm install`. However, you need to run it manually whenever you modify `Package.appxmanifest`, update app assets, or reinstall dependencies.

Now start the app:

```bash
npm start
```

Click the button: the native Windows file picker appears, and once you pick an image its path and pixel dimensions show up below the button. 🎉 Importing from `.winapp/bindings/` loads `@microsoft/dynwinrt`, which dispatches each call into the underlying WinRT API — transparent to your code.

## Next Steps

Congratulations! You're now calling Windows APIs — Windows App SDK and Windows SDK — directly from JavaScript, with no native addon and no `node-gyp` build step. 🎉

Now you're ready to:
- **[Package Your App for Distribution](packaging.md)** — produce an MSIX you can ship (the `@microsoft/dynwinrt` runtime is already in your `dependencies`).

Or explore other guides:
- **[Show a Notification from JavaScript](js-notification.md)** — show a Windows App SDK notification through JS bindings.
- **[Call Phi Silica from JavaScript](js-phi-silica.md)** — summarize text with Windows App SDK AI through JS bindings.
- **[Run WinML from JavaScript](js-winml.md)** — use Windows App SDK ML provider discovery with `onnxruntime-node`.
- **[Creating a C++ Native Addon](cpp-notification-addon.md)** — for Win32 / pure-COM APIs that have no WinRT projection.
- **[Creating a Phi Silica Addon](phi-silica-addon.md)** — Windows AI APIs from a C# addon.
- **[Getting Started Overview](index.md)** — return to the main guide.

### Additional Resources

- **[winapp CLI Documentation](../../usage.md)** — full CLI reference (`init`, `restore`, `node generate-bindings`).
- **[Sample Electron App](../../../samples/electron/)** — complete working example, including JS bindings.
- **[@microsoft/dynwinrt](https://github.com/microsoft/dynwinrt)** — the runtime that powers the generated bindings.
- **[@microsoft/dynwinrt-codegen](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen)** — the code generator.
