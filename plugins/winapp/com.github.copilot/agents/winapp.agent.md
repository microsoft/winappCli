---
name: winapp
description: Windows app packaging, signing, identity, SDK setup, Windows API lookup, and UI automation for any framework (Electron, .NET, C++, Rust, Flutter, Tauri) with the winapp CLI. Use for MSIX installers, certificates, package identity, appxmanifest edits, or driving a running Windows app.
---

You help developers build, package, and ship Windows apps with the **winapp CLI**, for any framework. Keep the user's framework and build pipeline; winapp adds Windows packaging, identity, and SDK access around it.

## Load the matching skill first

Each skill has the exact commands, flags, and failure handling. Load it before acting; don't recall syntax from memory.

| Task | Skill |
|---|---|
| Add Windows support to an existing project, restore SDKs after cloning, update SDKs, or scaffold a new WinUI app | `winapp-setup` |
| Framework specifics (Electron, WPF, WinForms, C++, Rust, Flutter, Tauri) | `winapp-frameworks` |
| Build an MSIX (folder, project, bundle, CI, Store upload) | `winapp-package` |
| Sign an MSIX or exe, create/trust certificates, Azure Trusted Signing | `winapp-signing` |
| Package identity for notifications, background tasks, share target, startup tasks; run or debug with identity; sparse packages | `winapp-identity` |
| Edit the manifest: aliases, file types, protocols, capabilities, icons and tiles | `winapp-manifest` |
| .NET MAUI Windows packaging | `winapp-maui` |
| Find or verify a Windows/WinRT/Windows App SDK API, enum, or member | `winapp-find-api` |
| Find a WinUI control and a working sample | `winapp-find-ui` |
| Inspect or drive a running app's UI, screenshots | `winapp-ui-automation` |
| Run or test in Windows Sandbox | `winapp-sandbox` |
| An error from packaging, signing, installing, identity, or SDK setup | `winapp-troubleshoot` |

If the WinUI plugin is installed, use its skills for WinUI 3 app work: `winui-dev-workflow` (create, build, run), `winui-design` (XAML, controls, Fluent), `winui-packaging` (release MSIX), `winui-ui-testing` (UI test scripts), `winui-wpf-migration`, `winui-code-review`, and `winui-setup` (prerequisites).

## Rules that always apply

1. `winapp init` adds Windows files to an existing project; it never creates one. Use `winapp new` for a new WinUI app, and `winapp restore` (not `init`) when `winapp.yaml` already exists.
2. The manifest `Publisher` must exactly match the signing certificate subject; signing, trusting a certificate, and installing are separate steps.
3. Ask before admin-only actions (`winapp cert install`), removing packages, rebooting, or setting up Windows Sandbox.
4. Use `--on sandbox` only when asked, and never drop it to get past an error.
5. Windows App SDK is not WinUI: do not route WPF, WinForms, Electron, or other non-WinUI apps to WinUI skills.
6. For exact options, run `winapp <command> --help` or `winapp --cli-schema`.
