# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
    Measure the same long-session rounds as retention.ps1 on the bench app running under the Visual
    Studio debugger (F5), with XAML Hot Reload on and off.
.DESCRIPTION
    For each mode, sets "debugging.xamlHotReload.enableXamlHotReload" (Tools > Options > Debugging >
    XAML Hot Reload) in Visual Studio's settings.json, opens a generated x64 solution for the staged
    bench app with `devenv <sln> /Command Debug.Start`, and lets retention.ps1 measure the app that
    Visual Studio launched. When the rounds finish, the app exits (ending the debug session) and the
    devenv process this script started is closed. settings.json is restored byte for byte at the end,
    even on failure.

    Visual Studio must not be running: its settings file is rewritten while it is closed. Visual
    Studio's window takes the foreground while it runs; no input is sent.

    Manual alternative (when automation is not possible): open the solution this script prints, set
    the option by hand, press F5, and run
      .\scripts\devtools-perf\retention.ps1 -Configs external -SkipPrepare
    which waits for the app to start and then measures it.
.EXAMPLE
    .\scripts\devtools-perf\vs-hot-reload.ps1 -Rounds 12
#>
[CmdletBinding()]
param(
    [ValidateSet('on', 'off')][string[]]$Modes = @('on', 'off'),
    [int]$Rounds = 12,
    [int]$NavCycles = 5,
    [ValidateSet('heavy', 'plain', 'resource', 'styled', 'button', 'checkbox')][string]$Page = 'heavy',
    [int]$ChurnElements = 2000,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\devtools-perf\vs-hot-reload')
)
$ErrorActionPreference = 'Stop'
$OutDir = (New-Item -ItemType Directory -Path $OutDir -Force).FullName
$work = Join-Path ([IO.Path]::GetTempPath()) 'winapp-devtools-perf'
$stage = Join-Path $work 'bench-app'
function Write-Step([string]$Message) { Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $Message" }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -products * -property installationPath
$vsId = & $vswhere -latest -products * -property instanceId
$vsMajor = (& $vswhere -latest -products * -property installationVersion).Split('.')[0]
$devenv = Join-Path $vsPath 'Common7\IDE\devenv.exe'
$settings = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\$vsMajor.0_$vsId\settings.json"
if (-not (Test-Path $settings)) { throw "Visual Studio settings not found: $settings" }
if (Get-Process devenv -ErrorAction SilentlyContinue) { throw 'Close Visual Studio first: its settings file is changed for the run.' }

Write-Step 'Preparing bench app'
& (Join-Path $PSScriptRoot '..\devtools-perf.ps1') -Apps bench -PrepareOnly | Out-Null

# x64-only solution with deploy enabled, and a packaged launch profile, so F5 deploys and debugs
# the same packaged build the other experiments use.
$guid = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$sln = Join-Path $stage 'bench.sln'
Set-Content $sln @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "winui-app", "winui-app.csproj", "$guid"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|x64 = Debug|x64
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		$guid.Debug|x64.ActiveCfg = Debug|x64
		$guid.Debug|x64.Build.0 = Debug|x64
		$guid.Debug|x64.Deploy.0 = Debug|x64
	EndGlobalSection
EndGlobal
"@
New-Item -ItemType Directory -Path (Join-Path $stage 'Properties') -Force | Out-Null
Set-Content (Join-Path $stage 'Properties\launchSettings.json') '{ "profiles": { "winui-app (Package)": { "commandName": "MsixPackage" } } }'

$original = [IO.File]::ReadAllBytes($settings)
try {
    foreach ($mode in $Modes) {
        Write-Step "Visual Studio, XAML Hot Reload $mode"
        $json = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json -AsHashtable
        $json['debugging.xamlHotReload.enableXamlHotReload'] = ($mode -eq 'on')
        $json['debugging.xamlHotReload.enableWinui'] = ($mode -eq 'on')
        # Trust the staged folder so opening the solution does not stop at a trust prompt.
        $trust = @($json['environment.security.trust.locations'])
        $json['environment.security.trust.locations'] = @($trust | Where-Object { $_ }) + @(@{ location = "$stage\"; locationType = 'folder'; trustedBy = 'system' })
        Set-Content $settings ($json | ConvertTo-Json -Depth 10)
        $started = Get-Date
        # Visual Studio deploys its own registration of the bench package and stops at a confirmation
        # when one already exists, so remove the harness's registration first (it is ours, from
        # -PrepareOnly or the previous mode).
        Get-AppxPackage -Name 'WinApp.DevToolsPerf.Bench' | Remove-AppxPackage
        $launch = "Start-Process -FilePath '$devenv' -ArgumentList '`"$sln`"', '/Command', 'Debug.Start' | Out-Null"
        try {
            & (Join-Path $PSScriptRoot 'retention.ps1') -Configs external -SkipPrepare -LaunchCommand $launch -Rounds $Rounds `
                -NavCycles $NavCycles -Page $Page -ChurnElements $ChurnElements -OutDir (Join-Path $OutDir $mode)
        }
        finally {
            Get-Process devenv -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $started } | ForEach-Object {
                if (-not $_.CloseMainWindow() -or -not $_.WaitForExit(20000)) { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
            }
            Start-Sleep -Seconds 5
        }
    }
}
finally {
    [IO.File]::WriteAllBytes($settings, $original)
    Write-Step 'Visual Studio settings restored'
}
