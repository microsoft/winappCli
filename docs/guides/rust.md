# Using WinAppCLI with Rust

This guide demonstrates how to use `winappcli` with a Rust application to debug with package identity and package your application as an MSIX.

For a complete working example, check out the [Rust sample](../../samples/rust-app) in this repository.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs (like Notifications, Security, AI APIs, etc), have a clean install/uninstall experience, and more.

A standard executable (like one created with `cargo build`) does not have package identity. This guide shows how to add it for debugging and then package it for distribution.

## Prerequisites

1.  **Rust Toolchain**: Install Rust using [rustup](https://rustup.rs/) or winget:
    ```powershell
    winget install Rustlang.Rustup
    ```

2.  **WinAppCLI**: Install the `winapp` tool via winget:
    ```powershell
    winget install microsoft.winappcli
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

We'll update the app to check if it's running with package identity. We'll use the `windows` crate to access Windows APIs.

First, add the `windows` dependency to your `Cargo.toml`:

```toml
[dependencies]
windows = { version = "0.58", features = ["ApplicationModel"] }
```

Next, replace the contents of `src/main.rs` with the following code. This code attempts to retrieve the current package identity. If it succeeds, it prints the Package Family Name; otherwise, it prints "Not packaged".

> **Note**: The [full sample](../../samples/rust-app) also includes code to show a Windows Notification if identity is present, but for this guide, we'll focus on the identity check.

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

## 4. Generate App Manifest

To give your application an identity, you need an `appxmanifest.xml`. This file describes your application to Windows. We will generate a default one now, and use it for both debugging and final packaging.

```powershell
winapp manifest generate
```

This creates an `appxmanifest.xml` file and an `Assets` folder in your current directory. You can open `appxmanifest.xml` to customize properties like the display name, publisher, and logo.

## 5. Debug with Identity

To test features that require identity (like Notifications) without fully packaging the app, you can use `winapp create-debug-identity`. This applies a temporary identity to your executable using the manifest we just generated.

1.  **Build the executable**:
    ```powershell
    cargo build
    ```

2.  **Apply Debug Identity**:
    Run the following command on your built executable:
    ```powershell
    winapp create-debug-identity .\target\debug\rust-app.exe
    ```

3.  **Run the Executable**:
    Run the executable directly (do not use `cargo run` as it might rebuild/overwrite the file):
    ```powershell
    .\target\debug\rust-app.exe
    ```

You should now see output similar to:
```
Package Family Name: rust-app_12345abcde
```
This confirms your app is running with a valid package identity!

## 6. Package with MSIX

Once you're ready to distribute your app, you can package it as an MSIX using the same manifest.

### Prepare the Package Directory
First, build your application in release mode for optimal performance:

```powershell
cargo build --release
```

Then, create a directory to hold your package files and copy your release executable.

```powershell
mkdir dist
copy .\target\release\rust-app.exe .\dist\
```

### Add Execution Alias
To allow users to run your app from the command line after installation (like `rust-app`), add an execution alias to the `appxmanifest.xml`.

Open `appxmanifest.xml` and add the `uap5` namespace to the `<Package>` tag if it's missing, and then add the extension inside `<Applications><Application><Extensions>...`:

```diff
<Package
  ...
  xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
+ xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  IgnorableNamespaces="uap uap2 uap3 rescap desktop desktop6 uap10">

  ...
  <Applications>
    <Application ...>
      ...
+     <Extensions>
+       <uap5:Extension Category="windows.appExecutionAlias">
+         <uap5:AppExecutionAlias>
+           <uap5:ExecutionAlias Alias="rust-app.exe" />
+         </uap5:AppExecutionAlias>
+       </uap5:Extension>
+     </Extensions>
    </Application>
  </Applications>
</Package>
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

> Note: The appxmanifest.xml and assets need to be in the target folder for packaging. To simplify, the `pack` command by default uses the appxmanifest.xml in your current directory and copies it to the target folder before packaging.

### Install and Run
Install the package by doubleclicking the generated *.msix file

Now you can run your app from anywhere in the terminal by typing:

```powershell
rust-app
```

You should see the "Package Family Name" output, confirming it's installed and running with identity.

### Tips:
1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate
2. The Microsoft Store will sign the MSIX for you, no need to sign before submission.
3. You might need to create multiple MSIX packages, one for each architecture you support (x64, Arm64)