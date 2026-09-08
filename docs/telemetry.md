---
title: Windows App Development CLI telemetry
description: The Windows App Development CLI collects usage information and sends it to Microsoft. Learn what data is collected and how to opt out.
ms.date: 2026-08-25
---

# Windows App Development CLI telemetry

The [Windows App Development CLI](usage.md) (winapp) includes a telemetry feature that collects anonymous usage data and sends it to Microsoft when you use CLI commands. The usage data includes exception information when the CLI crashes. Telemetry data helps the Windows App team understand how the tool is used so it can be improved. Information on failures helps the team resolve problems and fix bugs.

The collected data is anonymous. No personal information such as usernames, email addresses, or source code is collected.

> [!IMPORTANT]
> Telemetry is **only sent by the official, signed releases** of the winapp CLI distributed through official channels (winget, npm, GitHub releases, etc.). If you clone the repository and build the CLI yourself, or run it in a debugger, **no telemetry data is sent**. This means developers and contributors working on the CLI source code do not transmit telemetry events during development, testing, or debugging.

## Scope

Telemetry **is collected** when using any winapp CLI command, including:

- `winapp init`
- `winapp restore`
- `winapp package`
- `winapp manifest`
- `winapp sign`
- `winapp cert`
- `winapp tool`
- `winapp update`
- `winapp find-ui`

## How to opt out

The winapp CLI telemetry feature is enabled by default. To opt out of the telemetry feature, set the `WINAPP_CLI_TELEMETRY_OPTOUT` environment variable to `1`.

**Windows (PowerShell):**
```powershell
$env:WINAPP_CLI_TELEMETRY_OPTOUT = "1"
```

**Windows (Command Prompt):**
```cmd
set WINAPP_CLI_TELEMETRY_OPTOUT=1
```

To make this permanent, add the environment variable through System Properties > Environment Variables, or use:

```powershell
[System.Environment]::SetEnvironmentVariable('WINAPP_CLI_TELEMETRY_OPTOUT', '1', 'User')
```

## Disclosure

The winapp CLI displays the following (or similar) message when you first run any CLI command. This "first run" experience is how Microsoft notifies you about data collection.

```console
Welcome to the Windows App Development CLI! By using this tool, you agree to the 
collection of anonymous usage data to help improve the product. You can read the 
full privacy policy at https://go.microsoft.com/fwlink/?LinkId=521839

You can opt out of telemetry by setting the WINAPP_CLI_TELEMETRY_OPTOUT environment 
variable to '1'.

For more information, please visit: https://aka.ms/winappcli-telemetry-optout
```

This message is shown only once per user. A marker file (`.first-run-complete`) is created in the user .winapp directory to track that the notice has been displayed.

## Data points

The telemetry feature doesn't collect personal data, such as usernames or email addresses. It doesn't scan source code and doesn't collect project names, paths, repository identities, authors, package versions, or dependency lists. For project-aware commands (`init`, `run`, `restore`, `update`, and `package`), the CLI checks only a bounded set of recognized project metadata markers in the selected directory and up to eight parent directories, stopping at a Git repository boundary. Integrated callers such as the winapp NuGet package can instead provide an allow-listed framework category derived from evaluated build properties. Only the normalized categories described below are transmitted.

Recognized metadata includes project files such as `.csproj` and `package.json` and marker files such as `tauri.conf.json`, `pubspec.yaml`, `Cargo.toml`, `CMakeLists.txt`, and `winapp.yaml`. Metadata files read for classification are size-limited. Their contents, names, paths, and unrecognized values aren't included in telemetry. Project classification isn't performed when telemetry is opted out.

Protecting your privacy is important to us. If you suspect the telemetry is collecting sensitive data or the data is being insecurely or inappropriately handled, file an issue in the [microsoft/winsdk](https://github.com/microsoft/winsdk/issues) repository for investigation.

### Collected data

The telemetry feature collects the following data:

| Data point | Description |
|------------|-------------|
| Command name | The full type name of the command invoked (e.g., `WinApp.Cli.Commands.InitCommand`). |
| Command arguments | The names and sanitized values of command arguments. String values (file paths, directory paths) are replaced with `[string]` to avoid collecting sensitive information. |
| Command options | The names and sanitized values of command options. String values are replaced with `[string]` to avoid collecting sensitive information. Boolean values and enums are collected as-is. |
| Exit code | The exit code returned by the command (0 for success, non-zero for failure). |
| Timestamp | When the command was invoked and when it completed. |
| CLI version | The version of the winapp CLI tool (from assembly version). |
| CI environment | A boolean flag indicating whether the CLI is running in a Continuous Integration environment. |
| Caller | The value of the `WINAPP_CLI_CALLER` environment variable, if set. This allows wrapper tools (like the npm package) to identify themselves. |
| Project context | For `init`, `run`, `restore`, `update`, and `package`, a separate correlated event records bounded categories describing the project family (`dotnet`, `node`, `cpp`, `rust`, `dart`, `hybrid`, `mixed`, or `unknown`), recognized app framework (such as `winui`, `wpf`, `winforms`, `maui`, `electron`, `tauri`, `flutter`, `react-native-windows`, `avalonia`, `uwp`, `windows-app-sdk`, or `other-dotnet`), target kind, detection source, confidence, packaging model, and whether `run` used project or folder execution. It doesn't include project names, paths, repositories, versions, dependency lists, or source. |
| `find-ui` usage | For the `winapp find-ui` command only, an additional usage event with non-personal, bounded values: the mode (`search`, `fetch`, or `list`); the selected `--source` (a fixed value — `gallery`, `toolkit`, `reactor`, or `core`); the catalog scenario IDs fetched (e.g. `gallery-tabview-1`), which identify built-in WinUI sample controls, never your code; whether `--json` was used; and result/ID counts. The free-form search query is **never** collected, and any requested IDs that don't match a real catalog entry are counted but **not** collected as text. |

### Sanitization of sensitive data

The winapp CLI takes several measures to protect your privacy:

- **Command arguments and options** of type `string`, `FileInfo`, or `DirectoryInfo` are replaced with `[string]`.
- **Implicit values** (default values that weren't explicitly provided) are not collected.
- **Parsing errors** are logged as `[error]` without including the actual erroneous input.
- **Project classification** emits only fixed, allow-listed category values. Unknown or unrecognized metadata is reported as `unknown` or a generic family category rather than transmitted as text.
- All string values in telemetry events undergo **sensitive string replacement** before transmission, which replaces any registered sensitive strings with anonymized tokens.

## Crash exception telemetry

If the winapp CLI crashes, it collects the name of the exception and stack trace of the CLI code. This information is collected to assess problems and improve the quality of the tool. This article provides information about the data we collect. It also provides tips on how users building their own version of the winapp CLI can avoid inadvertent disclosure of personal or sensitive information.

The winapp CLI collects information for CLI exceptions only, not exceptions in your application. The collected data contains the name of the exception and the stack trace. This stack trace is of CLI code only.

The following example shows the kind of data that is collected:

```console
System.IO.IOException
  at System.IO.FileStream.Write(Byte[] buffer, Int32 offset, Int32 count)
  at WinApp.Cli.Services.MsixService.GenerateManifest()
  at WinApp.Cli.Commands.ManifestGenerateCommand.Handler.InvokeAsync(InvocationContext context)
  at WinApp.Cli.Program.Main(String[] args)
```

## Developer and contributor information

**If you build the winapp CLI from source yourself, no telemetry data is sent.** Telemetry is only active in the official, signed releases of the tool.

This means:
- Cloning the repository and building locally: **No telemetry sent**
- Running the CLI in a debugger: **No telemetry sent**
- Building from source for testing: **No telemetry sent**
- Using official releases from npm or GitHub: **Telemetry is sent** (unless opted out)

Contributors and developers can work on the winapp CLI source code without any concern about inadvertently sending telemetry data during development.

## See also

- [winapp CLI Usage Documentation](usage.md)
- [Microsoft Privacy Statement](https://go.microsoft.com/fwlink/?LinkId=521839)
