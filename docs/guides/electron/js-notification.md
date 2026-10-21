<!-- mslearn: true -->
<!-- description: Show native Windows toast notifications directly from your Electron app JavaScript using winapp JS bindings, with no native addon required. -->
# Show a Notification from JavaScript (JS bindings)

This guide shows how to show a Windows App SDK app notification directly from Electron main process JavaScript by using generated JS bindings.

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md).

## Step 1: Show a notification from the Electron main process

Import the generated bindings through the `#winapp/bindings` package import, build an app notification, and show it with the default notification manager:

> [!IMPORTANT]
> **Requires `@microsoft/dynwinrt-codegen` ≥ `0.1.0-preview.8`.** Older projects can either upgrade with `npm i -D @microsoft/dynwinrt-codegen@latest && npx winapp init --add-js-bindings` to have `winapp init` wire the `#winapp/bindings` imports map, or keep the relative form `require('../.winapp/bindings/index.js')` (path is relative to `src/index.js`; adjust it if your entry file lives elsewhere).

```js
// src/index.js (Electron main, CommonJS)
const {
  AppNotificationBuilder,
  AppNotificationManager,
} = require('#winapp/bindings');

function showNotification(title, message) {
  const notification = AppNotificationBuilder
    .create()
    .addText(title)
    .addText(message)
    .buildNotification();

  AppNotificationManager.default_.show(notification);
}

const createWindow = () => {
  // ... existing window creation code ...

  // Test the Windows App SDK notification
  showNotification(
    'Hello from Electron!',
    'This notification is powered by the Windows App SDK!'
  );
};
```

> [!NOTE]
> `AppNotificationManager.default_` maps the Windows App SDK `Default` property. The generated bindings add the trailing `_` because `default` is a JavaScript keyword.

## Step 2: Run it

Before notifications will work, make sure your app runs with identity:

```bash
npx winapp node add-electron-debug-identity
```

> [!NOTE]
> This command is already part of the `postinstall` script added in the setup guide, so it runs automatically after `npm install`. Run it manually whenever you modify `Package.appxmanifest`, update app assets, or reinstall dependencies.

Now start the app:

```bash
npm start
```

The Windows App SDK notification appears when the main process calls `showNotification`.

## Next Steps

Congratulations! You're now showing Windows App SDK notifications from your Electron app — no native addon, no `node-gyp` build step. 🎉

Now you're ready to:
- **[Package Your App for Distribution](packaging.md)** — produce an MSIX you can ship (the `@microsoft/dynwinrt` runtime is already in your `dependencies`).

Or explore other guides:
- **[Call Windows APIs from JavaScript](js-file-picker.md)** — pick a file and read its image dimensions using JS bindings.
- **[Call Phi Silica from JavaScript](js-phi-silica.md)** — summarize text with Windows App SDK AI through JS bindings.
- **[Run WinML from JavaScript](js-winml.md)** — use Windows App SDK ML provider discovery with `onnxruntime-node`.
- **[Creating a C++ Native Addon](cpp-notification-addon.md)** — native C++ addon counterpart for notifications.
- **[Getting Started Overview](index.md)** — return to the main guide.

### Additional Resources

- **[winapp CLI Documentation](../../usage.md)** — full CLI reference (`init`, `restore`, `node generate-bindings`).
- **[Sample Electron App](../../../samples/electron/)** — complete working example, including JS bindings.
- **[@microsoft/dynwinrt](https://github.com/microsoft/dynwinrt)** — the runtime that powers the generated bindings.
- **[@microsoft/dynwinrt-codegen](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen)** — the code generator.
