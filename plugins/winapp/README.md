# WinApp CLI plugin

Give GitHub Copilot, Claude Code, or OpenAI Codex the [WinApp CLI](https://github.com/microsoft/WinAppCli) (`winapp`) workflows for Windows apps built with any framework, including Electron, .NET, C++, Rust, Flutter, and Tauri. Ask your agent to set up the Windows SDK and Windows App SDK, add package identity, create and sign an MSIX installer, edit the app manifest, look up Windows APIs, find WinUI controls and samples, run your app in Windows Sandbox, inspect and test a running app's UI, measure WinUI performance, or fix packaging and signing errors.

For example, ask: "Package this app as a signed MSIX" or "Add package identity to this app with winapp."

## What's included

- The `winapp` agent, which picks the right skill for each request. Codex loads the skills only, not the agent.
- [Skills](https://github.com/microsoft/WinAppCli/tree/main/plugins/winapp/skills) for each WinApp CLI workflow.

## What it does on your computer

The skills have your agent run these on your Windows computer:

- WinApp CLI (`winapp`) commands. If WinApp CLI isn't installed, the agent installs it with WinGet or npm.
- Your project's own build commands, such as `dotnet build`, `npm run build`, or `cmake --build`.
- PowerShell to install or remove app packages, and Windows Sandbox tools (`wsb`, `dism`).
- The Azure CLI (`az login`), if you sign with Azure Trusted Signing and aren't signed in to Azure.

Steps that need administrator rights, such as trusting a development certificate, profiling app startup, or turning on Windows Sandbox, need your approval first. The agent also asks before it attaches WinUI DevTools to a running app or changes the app's properties. For UI automation, it sends clicks and keystrokes to the app you name, or runs the app in Windows Sandbox.

WinApp CLI downloads what each command needs:

- Packages from NuGet, such as the Windows SDK, the Windows App SDK, and build tools.
- npm packages for JavaScript bindings and native addons in Electron and Node.js projects.
- WinUI control and sample catalogs from GitHub, for `winapp find-ui`.
- The Microsoft Store Developer CLI from GitHub, for `winapp store`.
- Microsoft's VC++ runtime package, when an app that needs it runs in Windows Sandbox.
- When diagnosing a crash: Microsoft's debugging engine, the WinUI team's analysis script from GitHub, and symbols from Microsoft's symbol server when the agent adds `--symbols`.
- A periodic check on GitHub for a newer WinApp CLI. Set `WINAPP_CLI_UPDATE_CHECK=0` to turn it off.

The plugin has no MCP server and sends no data itself. WinApp CLI sends data in these cases:

- Signing with Azure Trusted Signing uses your Azure sign-in to find your signing account, then sends each file's digest to it.
- Signing with a timestamp sends the signature's hash to a timestamp server: DigiCert's in these skills' `winapp sign` examples, and Microsoft's for Azure Trusted Signing.
- Submitting to the Microsoft Store with `winapp store` uploads your app package to Microsoft Partner Center.
- WinApp CLI sends anonymous usage data to Microsoft; see [telemetry](https://github.com/microsoft/WinAppCli/blob/main/docs/telemetry.md) to turn it off.

## Install

See [Using with AI coding agents](https://github.com/microsoft/WinAppCli#-using-with-ai-coding-agents) for the Copilot CLI, Claude Code, and Codex commands.

## Feedback

[Open an issue](https://github.com/microsoft/WinAppCli/issues) in microsoft/WinAppCli. The plugin is licensed under MIT.
