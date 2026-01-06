# Using WinAppCLI with Tauri

This guide demonstrates how to use `winappcli` with a Tauri application to debug with package identity and package your application as an MSIX.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs (like Notifications, Security, AI APIs, etc), have a clean install/uninstall experience, and more.

For a complete working example, check out the [Tauri sample](../../samples/tauri-app) in this repository.

## Prerequisites

1. **Windows 11** 
1. **Node.js** - `winget install OpenJS.NodeJS`
1. **WinAppCLI** - `winget install microsoft.winappcli`

## 1. Create a New Tauri App

Start by creating a new Tauri application using the official scaffolding tool:

```powershell
npm create tauri-app@latest
```
Follow the prompts (e.g., Project name: `tauri-app`, Frontend language: `JavaScript`, Package manager: `npm`).

Navigate to your project directory and install dependencies:

```powershell
cd tauri-app
npm install
```

Run the app to make sure everything is working:

```powershell
npm run tauri dev
```

## 2. Update Code to Check Identity

We'll update the app to check if it's running with package identity. We'll use the `windows` crate in the Rust backend to access Windows APIs and expose it to the frontend.

### Backend Changes (Rust)

1.  **Add Dependency**: Open `src-tauri/Cargo.toml` and add the `windows` dependency for the Windows target:

    ```toml
    [target.'cfg(windows)'.dependencies]
    windows = { version = "0.58", features = ["ApplicationModel"] }
    ```

2.  **Add Command**: Open `src-tauri/src/lib.rs` and add the `get_package_family_name` command. This function attempts to retrieve the current package identity.

    ```rust
    #[tauri::command]
    fn get_package_family_name() -> String {
        #[cfg(target_os = "windows")]
        {
            use windows::ApplicationModel::Package;
            match Package::Current() {
                Ok(package) => {
                    match package.Id() {
                        Ok(id) => match id.FamilyName() {
                            Ok(name) => name.to_string(),
                            Err(_) => "Error retrieving Family Name".to_string(),
                        },
                        Err(_) => "Error retrieving Package ID".to_string(),
                    }
                }
                Err(_) => "No package identity".to_string(),
            }
        }
        #[cfg(not(target_os = "windows"))]
        {
            "Not running on Windows".to_string()
        }
    }
    ```

3.  **Register Command**: In the same file (`src-tauri/src/lib.rs`), update the `run` function to register the new command:

    ```rust
    pub fn run() {
        tauri::Builder::default()
            .plugin(tauri_plugin_opener::init())
            .invoke_handler(tauri::generate_handler![greet, get_package_family_name]) // Add get_package_family_name here
            .run(tauri::generate_context!())
            .expect("error while running tauri application");
    }
    ```

### Frontend Changes (JavaScript)

1.  **Update HTML**: Open `src/index.html` and add a paragraph to display the result:

    ```html
    <!-- ... inside <main> ... -->
    <p id="pfn-msg"></p>
    ```

2.  **Update Logic**: Open `src/main.js` to invoke the command and display the result:

    ```javascript
    const { invoke } = window.__TAURI__.core;

    // ... existing code ...

    async function checkPackageIdentity() {
      const pfn = await invoke("get_package_family_name");
      const pfnMsgEl = document.querySelector("#pfn-msg");
      
      if (pfn !== "No package identity" && !pfn.startsWith("Error")) {
        pfnMsgEl.textContent = `Package family name: ${pfn}`;
      } else {
        pfnMsgEl.textContent = `Not running with package identity`;
      }
    }

    window.addEventListener("DOMContentLoaded", () => {
      // ... existing code ...
      checkPackageIdentity();
    });
    ```

3. Now, run the app as usual:

    ```powershell
    npm run tauri dev
    ```

    You should see "Not running with package identity" in the app window. This confirms that the standard development build is running without package identity.

## 3. Generate App Manifest

To give your application an identity, you need an `appxmanifest.xml`. This file describes your application to Windows. We will generate a default one now, and use it for both debugging and final packaging.

```powershell
winapp manifest generate
```

This creates an `appxmanifest.xml` file and an `Assets` folder in your current directory. You can open `appxmanifest.xml` to customize properties like the display name, publisher, and logo.

## 4. Debug with Identity

To debug with identity, we need to build the Rust backend, apply the debug identity to the executable, and then run it directly. Since `npm run tauri dev` manages the process lifecycle, it's harder to inject the identity there. Instead, we'll create a custom script.

1.  **Add Script**: Open `package.json` and add a new script `tauri:dev:withidentity`:

    ```json
    "scripts": {
      "tauri": "tauri",
      "tauri:dev:withidentity": "cargo build --manifest-path src-tauri/Cargo.toml && winapp create-debug-identity src-tauri/target/debug/tauri-app.exe && .\\src-tauri\\target\\debug\\tauri-app.exe"
    }
    ```

    **What this script does:**
    *   `cargo build ...`: Recompiles the Rust backend.
    *   `winapp create-debug-identity ...`: Applies the temporary identity from your `appxmanifest.xml` to the built executable.
    *   `...tauri-app.exe`: Runs the executable directly.

2.  **Run the Script**:

    ```powershell
    npm run tauri:dev:withidentity
    ```

You should now see the app open and display a "Package family name", confirming it is running with identity! You can now start using and debugging APIs that require package identity, such as Notifications or the new AI APIs like Phi Silica. 

## 5. Package with MSIX

Once you're ready to distribute your app, you can package it as an MSIX which will provide the package identity to your application.

### Build Release
Build your application in release mode:

```powershell
npm run tauri build
```

### Prepare Package Directory
Create a directory to hold your package files and copy your release executable.

```powershell
mkdir dist
copy .\src-tauri\target\release\tauri-app.exe .\dist\
```

### Sign and Pack
If you haven't already, generate and install a self-signed certificate for local testing:

```powershell
# will generate devcert.pfx with publisher details matching the appxmanifest.xml
winapp cert generate --manifest .\appxmanifest.xml

# install certificate locally - run with sudo or as administrator
sudo winapp cert install .\devcert.pfx
```

Now, pack the application:

```powershell
# package and sign the app with the generated certificate
winapp pack .\dist --cert .\devcert.pfx 
```

### Install and Run
Install the package by double-clicking the generated `.msix` file. Once installed, you can launch your app from the start menu.


You should see the app running with identity.
