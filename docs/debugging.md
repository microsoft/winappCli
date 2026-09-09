<!-- mslearn: true -->
<!-- description: Register temporary package identity for an unpackaged app so you can debug identity-dependent Windows features directly from your build output. -->
# Debugging with Package Identity

Many Windows APIs (push notifications, background tasks, share target, startup tasks, Windows AI APIs) require your app to have **package identity**. During development, you don't want to build a full MSIX installer every time you test — winapp provides two commands to give your app identity on the fly.

> **Using Visual Studio with a packaging project?** If you are already using Visual Studio for your packaged project, you likely don't need winapp for debugging. Visual Studio already handles package registration, identity, AUMID activation, debugger attachment, and activation-code debugging — all from F5. It also offers **Debug → Other Debug Targets → Debug Installed App Package** for advanced scenarios. The workflows below are most useful for **VS Code users, terminal-based workflows, and frameworks that VS doesn't natively package** (Rust, Flutter, Tauri, Electron, plain C++).

## Two approaches: `winapp run` vs `create-debug-identity`

| | `winapp run` | `create-debug-identity` |
|---|---|---|
| **What it registers** | Full loose layout package (entire folder) | Sparse package (single exe) |
| **How the app launches** | Launched by winapp (AUMID activation or execution alias) | You launch the exe yourself (command line, IDE, etc.) |
| **Simulates MSIX install** | Yes — closest to production behavior | No — sparse identity only |
| **Files stay in place** | Copied to an AppX layout directory | Yes — exe stays at its original path |
| **Identity scope** | Entire folder contents (exe, DLLs, assets) | Single executable |
| **Debugger-friendly** | Attach to PID after launch, or use `--no-launch` then launch via alias | Launch directly from your IDE's debugger — the exe has identity regardless |
| **Console app support** | Launched through an execution alias automatically, so stdin/stdout stay in this terminal | Run exe directly in terminal |
| **Best for** | Most frameworks (.NET, C++, Rust, Flutter, Tauri) | Electron, or when you need full IDE debugger control (F5) |

## When to use which

### Default: `winapp run`

Use `winapp run` for most development workflows. It simulates a real MSIX install — your app gets the same identity, capabilities, and file associations it would have in production.

```powershell
# Build your app, then:
winapp run .\build\output
```

### Use `create-debug-identity` when:

- **Your exe is separate from your build output** — e.g., Electron apps where `electron.exe` lives in `node_modules/`
- **You need to debug startup code** and can't attach a debugger fast enough after AUMID launch
- **With some debuggers where you can't launch with AUMID** and need identity on the launched process — `create-debug-identity` registers the exe so it has identity no matter how it's started
- **You're testing sparse package behavior** specifically (AllowExternalContent, TrustedLaunch)

```powershell
# Register identity for an exe, then launch it however you want:
winapp create-debug-identity .\bin\Debug\myapp.exe
.\bin\Debug\myapp.exe   # or F5 in your IDE
```

## Debugging scenarios

### Scenario A: Just run with identity

The simplest workflow — build, run with identity, done.

```powershell
winapp run .\build\Debug
```

Winapp registers the folder as a loose layout package and launches the app. Identity-requiring APIs work immediately. This covers the majority of development and testing scenarios.

For **console apps** nothing extra is needed: winapp detects a console app and launches it through an execution alias, so stdin/stdout reach this terminal. Pass `--without-alias` to force AUMID activation instead, and `--with-alias` to force an alias for a *windowed* app:

```powershell
winapp run .\build\Debug --without-alias
```

### Scenario B: Attach a debugger to a running app

Launch with `winapp run`, note the PID, then attach your IDE's debugger.

```powershell
winapp run .\build\Debug
# Output: Process ID: 12345
```

Then in your IDE:
- **VS Code**: Run and Debug → select "Attach" configuration (see [IDE setup](#ide-setup) below)
- **WinDbg**: `windbg -p 12345`

> **Limitation:** You'll miss any code that runs before you attach. For startup debugging, use Scenario D (`create-debug-identity`).

### Scenario C: Register identity, then launch via AUMID or alias from your IDE

Use `--no-launch` to register the package, then launch the app through its AUMID (reported by `run`) or **execution alias** from your IDE.

**Step 1:** Register the package without launching:

```powershell
winapp run .\build\Debug --no-launch
```

**Step 2:** Configure your IDE to launch via the AUMID or the **execution alias** (not the exe directly). 
* Launching with AUMID: Use the command `start shell:AppsFolder\<AUMID>`. `winapp run` outputs the AUMID when the app is registered.
* Launching with the alias: The alias must be defined in your manifest (`Package.appxmanifest` preferred, `appxmanifest.xml` also supported).

> **Important:** Simply launching the exe in the build folder will **not** give it identity. The app must be started via AUMID activation or its execution alias. This is how loose layout packages work - identity is tied to the activation path, not the exe file.

### Scenario D: Launch from your IDE with identity (startup debugging)

This is the best approach for **debugging startup code with full IDE control** - your IDE's debugger controls the process from the very first instruction, and the exe has identity no matter how it's launched.

```powershell
winapp create-debug-identity .\build\Debug\myapp.exe
```

Now launch the exe any way you like — from the terminal, from VS Code's F5, from a script. The exe has identity because Windows registered a **sparse package** pointing directly at it.

> **How it differs from `winapp run`:** With `create-debug-identity`, identity is tied to the exe itself (via `Add-AppxPackage -ExternalLocation`). With `winapp run`, identity is tied to the loose layout package — the app must be launched through AUMID or an alias. This makes `create-debug-identity` the better choice when you need your IDE to launch and debug the exe directly.

> This is also the best approach for **Electron apps** where the exe path differs from your source directory.

### Scenario E: Capture debug output and crash diagnostics

Capture `OutputDebugString` messages and first-chance exceptions inline. Framework noise (WinUI, COM, DirectX internal traces) is filtered from the console so only your app's debug messages appear. Everything is still written to the log file for full investigation.

If the app crashes, a minidump is captured and analyzed automatically:

```powershell
winapp run .\build\Debug --debug-output
```

On crash, the output includes the exception type, message, and stack trace with source file and line numbers (resolved from PDBs in your build output folder). Managed (.NET) crashes are analyzed instantly with no external tools. Native (C++/WinRT) crashes show module names and offsets; add `--symbols` to download PDB symbols for full function names:

```powershell
winapp run .\build\Debug --debug-output --symbols
```

> **Important:** This attaches winapp as the debugger. Windows only allows one debugger per process, so you **cannot** also attach Visual Studio, VS Code, or WinDbg.

#### WinUI stowed-exception triage

Most WinUI crashes start inside a XAML event handler and surface as a **stowed exception** (`0xC000027B`) that is re-raised later from the dispatcher, so the normal stack no longer points at the real cause. When the crashed app loaded `Microsoft.UI.Xaml.dll`, winapp automatically runs an extra triage pass that decodes the stowed exception and the native XAML dispatch chain (`Microsoft.UI.Xaml` → `CXcpDispatcher` → `CoreMessagingXP` → CLR host). The result is appended to the debug log. No flag is needed — it is enabled automatically for WinUI dumps. Add `--symbols` for fully resolved function names in the dispatch chain.

To make this work, winapp captures the crash dump with the terminating stowed exception's record (and its parameters, which point at the stowed-exception array) while keeping the first-chance thread context, so the standard managed analysis still recovers your original user frame *and* the triage pass can locate the stowed exception.

This pass hosts DbgEng with the WinUI team's WinDbg JavaScript extension. The debugging engine comes from NuGet; `JsProvider.dll` (the JavaScript host, not published on NuGet) is fetched on first use from the official WinDbg download. Both are version-pinned and verified before load — SHA-512 content hashes, plus a Microsoft Authenticode signature check on `JsProvider.dll` — and any verification failure skips triage rather than loading unverified code. Everything is cached under the winapp global directory, so later runs are offline.

If your environment blocks those downloads, either install **Debugging Tools for Windows** (via the Windows SDK) or set `WINAPP_DBGTOOLS_DIR` to a debugger directory containing `dbgeng.dll` and `JsProvider.dll`. When `WINAPP_DBGTOOLS_DIR` is set it is authoritative — only that directory is consulted — and if it's incomplete the log names the missing component.

When triage succeeds the console shows a one-line verdict (the stowed exception's error code/message). When it can't run, the console says so, the log explains why, and the standard managed/native analysis still runs.

The triage pass runs in a short-lived child process, because winapp's main process has already loaded the system `dbghelp.dll` and the modern `dbgeng.dll` can't bind alongside it. Decoding the stowed-exception structures also needs OS symbols (`combase.dll`), which `--symbols` downloads from the Microsoft public symbol server; on builds whose symbols aren't published there, triage still identifies the stowed exception but can't fully expand it.

## IDE setup

### VS Code

The **[WinApp VS Code extension](https://marketplace.visualstudio.com/items?itemName=Microsoft-WinAppCLI.winapp)** provides a custom **`winapp` debug type** that launches your app with package identity and attaches the debugger — all from a single **F5** press. Install it from the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=Microsoft-WinAppCLI.winapp); its source and issue tracker live in the [microsoft/WinAppVSCE](https://github.com/microsoft/WinAppVSCE) repository.

#### One-press F5 debugging with identity

Add a `winapp` launch configuration to `.vscode/launch.json`:

```jsonc
{
    "version": "0.2.0",
    "configurations": [
        {
            "type": "winapp",
            "request": "launch",
            "name": "WinApp: Launch and Attach"
        }
    ]
}
```

When you press **F5**:

1. The extension scans your workspace for build output directories containing `.exe` files.
2. You select the build folder to run (or set `inputFolder` to skip the prompt).
3. It launches your app via `winapp run` to give it package identity.
4. A child debug session attaches to the running process using the debugger you specified.

Once the debugger attaches, you get the full VS Code debugging experience — set breakpoints by clicking the gutter, step through code line-by-line (`F10`), step into functions (`F11`), inspect variables in the **Variables** pane, and evaluate expressions in the **Debug Console**. The app runs with package identity throughout, so identity-dependent APIs behave exactly as they would in production.

> **Important:** The `winapp` debug type does **not** build your project automatically. After making code changes, rebuild before pressing F5.

#### Automate builds with `preLaunchTask`

To avoid forgetting to rebuild, add a `preLaunchTask` that builds your project before every debug session:

1. Define a build task in `.vscode/tasks.json` (example for .NET):
    ```jsonc
    {
        "version": "2.0.0",
        "tasks": [
            {
                "label": "build",
                "command": "dotnet",
                "type": "process",
                "args": ["build", "${workspaceFolder}"],
                "problemMatcher": "$msCompile"
            }
        ]
    }
    ```
2. Reference it in your `launch.json`:
    ```jsonc
    {
        "type": "winapp",
        "request": "launch",
        "name": "WinApp: Launch and Attach",
        "preLaunchTask": "build"
    }
    ```

#### Configuration properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `inputFolder` | string | | Path to the build output folder containing your app binaries (e.g., `${workspaceFolder}/bin/Debug/net8.0-windows10.0.22621`). If not set, you will be prompted to select a folder. |
| `manifest` | string | | Path to an AppX manifest file (e.g., `AppxManifest.xml`, `Package.appxmanifest`, or `appxmanifest.xml`). If not set, the CLI auto-detects from the input folder or current directory. |
| `debuggerType` | string | `coreclr` | Underlying debugger to use (`coreclr`, `cppvsdbg`, or `node`). |
| `workingDirectory` | string | workspace folder | Working directory for the application. |
| `args` | string | | Command-line arguments to pass to the application. |
| `outputAppxDirectory` | string | | Output directory for the loose-layout package. Defaults to an `AppX` folder inside the input folder. |
| `port` | number | `9229` | (`node` only) The port used for the Node.js `--inspect` listener and the attach connection. Override when the default port is already in use. |

#### Supported debuggers

| `debuggerType` | Language | Required Extension |
|----------------|----------|--------------------|
| `coreclr` (default) | C# / .NET | [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp) |
| `cppvsdbg` | C / C++ | [C/C++](https://marketplace.visualstudio.com/items?itemName=ms-vscode.cpptools) |
| `node` | Node.js / Electron | Built-in |

Example for a C++ project:

```jsonc
{
    "type": "winapp",
    "request": "launch",
    "name": "WinApp: Launch C++ App",
    "debuggerType": "cppvsdbg"
}
```

#### Startup debugging with Create Debug Identity

If you need to debug startup code from the very first instruction, the F5 attach approach may miss early code. Instead, use the **WinApp: Create Debug Identity** command from the Command Palette (`Ctrl+Shift+P`) to register a sparse package for your executable, then launch it with your standard debugger:

```jsonc
{
    "name": "Launch (with identity)",
    "type": "coreclr",
    "request": "launch",
    "program": "${workspaceFolder}/bin/Debug/net8.0-windows10.0.22621/myapp.exe"
}
```

Since `create-debug-identity` registers identity on the exe itself, the app has identity no matter how it's launched — including from a standard VS Code launch configuration.

#### Attach to a running process

If you prefer to launch with `winapp run` from the terminal and then attach, use a standard attach configuration:

```json
{
    "name": "Attach to Process",
    "type": "coreclr",
    "request": "attach",
    "processId": "${command:pickProcess}"
}
```

For C++/Rust, use `"type": "cppvsdbg"` (MSVC) or `"type": "lldb"` (LLDB):

```json
{
    "name": "Attach (C++)",
    "type": "cppvsdbg",
    "request": "attach",
    "processId": "${command:pickProcess}"
}
```

#### Cleaning up

When you're done testing, run **WinApp: Unregister Package** from the Command Palette to remove sideloaded development packages without leaving VS Code.
