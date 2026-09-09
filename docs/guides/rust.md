<!-- mslearn: true -->
<!-- description: Use the winapp CLI with a Rust Windows app to restore Windows App SDK headers, add package identity, and package the desktop app as MSIX. -->
# Using winapp CLI with Rust

This guide demonstrates how to use the `winapp` CLI with a Rust application to debug with package identity and package your application as an MSIX.

For a complete working example, check out the [Rust sample](../../samples/rust-app) in this repository.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs (like Notifications, Security, AI APIs, etc), have a clean install/uninstall experience, and more.

A standard executable (like one created with `cargo build`) does not have package identity. This guide shows how to add it for debugging and then package it for distribution.

## Prerequisites

1.  **Rust Toolchain**: Install Rust using [rustup](https://rustup.rs/) or winget (or update if already installed):
    ```powershell
    winget install Rustlang.Rustup --source winget
    ```

2.  **winapp CLI**: Install the `winapp` tool via winget (or update if already installed):
    ```powershell
    winget install microsoft.winappcli --source winget
    ```

## 1. Create a New Rust App

Start by creating a simple Rust application:

```powershell
cargo new rust-app
cd rust-app
```

Run it to make sure everything is working:

```powershell
cargo run
```
*Output should be "Hello, world!"*

## 2. Update Code to Check Identity

We'll update the app to check if it's running with package identity. This will help us verify that identity is working correctly in later steps. We'll use the `windows` crate to access Windows APIs.

First, add the `windows` dependency to your `Cargo.toml` by running:

```powershell
cargo add windows --features ApplicationModel
```

This adds the Windows API bindings with the `ApplicationModel` feature, which gives us access to the `Package` API for checking identity.

Next, replace the entire contents of `src/main.rs` with the following code. This code attempts to retrieve the current package identity. If it succeeds, it prints the Package Family Name; otherwise, it prints "Not packaged".

> [!NOTE]
> The [full sample](../../samples/rust-app) also includes code to show a Windows Notification if identity is present, but for this guide, we'll focus on the identity check.

```rust
use windows::ApplicationModel::Package;

fn main() {
    match Package::Current() {
        Ok(package) => {
            match package.Id() {
                Ok(id) => match id.FamilyName() {
                    Ok(name) => println!("Package Family Name: {}", name),
                    Err(e) => println!("Error getting family name: {}", e),
                },
                Err(e) => println!("Error getting package ID: {}", e),
            }
        }
        Err(_) => println!("Not packaged"),
    }
}
```

## 3. Run Without Identity

Now, build and run the app as usual:

```powershell
cargo run
```

You should see the output "Not packaged". This confirms that the standard executable is running without any package identity.

## 4. Initialize Project with winapp CLI

The `winapp init` command sets up everything you need in one go: app manifest and assets. The manifest defines your app's identity (name, publisher, version) which Windows uses to grant API access.

Run the following command and follow the prompts:

```powershell
winapp init
```

When prompted:
- **Package name**: Press Enter to accept the default (rust-app)
- **Publisher name**: Press Enter to accept the default or enter your name
- **Version**: Press Enter to accept 1.0.0.0
- **Description**: Press Enter to accept the default or enter a description
- **Setup SDKs**: Select "Do not setup SDKs" (Rust uses its own `windows` crate, not the C++ SDK headers)

This command will:
- Create `Package.appxmanifest` — the manifest that defines your app's identity
- Create `Assets` folder — icons required for MSIX packaging and Store submission

> [!NOTE]
> Because no SDK packages are being managed, no `winapp.yaml` is created — Rust uses the `windows` crate via Cargo, so there's nothing for `winapp restore`/`update` to track.

You can open `Package.appxmanifest` to further customize properties like the display name, publisher, and capabilities.

### Add Execution Alias (for console apps)

Because `cargo new` creates a console app, we need to add an execution alias to the manifest. Without it, `winapp run` launches the app via AUMID activation, which opens a new window — and that window closes immediately when a console app finishes, swallowing any output.

The alias also lets users run your app by name from any terminal after they install the MSIX. The manifest registers an alias like `rust-app.exe` (defaulting to your project's exe name), which users can invoke as `rust-app` or `rust-app.exe`.

> **Skip this step if you're building a UI app** (a Rust app that renders its own window). Those apps work fine with the default AUMID launch.

Add the alias:

```powershell
winapp manifest add-alias
```

This adds a `uap5:ExecutionAlias` entry to `Package.appxmanifest`.

## 5. Debug with Identity

To test features that require identity (like Notifications) without fully packaging the app, use `winapp run`. This registers the entire build output folder as a loose layout package — just like a real MSIX install — and launches the app. No certificate or signing is needed for debugging.

1.  **Build the executable**:
    ```powershell
    cargo build
    ```

2.  **Run with identity**:
    ```powershell
    winapp run .\target\debug
    ```

A console app is launched via its execution alias automatically, so its output stays in the current terminal — winapp detects that from the built binary and uses the `uap5:ExecutionAlias` we added in step 4. Pass `--without-alias` to force AUMID activation instead, in which case the app prints nothing here.

> [!NOTE]
> `winapp run` also registers the package on your system. This is why the MSIX may appear as "already installed" when you try to install it later in step 6. Use `winapp unregister` to clean up development packages when done.

You should now see output similar to:
```
Package Family Name: rust-app_12345abcde
```
This confirms your app is running with a valid package identity!

> [!TIP]
> For advanced debugging workflows (attaching debuggers, IDE setup, startup debugging), see the [Debugging Guide](../debugging.md).

## 6. Package with MSIX

Once you're ready to distribute your app, you can package it as an MSIX using the same manifest. MSIX provides clean install/uninstall, auto-updates, and a trusted installation experience.

### Prepare the Package Directory
First, build your application in release mode for optimal performance:

```powershell
cargo build --release
```

Then, create a directory with just the files needed for distribution. The `target\release` folder contains build artifacts that aren't part of your app — we only need the executable:

```powershell
mkdir dist
copy .\target\release\rust-app.exe .\dist\
```

### Generate a Development Certificate

MSIX packages must be signed. For local testing, generate a self-signed development certificate:

```powershell
winapp cert generate --if-exists skip
```

> [!IMPORTANT]
> The certificate's publisher must match the `Publisher` in your `Package.appxmanifest`. The `cert generate` command reads this automatically from your manifest.

### Sign and Pack

Now you can package and sign in one step:

```powershell
winapp pack .\dist --cert .\devcert.pfx 
```

> Note: The `pack` command automatically uses the Package.appxmanifest from your current directory and copies it to the target folder before packaging. The generated .msix file will be in the current directory.

### Install the Certificate

Before you can install the MSIX package, you need to trust the development certificate on your machine. Run this command as administrator (you only need to do this once per certificate):

```powershell
winapp cert install .\devcert.pfx
```

### Install and Run

> [!NOTE]
> If you used `winapp run` in step 5, the package may already be registered on your system. Use `winapp unregister` first to remove the development registration, then install the release package.

Install the package by double-clicking the generated `.msix` file, or via PowerShell:

```powershell
Add-AppxPackage .\rust-app.msix
```

Now you can run your app from anywhere in the terminal by typing:

```powershell
rust-app
```

You should see the "Package Family Name" output, confirming it's installed and running with identity.

> [!TIP]
> If you need to repackage your app (e.g., after code changes), increment the `Version` in your `Package.appxmanifest` before running `winapp pack` again. Windows requires a higher version number to update an installed package.

## Tips
1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate
2. The Microsoft Store will sign the MSIX for you, no need to sign before submission.
3. You might need to create multiple MSIX packages, one for each architecture you support (x64, Arm64)

## Next Steps

- **Distribute via winget**: Submit your MSIX to the [Windows Package Manager Community Repository](https://github.com/microsoft/winget-pkgs)
- **Publish to the Microsoft Store**: Use `winapp store` to submit your package
- **Set up CI/CD**: Use the [`setup-WinAppCli`](https://github.com/microsoft/setup-WinAppCli) GitHub Action to automate packaging in your pipeline
- **Explore Windows APIs**: With package identity, you can now use [Notifications](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart), [on-device AI](https://learn.microsoft.com/windows/ai/apis/), and other [identity-dependent APIs](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions)