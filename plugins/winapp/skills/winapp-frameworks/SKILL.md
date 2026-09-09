---
name: winapp-frameworks
description: Framework-specific Windows development guidance for Electron, .NET (WPF, WinForms), C++, Rust, Flutter, and Tauri. Use when packaging or adding Windows features to an Electron app, .NET desktop app, Flutter app, Tauri app, Rust app, or C++ app.
---
## When to use

Use this skill when:
- **Working with a specific app framework** and need to know the right winapp workflow
- **Choosing the correct install method** (npm package vs. standalone CLI)
- **Looking for framework-specific guides** for step-by-step setup, build, and packaging

Each framework has a detailed guide — refer to the links below rather than trying to guess commands.

## Framework guides

| Framework | Install method | Guide |
|-----------|---------------|-------|
| **Electron** | `npm install --save-dev @microsoft/winappcli` | [Electron setup guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/setup.md) |
| **.NET** (WPF, WinForms, Console) | `winget install Microsoft.winappcli` | [.NET guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/dotnet.md) |
| **.NET MAUI** | `winget install Microsoft.winappcli` | [MAUI guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/maui.md) + `winapp-maui` skill |
| **C++** (CMake, MSBuild) | `winget install Microsoft.winappcli` | [C++ guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/cpp.md) |
| **Rust** | `winget install Microsoft.winappcli` | [Rust guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/rust.md) |
| **Flutter** | `winget install Microsoft.winappcli` | [Flutter guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/flutter.md) |
| **Tauri** | `winget install Microsoft.winappcli` | [Tauri guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/tauri.md) |

## Key differences by framework

### Electron (npm package)
Use the **npm package** (`@microsoft/winappcli`), **not** the standalone CLI. The npm package includes:
- The native winapp CLI binary bundled inside `node_modules`
- A Node.js SDK with helpers for creating native C#/C++ addons
- Electron-specific commands under `npx winapp node`
- Generated JS bindings (`.winapp/bindings/`) for direct JavaScript access to Windows App SDK APIs

Quick start:
```powershell
npm install --save-dev @microsoft/winappcli
npx winapp init . --use-defaults --add-js-bindings
npx winapp node create-addon --template cs   # create a C# native addon
npx winapp node add-electron-debug-identity  # register identity for debugging
```

Windows integration guidance:

- Use **JS bindings** to call Windows App SDK APIs directly from JavaScript without native addons (for example AI APIs, notifications, and file pickers). Custom WinRT components with .winmd metadata can also be added via `winapp.jsBindings.additionalWinmds` in package.json.
- Use **native addons** when you need Win32/COM APIs, third-party C++ libraries, or .NET assemblies: `--template cpp` for C++ (node-gyp), or `--template cs` for C#.
- Mixing JS bindings and native addons in one Electron app is fine.

Additional Electron guides:
- [Notification JS bindings guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/js-notification.md)
- [Windows APIs JS bindings guide (file picker + imaging)](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/js-file-picker.md)
- [Phi Silica JS bindings guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/js-phi-silica.md)
- [WinML JS bindings guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/js-winml.md)
- [Packaging guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/packaging.md)
- [C++ notification addon guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/cpp-notification-addon.md)
- [WinML addon guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/winml-addon.md)
- [Phi Silica addon guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/phi-silica-addon.md)

### .NET (WPF, WinForms, Console)
.NET projects have direct access to Windows APIs. Key differences:
- Projects with NuGet references to `Microsoft.Windows.SDK.BuildTools` or `Microsoft.WindowsAppSDK` **don't need `winapp.yaml`** — winapp auto-detects SDK versions from the `.csproj`
- The key prerequisite is `Package.appxmanifest`, not `winapp.yaml`
- No native addon step needed — unlike Electron, .NET can call Windows APIs directly
- `winapp init` automatically adds the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package, enabling `dotnet run` with automatic identity registration
- Arguments written after `dotnet run` go to the launched application, exactly as they would without the package; configure the launcher itself with the `WinAppRun*` MSBuild properties
- Example combining both: `dotnet run -p:WinAppRunDetach=true --app-arg` (WinApp detaches, the app receives `--app-arg`)
- Use `--` when the app's flag is also a `dotnet run` option (`--configuration`, `--framework`, `--project`, `-c`, `-f`, `-r`, ...), e.g. `dotnet run -- --configuration Release`; otherwise the SDK claims it and the app never sees it

**If you already have a `Package.appxmanifest`** (e.g., WinUI 3 apps or projects with an existing packaging setup), you likely **don't need `winapp init`** — your project is already configured for packaged builds. Just make sure:
- Your `.csproj` references the `Microsoft.WindowsAppSDK` NuGet package (WinUI 3 apps already have this)
- The project properties are set up for packaged builds (e.g., `<WindowsPackageType>MSIX</WindowsPackageType>` or equivalent)
- WinUI 3 apps created from Visual Studio templates are typically already fully configured

Quick start:
```powershell
winapp init . --use-defaults
dotnet build <path-to-project.csproj> -c Debug -p:Platform=x64
winapp run bin\x64\Debug\<tfm>\win-x64\
```

Replace `<tfm>` with your target framework (e.g., `net10.0-windows10.0.26100.0`), and adjust `x64` to match your target architecture.

### .NET MAUI
MAUI has one important quirk: its checked-in `Platforms/Windows/Package.appxmanifest` is full of `$placeholder$` tokens that **`winapp package` does not resolve**. MAUI's **resizetizer** fills them at build/publish time into a generated manifest:
- Resizetizer manifest: `obj\<Config>\<TFM>\<RID>\resizetizer\m\Package.appxmanifest`
- Fully-resolved output manifest: `bin\<Config>\<TFM>\<RID>\AppxManifest.xml`

Publish the Windows head first, then point `winapp package --manifest` at the **generated** manifest — never the source one. See the dedicated **`winapp-maui`** skill for the full workflow, CI examples, and troubleshooting.

### C++ (CMake, MSBuild)
C++ projects use winapp primarily for SDK projections (CppWinRT headers) and packaging:
- `winapp init --setup-sdks stable` downloads Windows SDK + App SDK and generates CppWinRT headers
- Headers generated in `.winapp/generated/include`
- Response file at `.cppwinrt.rsp` for build system integration
- Add `.winapp/packages` to include/lib paths in your build system

### Rust
- Use the `windows` crate for Windows API bindings
- winapp handles manifest, identity, packaging, and certificate management
- Typical build output: `target/release/myapp.exe`

### Flutter
- Flutter handles the build (`flutter build windows`)
- winapp handles manifest, identity, packaging
- Build output: `build\windows\x64\runner\Release\`

### Tauri
- Tauri has its own bundler for `.msi` installers
- Use winapp specifically for **MSIX distribution** and package identity features
- winapp adds capabilities beyond what Tauri's built-in bundler provides (identity, sparse packages, Windows API access)

## Debugging by framework

| Framework | Recommended command | Notes |
|-----------|-------------------|-------|
| **.NET** | `winapp run .\bin\x64\Debug\<tfm>\win-x64\` | Build with `dotnet build -c Debug -p:Platform=x64` first; GUI apps launch via AUMID, console apps automatically via an execution alias |
| **C++** | `winapp run .\build\Debug` | Console apps are detected and launched via an execution alias automatically |
| **Rust** | `winapp run .\target\debug` | Console apps are detected and launched via an execution alias automatically |
| **Flutter** | `winapp run .\build\windows\x64\runner\Debug` | GUI app — plain `winapp run` works |
| **Tauri** | `winapp run .\dist` | Stage exe to `dist/` first (avoids copying entire `target/` tree); GUI app |
| **Electron** | `npx winapp node add-electron-debug-identity` | Uses Electron-specific identity registration; `winapp run` is **not** recommended for Electron |

**Key rules:**
- **GUI apps** (Flutter, Tauri, WPF): use `winapp run <build-output>` — launches via AUMID activation
- **Console apps** (C++, Rust, .NET console): plain `winapp run <build-output>` — winapp detects a console app from the built binary's PE subsystem and launches it via an execution alias, so stdin/stdout reach your terminal. It stages the required `uap5:ExecutionAlias` into the AppX layout itself, so no manifest edit is needed. Pass `--without-alias` to force AUMID activation instead (the app then prints nothing); `--with-alias` only forces an alias for a *windowed* app
- **Electron**: different mechanism — uses `npx winapp node add-electron-debug-identity` because `electron.exe` is in `node_modules/`, not your build output
- **Startup debugging (any framework)**: use `winapp create-debug-identity <exe>` so your IDE can F5-launch the exe with identity from the first instruction

For full debugging scenarios and IDE setup, see the [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md).

## Related skills
- **Setup**: `winapp-setup` — initial project setup with `winapp init`
- **Manifest**: `winapp-manifest` — creating and customizing `Package.appxmanifest`
- **Signing**: `winapp-signing` — certificate generation and management
- **Packaging**: `winapp-package` — creating MSIX installers from build output
- **Identity**: `winapp-identity` — enabling package identity for Windows APIs during development
- **MAUI**: `winapp-maui` — packaging/signing .NET MAUI Windows apps and resolving the resizetizer manifest
- Not sure which command to use? See `winapp-troubleshoot` for a command selection flowchart

## CLI reference

Run `winapp <command> --help` for current command options, or `winapp --cli-schema` for the complete machine-readable command schema.
