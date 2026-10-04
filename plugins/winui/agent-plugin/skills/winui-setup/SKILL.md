---
name: winui-setup
description: "Install and verify WinUI 3 prerequisites — .NET SDK 8.0.100+, WinApp CLI 0.7+, and Developer Mode; identify additional Native AOT and Windows Sandbox requirements. Use when the user asks to set up or repair the toolchain, or when another WinUI skill finds a missing or outdated prerequisite."
---

### Purpose

Install and verify the prerequisites every other `winui-*` skill assumes. WinApp CLI 0.7 owns WinUI template discovery and installation through `winapp new`; **do not install the template pack separately**. The project's target framework may require a newer .NET SDK than the CLI's minimum.

> [!IMPORTANT]
> Install per-user prerequisites (WinApp CLI) without asking, and tell the user what changed. **Ask before anything that needs admin rights (UAC) or a large toolchain** — the .NET SDK, Developer Mode, and Native AOT build tools. Windows Sandbox enablement is the user's to do.

This skill is idempotent: detect everything first, install or upgrade only what is needed, and print one final summary.

### Steps

#### Detect everything

Run these checks together so the user sees the full state before anything changes:

```powershell
$minimumDotNet = [version]'8.0.100'
$minimumWinApp = [version]'0.7.0'

# .NET SDK — project-mode winapp run requires SDK 8.0.100+
$dotnetSdks = @(& dotnet --list-sdks 2>$null) | ForEach-Object {
    $text = ($_ -replace ' \[.*$','').Trim()
    $parsed = $null
    if ([version]::TryParse(($text -split '-')[0], [ref]$parsed)) { $parsed }
}
$dotnetVersion = $dotnetSdks |
    Where-Object { $_ -ge $minimumDotNet } |
    Sort-Object -Descending |
    Select-Object -First 1
$dotnetOk = $null -ne $dotnetVersion

# WinApp CLI — require released 0.7+ for the migrated workflows
$winappCmd = Get-Command winapp -ErrorAction SilentlyContinue
$winappVersion = $null
$winappPrerelease = $false
if ($winappCmd) {
    foreach ($line in @(& winapp --version 2>$null)) {
        $match = [regex]::Match(
            [string]$line,
            '^\s*v?(?<version>\d+\.\d+\.\d+)(?:-(?<prerelease>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?\s*$'
        )
        if ($match.Success) {
            $parsed = $null
            if ([version]::TryParse($match.Groups['version'].Value, [ref]$parsed)) {
                $winappVersion = $parsed
                $winappPrerelease = $match.Groups['prerelease'].Success
            }
        }
    }
}
$winappOk = ($winappVersion -ge $minimumWinApp) -and -not $winappPrerelease

# Developer Mode
$devModeOk = ((Get-ItemProperty `
    -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
    -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
).AllowDevelopmentWithoutDevLicense) -eq 1
```

Print a one-shot status table:

```text
.NET SDK >= 8.0.100     [OK] found 10.0.100
WinApp CLI >= 0.7.0     [!] found 0.6.0 — will upgrade
Developer Mode          [X] disabled — needs admin to enable
```

#### Install what's missing

##### .NET SDK (ask first)

The SDK installer is machine-wide and triggers UAC. **Ask the user first.** When they agree and no SDK at or above `8.0.100` was found, install the recommended SDK:

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact --silent --accept-package-agreements --accept-source-agreements
```

Do not install another SDK when one already satisfies both the CLI minimum and
the requested project's target framework / `global.json`. The CLI's minimum
does not let an 8.x SDK build a `net10.0-windows` app.
If the base check passed but the requested project needs another SDK, report
that requirement and offer to install its matching SDK rather than declaring
the project ready. If the user declines, print the command for later use.

##### WinApp CLI (no admin needed)

WinApp CLI is a per-user MSIX package, so install or upgrade it **without asking** and tell the user what changed. If `winapp` is missing, install it. If it is below released 0.7.0, upgrade it. Skip both when the installed version already meets the minimum:

```powershell
# When winapp is missing
winget install --id Microsoft.WinAppCli --exact --silent --accept-package-agreements --accept-source-agreements

# When winapp does not meet the released 0.7.0 minimum
winget upgrade --id Microsoft.WinAppCli --exact --silent --accept-package-agreements --accept-source-agreements
```

Refresh PATH after any winget install or upgrade:

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path','User')
```

Run the version detection again. If a released `0.7.0` or later is still unavailable, report the actual version and mark setup blocked; do not fall back to older commands. A prerelease doesn't meet the requirement unless the user asks for preview validation.

##### Developer Mode (ask first)

Developer Mode requires admin elevation. **Ask the user before triggering UAC.** Only if they agree, elevate this one operation:

```powershell
Start-Process powershell -Verb RunAs -ArgumentList @(
    '-NoProfile','-Command',
    "New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Force | Out-Null; " +
    "Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' " +
    "-Name AllowDevelopmentWithoutDevLicense -Type DWord -Value 1"
) -Wait
```

If the user declines or dismisses UAC, continue to the summary and print the command for later use.

#### Additional requirements for the requested workflow

| Workflow | Additional requirements |
|---|---|
| Native AOT publish/run | Windows native compiler/linker toolchain: Visual Studio or Build Tools with **Desktop development with C++**, including the target architecture's tools and Windows SDK. See [Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/). These are not required for normal JIT iteration. |
| Windows Sandbox app runs and UI automation (preferred when available) | WinApp's integration requires Windows 11 24H2+, hardware virtualization, and a working Windows Sandbox feature/client. Supported editions include **Pro, Enterprise, and Education; not Home**. Input/capture needs an unlocked host and a connected, non-minimized client. See [WinApp Sandbox prerequisites](https://github.com/microsoft/winappCli/blob/main/docs/sandbox-execution.md#prerequisites). |

Report these separately from the base toolchain. `winapp target snapshot sandbox --json` inspects an existing
guest without starting or repairing it; "no target running" alone does not
mean the Windows feature is unavailable. `winui-ui-testing`
Step 1 defines what to do when Windows Sandbox is unavailable.

Enabling Windows Sandbox is a **user action** (admin plus a reboot): ask the user
to open **Turn Windows features on or off**, select **Windows Sandbox**, and
restart if prompted. Hardware virtualization must be enabled; a VM may also
require nested virtualization. See Microsoft's
[supported editions](https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/)
and [installation steps](https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-install).

### Final summary

Always print a single summary:

```text
==== winui-setup summary ====
.NET SDK >= 8.0.100     [>] already present (10.0.100)
WinApp CLI >= 0.7.0     [OK] upgraded to 0.7.0
Developer Mode          [OK] enabled
```

Only report readiness for the workflows whose requirements passed. If the current harness exposes the `winui-dev` orchestrator agent,
start a fresh session with that agent and ask it to build a WinUI app. Otherwise,
start a fresh session in the current harness and ask it to perform the WinUI task;
it will load the relevant `winui-*` skills on demand.

For GitHub Copilot CLI, for example:

    copilot --agent winui:winui-dev -p "build me a WinUI 3 markdown editor"

### Things to NOT do

- Do not install Visual Studio; it is optional. Native AOT needs only the MSVC C++ build tools, a separate, explicitly approved setup.
- Do not install or upgrade the user's AI coding harness; this skill manages Windows/WinUI development prerequisites only.
- Do not elevate the entire session; request elevation only for the specific approved setup operation.
- Do not silently retry failed installs.
