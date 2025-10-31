# winapp Electron Sample

This sample demonstrates usage of the winapp npm package with an Electron app.

The sample is a default Electron Forge generated application with the following modifications:

1. **Initialized a winapp project** by running `npx winapp init`. This generates:
   - A `.winapp` folder containing headers and libs for the Windows SDK and Windows App SDK
   - An `appxmanifest.xml` with required assets
   - A `devcert.pfx` (dev certificate)
   - Installs the Windows App SDK runtime
   - A `winapp.yaml` file to track NuGet versions and project configuration
   
   The `.winapp` folder and `devcert.pfx` are added to `.gitignore` to ensure they are not committed to git. Running `npx winapp restore` will restore them (this is added as a postinstall script in `package.json`).

2. **Generated a native addon** using `npx winapp node generate-addon` to call APIs from the Windows SDK and Windows App SDK. The addon folder contains the generated addon alongside the `build-addon` script added to `package.json`. The addon contains a function to raise a Windows notification, and the JavaScript code has been modified to call this function.

3. **Modified `forge.config.js`** to ignore the `.winapp`, `devcert.pfx`, and `winapp.yaml` files from the final package, and to copy the `appxmanifest.xml` and `Assets` folder to the final package.

## Prerequisites

Before running the sample, ensure the npm package has been built:

1. Navigate to the root of this repo
2. Run `.\scripts\build-cli.ps1` to build all projects

Then run `npm install` to install all dependencies. The sample has a `postinstall` script that sets up the project with the CLI:

```json
"postinstall": "winapp restore && winapp cert generate && winapp node add-electron-debug-identity"
```

This script runs three winapp commands:

- **`winapp restore`** - Restores all NuGet packages and makes the Windows SDKs available to the app
- **`winapp cert generate`** - Generates a dev certificate for signing the MSIX. The command uses the `appxmanifest.xml` in the root for the publisher name to ensure the package can be signed
- **`winapp node add-electron-debug-identity`** - Adds debug identity to the Electron process so you can debug APIs that require identity

## Testing Debug Identity

The `winapp node add-electron-debug-identity` command registers the `electron.exe` in `node_modules` with a temporary debug identity generated from the `appxmanifest.xml`. When you run `npm install`, this command runs automatically via the postinstall script.

If you modify the `appxmanifest.xml`, or if the postinstall script did not run, re-register the debug identity with:

```bash
npx winapp node add-electron-debug-identity
```

When starting the app with `npm start`, the app will have identity and you can test Windows APIs that require identity.

> **Note:** There is a Windows bug that breaks Electron apps running with sparse packaging (which enables debug identity). The bug has been fixed but has not yet rolled out to most Windows machines. As a workaround, sandboxing is disabled when running with debug identity (the `npm start` script in this sample passes `--no-sandbox`).

## MSIX Packaging

The sample contains an example of packaging and signing an MSIX with `winapp`. The `package-msix` script in `package.json` demonstrates how to package and sign the app:

```json
"package-msix": "npm run build-addon && npm run package & winapp package ./out/sample-electron-app-win32-arm64/ --output-folder ./out --cert ./devcert.pfx"
```

> **Note:** The output folder path is currently hardcoded. You may need to modify this script based on your architecture and output configuration.