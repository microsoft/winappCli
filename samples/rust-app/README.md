# Rust Windows App Sample

This sample demonstrates how to check for package identity and send Windows notifications from a Rust application.

For a complete step-by-step guide, see the [Rust Getting Started Guide](../../docs/guides/rust.md).

## What This Sample Shows

- Basic Rust console application
- Using Windows Runtime APIs to retrieve package identity
- Using `winapp run` to run the app packaged (registers a loose layout package, just like a real MSIX install)
- Sending Windows notifications with package identity
- MSIX packaging with app manifest and assets

## Prerequisites

- Rust toolchain ([rustup](https://rustup.rs/) or `winget install Rustlang.Rustup --source winget`)
- winapp CLI (`winget install microsoft.winappcli --source winget`)

## How to Run

### 1. Run without Identity
To run the application as a standard executable without package identity:

1. Build the project:
   ```powershell
   cargo build
   ```
2. Run the executable:
   ```powershell
   .\target\debug\rust-app.exe
   ```
   *Output should be: "Not packaged"*

### 2. Run with Identity (Debug)
To run the application packaged with `winapp run`:

1. Build the project:
   ```powershell
   cargo build
   ```
2. Run packaged using `winapp run`:
   ```powershell
   winapp run .\target\debug
   ```
   This registers a loose layout package (just like a real MSIX install), then launches the app via its execution alias so console output stays in the current terminal.

   *Output should show the Package Family Name and trigger a notification.*

> **Note:** A console app is launched through an execution alias automatically, so `--with-alias` is only needed to force one for a windowed app. This sample's `appxmanifest.xml` declares its own `uap5:ExecutionAlias`, which winapp uses as-is; otherwise winapp stages one into the AppX layout for you. Add one to an appxmanifest.xml with `winapp manifest add-alias`.

### 3. Package and Run (MSIX)
To fully package the application as an MSIX and install it:

1. **Build for Release**:
   ```powershell
   cargo build --release
   ```

2. **Prepare Packaging Directory**:
   ```powershell
   mkdir msix
   copy .\target\release\rust-app.exe .\msix\
   ```
   *(Note: Copy the exe and any other needed dependencies to this `msix` folder)*

3. **Generate a Development Certificate** (first time only):
   ```powershell
   winapp cert generate --if-exists skip
   ```

4. **Pack the Application**:
   ```powershell
   winapp pack .\msix --cert .\devcert.pfx
   ```

5. **Install the Certificate** (first time only, requires Admin):
   ```powershell
   winapp cert install .\devcert.pfx
   ```

6. **Install and Run**:
   *   Double-click the generated `.msix` file to install.
   *   Once installed, you can run it from the Start menu or by typing `winapp-rust-sample.exe` in your terminal.
