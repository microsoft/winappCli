# winapp run on a project

### Run and debug with identity

```powershell
# Register debug identity and launch app from build output
winapp run ./bin/Debug

# Launch with custom manifest and pass arguments to the app
winapp run ./dist --manifest ./out/Package.appxmanifest --args "--my-flag value"

# Pass arguments after -- to avoid escaping (equivalent to --args)
winapp run ./bin/Debug -- --my-flag value

# Register identity without launching (useful for attaching a debugger manually)
winapp run ./bin/Debug --no-launch

# Launch and capture OutputDebugString messages and crash diagnostics
# Note: prevents other debuggers (VS, VS Code) from attaching — use --no-launch if you need those instead
winapp run ./bin/Debug --debug-output
```

Use `winapp run` during iterative development — it creates a loose layout package, registers a debug identity, and launches the app in one step. For identity-only registration without loose layout, use `winapp create-debug-identity` instead.

#### Project mode: `winapp run` on a `.csproj` (.NET / WinUI)

For .NET SDK projects you can point `winapp run` **at the project instead of the build output** — it builds the `.csproj` and launches it in one step, so there's no separate `dotnet build` and no need to know the output path:

```powershell
# Build and run the project in the current directory (input defaults to ".")
winapp run

# Run a specific project, configuration, and architecture
winapp run ./src/MyApp/MyApp.csproj -c Release --arch arm64

# Force an unpackaged run of a packaged project
winapp run . -p WindowsPackageType=None

# Show winapp's build decision traces (dotnet build stays at minimal verbosity)
winapp run . --verbose

# Test a project configured with <PublishAot>true</PublishAot>
winapp run . --aot
winapp run . --aot -c Release
```

Project mode supports both **packaged** and **unpackaged** WinUI apps, detected from the project's effective `WindowsPackageType` (`MSIX` ⇒ loose-layout register + AUMID launch; `None` ⇒ launch the built `.exe`), and installs the matching-architecture Windows App Runtime before launching. RID-only remains the default; when the effective configuration requires a self-contained profile, winapp selects the architecture-matching profile without forcing that platform onto referenced `AnyCPU` libraries. Requires .NET SDK 8.0.100+.

- **Build inputs:** `-c/--configuration`, `--arch`, `-r/--runtime`, `-f/--framework`, `--no-build`, `--no-restore`, `--aot`, `-p/--property` (repeat for multiple properties; use `%3B` or `%2C` for a literal semicolon or comma in a value). `--aot` supports x64/ARM64 projects, requires .NET SDK 8.0.300+ and effective `PublishAot=true`, and cannot use `--no-build` or `--manifest`; configure the project manifest before publishing.
- **Packaged-only options:** `--manifest`, `--no-launch`, `--with-alias`, `--clean`, `--unregister-on-exit`, `--output-appx-directory`, `--executable`, `--unique-identity` — rejected for unpackaged apps.
- **Output:** winapp restores dependencies, builds, and streams both commands' output live (including successful-build warnings). Restore output uses sanitized plain lines; interactive terminals show dotnet's in-place build progress when no build-time restore is needed, while other build output uses sanitized plain lines. `--json` sends restore/build invocations and child output to **stderr** so stdout stays valid JSON. `--quiet` suppresses invocations and sends dotnet's quiet restore/build output to **stderr** so stdout stays clean. With `--aot`, `--verbose` adds the publish command and resolved paths; publish diagnostics go to **stderr** under `--json`/`--quiet`.

For parallel packaged checkouts, see **Parallel packaged worktrees** in this skill and the canonical
[`--unique-identity` workflow](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#unique-identity-for-parallel-checkouts).
Do not add this flag to an unpackaged run or automatically remove another checkout's
registration to make normal mode succeed.

#### Project mode: `winapp run` on a C++ `.vcxproj`

A Visual Studio C++ project (for example the **WinUI Blank App (Packaged)** C++/WinRT template) runs the same way. winapp builds it with Visual Studio's MSBuild (restoring `packages.config` first), then launches it packaged or unpackaged:

```powershell
winapp run .\MyApp.vcxproj
winapp run . -c Release --arch arm64 --detach --json
```

Requires Visual Studio or Build Tools with the **Desktop development with C++** workload (WinUI 3 apps also need **C++ WinUI app development tools**); the .NET SDK is not needed. If the C++ build tools, platform toolset, or Windows SDK are missing, the error says what to install. `--framework` and `--aot` are .NET-only.

#### Choosing between `run` and `create-debug-identity`

| | `winapp run` | `create-debug-identity` |
|---|---|---|
| **Registers** | Full loose layout package (entire folder) | Sparse package (single exe) |
| **App launch** | Winapp launches via AUMID or alias | You launch the exe yourself |
| **Simulates MSIX** | Yes — closest to production | No — identity only |
| **Files** | Copied to AppX layout dir | Exe stays in place |
| **Best for** | Most frameworks (.NET, C++, Rust, Flutter, Tauri) | Electron, or F5 startup debugging |

**Default to `winapp run`.** Use `create-debug-identity` when you need your IDE to launch and debug the exe directly (startup debugging), or when the exe is separate from your source (Electron).

Console apps keep stdin/stdout in the current terminal automatically: winapp reads the app's output type — from the project or `.cs` file where it can, and from the built binary's PE subsystem when running a build-output folder. Pass `--with-alias` only to force it for a windowed app, or when a folder holds several executables and detection can't pick one.

> **`--debug-output` caveat:** Captures `OutputDebugString` and crash diagnostics (minidump + automatic analysis for both managed and native crashes) but attaches winapp as the debugger — you cannot also attach VS Code or WinDbg. Use `--no-launch` if you need your own debugger. Add `--symbols` to download PDB symbols for richer native crash analysis. For WinUI 3 apps, a stowed-exception triage pass runs automatically (surfacing the originating HRESULT and native XAML dispatch stack); the debugger components it needs are downloaded on first use, or set `WINAPP_DBGTOOLS_DIR` to a directory containing `dbgeng.dll` and `JsProvider.dll` for offline/locked-down environments.

For full debugging scenarios and IDE setup, see the [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md).
