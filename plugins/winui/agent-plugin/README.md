# WinUI plugin

Build Windows desktop apps with WinUI 3 and the Windows App SDK using GitHub Copilot, Claude Code, or OpenAI Codex. Ask your agent to set up your computer, create a project, design pages with Fluent controls, build and run the app, fix build errors, write UI tests, review the code, migrate a WPF app, or package the app as a signed MSIX installer.

For example, ask: "Create a new WinUI 3 app with MVVM" or "Migrate this WPF app to WinUI 3."

## What's included

- The `winui-dev` agent, which picks the right skill for each request. Codex loads the skills only, not the agent.
- [Skills](https://github.com/microsoft/WinAppCli/tree/main/plugins/winui/agent-plugin/skills) for setup, the build-and-run loop, XAML design, code review, UI testing, packaging, and WPF migration.

## What it does on your computer

The skills have your agent run the [WinApp CLI](https://github.com/microsoft/WinAppCli) (`winapp`), `dotnet`, `winget`, and PowerShell on your Windows computer:

- If WinApp CLI is missing or older than 0.7, the agent installs or updates it for your user account with WinGet and tells you.
- Steps that need administrator rights, such as installing the .NET SDK, turning on Developer Mode, or trusting a development certificate, need your approval first.
- For UI tests, the agent sends clicks and keystrokes to your app, in Windows Sandbox when it's available.

WinApp CLI and `dotnet` download what each step needs:

- Packages from NuGet, such as the Windows SDK, the Windows App SDK, app templates, and build tools.
- WinUI control and sample catalogs from GitHub, when the agent looks up controls.
- Microsoft's VC++ runtime package, when an app that needs it runs in Windows Sandbox.
- When diagnosing a crash: Microsoft's debugging engine, the WinUI team's analysis script from GitHub, and symbols from Microsoft's symbol server when the agent adds `--symbols`.
- A periodic check on GitHub for a newer WinApp CLI. Set `WINAPP_CLI_UPDATE_CHECK=0` to turn it off.

The plugin has no MCP server and sends no data itself. When the agent signs a package with a timestamp, the signature's hash goes to the timestamp server; these skills use DigiCert's. WinApp CLI sends anonymous usage data to Microsoft; see [telemetry](https://github.com/microsoft/WinAppCli/blob/main/docs/telemetry.md) to turn it off.

## Install

See [Using with AI coding agents](https://github.com/microsoft/WinAppCli#-using-with-ai-coding-agents) for the Copilot CLI, Claude Code, and Codex commands.

## Feedback

[Open an issue](https://github.com/microsoft/WinAppCli/issues) in microsoft/WinAppCli. The plugin is licensed under MIT.
