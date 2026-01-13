# Packaging Your Electron App for Distribution

This guide shows you how to create an MSIX package for distributing your Electron app with Windows APIs.

## Prerequisites

Before packaging, make sure you've:
- Completed the [development environment setup](setup.md)
- [OPTIONAL] Created and tested your addon (e.g., [Phi Silica addon](phi-silica-addon.md) or [WinML addon](winml-addon.md))
- Verified your app runs correctly with `npm start`

## Prepare for Packaging

> **📝 Note:** Before packaging, make sure to configure your build tool (Electron Forge, webpack, etc.) to exclude temporary files from the final build:
> - `.winapp/` folder
> - `winapp.yaml`
> - Certificate files (`.pfx`)
> - Debug symbols (`.pdb`)
> - C# build artifacts (`obj/`, `bin/` folders)
> - MSIX packages (*.msix)
> 
> **⚠️ Important:** Verify that your `appxmanifest.xml` matches your packaged app structure:
> - The `Executable` attribute should point to the correct .exe file in your packaged output

## Packaging Options

You have two options for creating an MSIX package for your Electron app:

1. **Option 1: Using winapp CLI directly** - More configurable, works with any packager
2. **Option 2: Using Electron Forge MSIX Maker** - More integrated into the Forge workflow

Choose the option that best fits your workflow.

---

### Option 1: Using winapp CLI directly (Recommended for flexibility)

This approach gives you more control over the packaging process and works with any Electron packager.

#### Build Your Electron App

To package your Electron app with MSIX, we need to first create the production layout. With electron forge, we can use the package command:

```bash
# Package with Electron Forge (or your preferred packager)
npx electron-forge package
```

This will create a production version of your app in the `./out/` folder. The exact folder name will depend on your app name and architecture (e.g., `my-windows-app-win32-x64`).

#### Create the MSIX Package

Now use the winapp CLI to create and sign an MSIX package from your packaged app:

```bash
npx winapp pack .\out\<your-app-folder> --output .\out --cert .\devcert.pfx --manifest .\appxmanifest.xml
```

Replace `<your-app-folder>` with the actual folder name created by Electron Forge (e.g., `my-windows-app-win32-x64` for x64 or `my-windows-app-win32-arm64` for ARM64).

The `--manifest` option is optional. If not provided, it will look for an appxmanifest.xml in the folder being packaged, or in the current directory.

The `--cert` option is also optional. If not provided, the msix will not be signed.

The `--out` option is also optional. If not provided, the current directory will be used.

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

---

### Option 2: Using Electron Forge MSIX Maker (for Forge users)

If you're already using Electron Forge, you can integrate MSIX packaging directly into the Forge workflow using the [`@electron-forge/maker-msix`](https://www.electronforge.io/config/makers/msix) maker.

#### Install the MSIX Maker

```bash
npm install --save-dev @electron-forge/maker-msix
```

#### Configure forge.config.js

Add the MSIX maker to your `forge.config.js`:

```javascript
module.exports = {
  // ... other config
  makers: [
    {
      name: '@electron-forge/maker-msix',
      config: {
        appManifest: '.\\appxmanifest.xml',
        windowsSignOptions: {
          certificateFile: '.\\devcert.pfx',
          certificatePassword: 'password'
        }
      }
    }
  ],
  // ... rest of your config
};
```

#### Update appxmanifest.xml

The Electron Forge MSIX maker uses a different folder layout than the winapp CLI approach. Update the `Executable` path in your `appxmanifest.xml` to point to the `app` folder:

```xml
<Applications>
  <Application Id="myApp"
    Executable="app\my-app.exe"
    EntryPoint="Windows.FullTrustApplication">
    <!-- ... rest of your application config -->
  </Application>
</Applications>
```

Replace `my-app.exe` with your actual executable name.

#### Create the MSIX Package

Now you can create the MSIX package with a single command:

```bash
npm run make
```

The MSIX package will be created in the `./out/make/msix/` folder.

> **💡 Tip:** This approach is more integrated with the Electron Forge workflow and automatically handles packaging and MSIX creation in one step.

## Install and Test the MSIX

First, install the development certificate (one-time setup):

```bash
# Run as Administrator:
npx winapp cert install .\devcert.pfx
```

Now install the MSIX package. Double click the msix file or run the following command:

```bash
Add-AppxPackage .\my-windows-app.msix
```

Your app will appear in the Start Menu! Launch it and test your Windows API features.

## Distribution Options

Once you have a working MSIX package, you have several options for distributing your app:

### Direct Download
Host the MSIX package on your website for direct download. Ensure you sign it with a code signing certificate from a trusted certificate authority (CA) so users can install it without security warnings. 

### Microsoft Store
Submit your app to the Microsoft Store for the widest distribution and automatic updates. You'll need to:
1. Create a Microsoft Partner Center account
2. Reserve your app name
3. Update `appxmanifest.xml` with your Store identity. No need to sign the msix, the store publishing process will sign it automaticly. 
5. Submit for certification

Learn more: [Publish your app to the Microsoft Store](https://learn.microsoft.com/windows/apps/publish/)

### Enterprise Distribution
Distribute directly to enterprise customers via:
- **Company Portal** - For organizations using Intune
- **Direct Download** - Host the MSIX on your website
- **Sideloading** - Install via PowerShell or App Installer

Learn more: [Distribute apps outside the Store](https://learn.microsoft.com/windows/msix/desktop/managing-your-msix-deployment-overview)

### App Installer
Create an `.appinstaller` file for automatic updates:

```xml
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller
    Uri="https://your-domain.com/packages/myapp.appinstaller"
    Version="1.0.0.0"
    xmlns="http://schemas.microsoft.com/appx/appinstaller/2017/2">
    <MainPackage
        Name="YourAppName"
        Version="1.0.0.0"
        Publisher="CN=YourPublisher"
        Uri="https://your-domain.com/packages/myapp.msix"
        ProcessorArchitecture="x64" />
    <UpdateSettings>
        <OnLaunch HoursBetweenUpdateChecks="24" />
    </UpdateSettings>
</AppInstaller>
```

Learn more: [App Installer file overview](https://learn.microsoft.com/windows/msix/app-installer/app-installer-file-overview)

## Next Steps

Congratulations! You've successfully packaged your Windows-enabled Electron app for distribution! 🎉

### Additional Resources

- **[winapp CLI Documentation](../../usage.md)** - Full CLI reference
- **[Sample Electron App](../../../samples/electron/)** - Complete working example
- **[MSIX Packaging Documentation](https://learn.microsoft.com/windows/msix/)** - Learn more about MSIX
- **[Windows App Certification Kit](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-app-certification-kit)** - Test your app before Store submission

### Return to Overview

- **[Getting Started Overview](../../electron-get-started.md)** - Return to the main guide
- **[Setting Up Development Environment](setup.md)** - Review setup steps
- **[Creating a Phi Silica Addon](phi-silica-addon.md)** - Review addon creation
- **[Creating a WinML Addon](winml-addon.md)** - Learn about WinML integration

### Get Help

- **Found a bug?** [File an issue](https://github.com/microsoft/WinAppCli/issues)

Happy distributing! 🚀
