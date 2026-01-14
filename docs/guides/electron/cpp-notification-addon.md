# Creating a C++ Native Addon with Notifications

This guide shows you how to create a C++ native addon that calls the Windows App SDK notification APIs in your Electron app. This is a great starting point for understanding native addons before diving into more complex scenarios.

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md)

## Step 1: Create a C++ Native Addon

Let's create a native addon using C++ and [node-addon-api](https://github.com/nodejs/node-addon-api). This provides direct access to Windows APIs with maximum performance.

```bash
npx winapp node create-addon
```

> [!NOTE]
> This command might prompt your to install python of Visual Studio build tools if you don't already have them installed.

This creates a `nativeWindowsAddon/` folder with:
- `nativeWindowsAddon.cc` - Your C++ code that will call Windows APIs
- `binding.gyp` - Build configuration for node-gyp

The command also installs required dev dependencies (`nan`, `node-addon-api`, `node-gyp`) and adds a `build-nativeWindowsAddon` script to your `package.json`:
```json
{
  "scripts": {
    "build-nativeWindowsAddon": "node-gyp clean configure build --directory=nativeWindowsAddon"
  }
}
```

The generated template includes a sample `ShowNotification` function that uses the Windows SDK notification APIs. Let's verify everything is set up correctly by building the addon:

```bash
# Build the C++ addon
npm run build-nativeWindowsAddon
```

> [!NOTE]
> You can also create a C# addon using `npx winapp node create-addon --template cs`. C# addons use [node-api-dotnet](https://github.com/microsoft/node-api-dotnet). See the other guides for creating addons or the [full command documentation](../../usage.md#node-create-addon) for more options.

## Step 2: Update to Windows App SDK Notifications

The generated code uses the older Windows SDK notification APIs (`Windows.UI.Notifications`). Let's update it to use the modern **Windows App SDK** notification APIs (`Microsoft.Windows.AppNotifications`), which provide a better developer experience and more features.

Open `nativeWindowsAddon/nativeWindowsAddon.cc` and replace the entire contents with this code:

```cpp
#include <napi.h>
#include <windows.h>

#include <winrt/Windows.Foundation.h>
#include <winrt/Microsoft.Windows.AppNotifications.h>
#include <winrt/Microsoft.Windows.AppNotifications.Builder.h>

using namespace winrt;
using namespace Microsoft::Windows::AppNotifications;
using namespace Microsoft::Windows::AppNotifications::Builder;

// Function to display a Windows App SDK notification
void ShowNotification(const Napi::CallbackInfo& info) {
    Napi::Env env = info.Env();

    try {
        // Get arguments from JavaScript (title and message)
        if (info.Length() < 2 || !info[0].IsString() || !info[1].IsString()) {
            Napi::TypeError::New(env, "Expected two string arguments: title and message").ThrowAsJavaScriptException();
            return;
        }

        std::string title = info[0].As<Napi::String>();
        std::string message = info[1].As<Napi::String>();

        // Convert to wide strings
        std::wstring wTitle(title.begin(), title.end());
        std::wstring wMessage(message.begin(), message.end());

        // Use AppNotificationBuilder for a cleaner API
        AppNotificationBuilder builder;
        builder.AddText(wTitle);
        builder.AddText(wMessage);
        
        AppNotification notification = builder.BuildNotification();
        AppNotificationManager::Default().Show(notification);

    } catch (const winrt::hresult_error& ex) {
        Napi::Error::New(env, winrt::to_string(ex.message())).ThrowAsJavaScriptException();
    } catch (const std::exception& ex) {
        // Handle exceptions and throw back to JavaScript
        Napi::Error::New(env, ex.what()).ThrowAsJavaScriptException();
    } catch (...) {
        Napi::Error::New(env, "Unknown error occurred").ThrowAsJavaScriptException();
    }
}

// Initialize the module
Napi::Object Init(Napi::Env env, Napi::Object exports) {
    exports.Set(Napi::String::New(env, "showNotification"), Napi::Function::New(env, ShowNotification));
    return exports;
}

NODE_API_MODULE(addon, Init)
```

The key changes here are switching from the older `Windows.UI.Notifications` namespace to the modern `Microsoft.Windows.AppNotifications` APIs, and using `AppNotificationBuilder` to construct notifications instead of manually building XML strings. This provides a cleaner, more maintainable API that's consistent with the Windows App SDK patterns.

## Step 3: Build the C++ Addon

Now build the addon with the updated code:

```bash
npm run build-nativeWindowsAddon
```

The compiled addon will be in `nativeWindowsAddon/build/Release/nativeWindowsAddon.node`.

## Step 4: Test the Notification API

Now let's verify the addon works by calling it from the main process. Open `src/index.js`:

1. Add the addon import with your other `require` statements at the top:

```javascript
const nativeWindowsAddon = require('../nativeWindowsAddon/build/Release/nativeWindowsAddon.node');
```

2. Call the notification function at the end of the `createWindow()` function:

```javascript
const createWindow = () => {
  // ... existing window creation code ...

  // Test the Windows App SDK notification
  nativeWindowsAddon.showNotification(
    'Hello from Electron!',
    'This notification is powered by the Windows App SDK!'
  );
};
```

When you run the app, a Windows notification will appear. From here, you can integrate the addon into your app however you'd like - whether that's exposing it through a preload script to the renderer process, calling it from IPC handlers, or using it directly in the main process.

## Step 5: Update Debug Identity

Before the notification API will work, you need to ensure your app runs with identity. Run:

```bash
npx winapp node add-electron-debug-identity
```

This command:
1. Reads your `appxmanifest.xml` to get app details and capabilities
2. Registers `electron.exe` in your `node_modules` with a temporary identity
3. Enables you to test identity-required APIs without full MSIX packaging

> **Note:** This command is already part of the `postinstall` script we added in the setup guide, so it runs automatically after `npm install`. However, you need to run it manually whenever you:
> - Modify `appxmanifest.xml` (change capabilities, identity, or properties)
> - Update app assets (icons, logos, etc.)
> - Reinstall or update dependencies

Now run your app:

```bash
npm start
```

You should see a notification appear in the Windows notification center! 🎉

<details>
<summary><b>⚠️ Known Issue: App Crashes or Blank Window (click to expand)</b></summary>

There is a known Windows bug with sparse packaging Electron applications which causes the app to crash on start or not render web content. The issue has been fixed in Windows but has not yet propagated to all devices.

See [development environment setup](setup.md) for workaround.
</details>

## Next Steps

Congratulations! You've successfully created a native C++ addon that calls Windows App SDK APIs! 🎉

Now you're ready to:
- **[Package Your App for Distribution](packaging.md)** - Create an MSIX package that you can distribute

Or explore other guides:
- **[Creating a Phi Silica Addon](phi-silica-addon.md)** - Learn how to use Windows AI APIs with C#
- **[Creating a WinML Addon](winml-addon.md)** - Learn how to use Windows Machine Learning
- **[Getting Started Overview](../../electron-get-started.md)** - Return to the main guide

### Additional Resources

- **[winapp CLI Documentation](../../usage.md)** - Full CLI reference
- **[Sample Electron App](../../../samples/electron/)** - Complete working example with C++ addon
- **[node-addon-api](https://github.com/nodejs/node-addon-api)** - C++ ↔ JavaScript interop library
- **[Windows App SDK Samples](https://github.com/microsoft/WindowsAppSDK-Samples)** - Collection of Windows App SDK samples
