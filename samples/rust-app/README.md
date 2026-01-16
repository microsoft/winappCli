# Rust Windows App Sample

This sample demonstrates how to check for package identity and send Windows notifications from a Rust application.

## Dependencies

Before running the sample, ensure you have Rust and the `winappcli` tool installed.

### Install Rust
If you haven't installed Rust yet, you can download it from [rust-lang.org](https://www.rust-lang.org/tools/install) or use winget:

```powershell
winget install Rustlang.Rustup --source winget
```

### Install winappcli
Install the `winapp` command line tool using winget:

```powershell
winget install microsoft.winappcli --source winget
```

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
To run the application with a temporary debug identity:

1. Build the project:
   ```powershell
   cargo build
   ```
2. Apply debug identity to the executable:
   ```powershell
   winapp create-debug-identity .\target\debug\rust-app.exe
   ```
3. Run the executable:
   ```powershell
   .\target\debug\rust-app.exe
   ```
   *Output should show the Package Family Name and trigger a notification.*

### 3. Package and Run (MSIX)
To fully package the application as an MSIX and install it:

1. **Generate a Certificate**: Create a self-signed certificate for signing based on the manifest.
   ```powershell
   winapp cert generate --manifest .\appxmanifest.xml
   ```

2. **Install the Certificate**: Install the certificate locally (requires Admin privileges).
   ```powershell
   winapp cert install .\devcert.pfx
   ```

3. **Build for Release**:
   ```powershell
   cargo build --release
   ```

4. **Prepare Packaging Directory**:
   ```powershell
   mkdir msix
   mv .\target\release\rust-app.exe .\msix\
   ```
   *(Note: Move the exe and any other needed dependencies to this `msix` folder)*

5. **Pack the Application**:
   ```powershell
   winapp pack .\msix --manifest .\appxmanifest.xml --cert .\devcert.pfx
   ```

6. **Install and Run**:
   *   Double-click the generated `.msix` file to install.
   *   Once installed, you can run it from the Start menu or by typing `winapp-rust-sample.exe` in your terminal.
