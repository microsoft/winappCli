# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
    WinUI XAML diagnostics cost repro: build, run each configuration, print a comparison.
.DESCRIPTION
    Configurations (each a fresh launch of ReproApp, which measures itself and exits):
      off         nothing attached
      env         ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1 only
      tap         ReproTap loaded with InitializeXamlDiagnosticsEx, never subscribes
      tap-advise  ReproTap loaded and subscribed to visual-tree changes (callback only counts)
      vs-on       under the Visual Studio debugger (F5) with XAML Hot Reload on
      vs-off      under the Visual Studio debugger (F5) with XAML Hot Reload off

    vs-on and vs-off open Visual Studio (it takes the foreground) with `devenv ReproApp.sln
    /Command Debug.Start`, after setting debugging.xamlHotReload.enableXamlHotReload in its
    settings.json. Visual Studio must be closed beforehand; settings.json is restored byte for byte.

    Requires the .NET 10 SDK and Visual Studio with the C++ x64 tools (for the TAP) and, for vs-*,
    the WinUI workload. Use -Summarize <csv files> to compare CSVs from manual runs.
.EXAMPLE
    .\run.ps1
.EXAMPLE
    .\run.ps1 -Configs off,tap,tap-advise -Rounds 10
.EXAMPLE
    .\run.ps1 -Summarize .\results\off.csv, $env:TEMP\winui-diag-repro\run-20261006-201500.csv
#>
[CmdletBinding()]
param(
    [ValidateSet('off', 'env', 'tap', 'tap-advise', 'vs-on', 'vs-off')]
    [string[]]$Configs = @('off', 'env', 'tap', 'tap-advise'),
    [int]$Rounds = 12,
    [int]$Navs = 10,
    [int]$Churn = 40000,
    [string]$OutDir = (Join-Path $PSScriptRoot 'results'),
    [string[]]$Summarize
)
$ErrorActionPreference = 'Stop'

function Show-Summary([string[]]$Files) {
    $rows = foreach ($file in $Files) {
        $lines = Get-Content $file
        $header = ($lines | Where-Object { $_ -like '# *' } | Select-Object -First 1)
        $data = @($lines | Where-Object { $_ -notlike '#*' } | ConvertFrom-Csv)
        $mem = @($data | Where-Object phase -eq 'memory')
        $nav = @($data | Where-Object phase -eq 'navigation')
        if ($mem.Count -lt 3 -or $nav.Count -lt 2) { Write-Warning "$file is incomplete."; continue }
        # Retention: growth of the settled floor from round 2 on (round 1 includes warm-up).
        $growth = ([double]$mem[-1].privateMB - [double]$mem[1].privateMB) / ($mem.Count - 2)
        $churnMs = @($mem | ForEach-Object { [double]$_.churnMs } | Sort-Object)
        # Navigation: least-squares slope across rounds (ms per round of navigations), plus the
        # first round and the mean of the last four. Individual rounds are bimodal, so single values mislead.
        $ys = @($nav | ForEach-Object { [double]$_.navP50Ms })
        $mx = ($ys.Count - 1) / 2.0; $my = ($ys | Measure-Object -Average).Average
        $num = 0.0; $den = 0.0
        for ($k = 0; $k -lt $ys.Count; $k++) { $num += ($k - $mx) * ($ys[$k] - $my); $den += ($k - $mx) * ($k - $mx) }
        $first = $ys[0]
        $last = [Math]::Round(($ys | Select-Object -Last 4 | Measure-Object -Average).Average)
        [pscustomobject]@{
            Run = [IO.Path]::GetFileNameWithoutExtension($file)
            'Churn ms' = $churnMs[[int][Math]::Floor(($churnMs.Count - 1) / 2)]
            'Retained B/element' = [Math]::Round($growth * 1MB / $Churn)
            'Nav CPU ms, round 1' = $first
            'Nav CPU ms, last 4 rounds' = $last
            'Nav slope, ms/round' = [Math]::Round($num / $den)
            Attached = $header -replace '^# ', ''
        }
    }
    $rows | Format-Table -AutoSize -Wrap | Out-String -Width 220
}

if ($Summarize) { Show-Summary $Summarize; return }

$OutDir = (New-Item -ItemType Directory -Path $OutDir -Force).FullName
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$env:PATH = "$(Split-Path $vswhere);$env:PATH"  # vcvars64.bat calls vswhere
$vsPath = & $vswhere -latest -products * -property installationPath
$tapDir = Join-Path $PSScriptRoot 'ProbeTap'
$bin = Join-Path $tapDir 'bin'
New-Item -ItemType Directory -Path $bin -Force | Out-Null

Write-Host 'Building ReproTap and Inject'
$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
& $env:ComSpec /c "call `"$vcvars`" >nul && cd /d `"$bin`" && cl /nologo /std:c++20 /O2 /MT /EHsc /W3 /WX /DUNICODE /LD `"$tapDir\ReproTap.cpp`" /Fe:ReproTap.dll /link ole32.lib oleaut32.lib /EXPORT:DllGetClassObject,PRIVATE /EXPORT:DllCanUnloadNow,PRIVATE >nul && cl /nologo /O2 /MT /W3 /WX /DUNICODE `"$tapDir\Inject.cpp`" /Fe:Inject.exe /link ole32.lib >nul"
if ($LASTEXITCODE) { throw 'Building the TAP failed.' }

Write-Host 'Building ReproApp (Debug, x64)'
$project = Join-Path $PSScriptRoot 'ReproApp\ReproApp.csproj'
dotnet build $project -c Debug -p:Platform=x64 --nologo -v quiet | Out-Host
if ($LASTEXITCODE) { throw 'Building ReproApp failed.' }
$exe = Get-ChildItem (Join-Path $PSScriptRoot 'ReproApp\bin\x64\Debug') -Recurse -Filter ReproApp.exe | Select-Object -First 1 -ExpandProperty FullName

$csvs = @()
foreach ($config in $Configs) {
    $csv = Join-Path $OutDir "$config.csv"
    Remove-Item $csv -ErrorAction SilentlyContinue
    Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $config"
    if ($config -like 'vs-*') {
        if (Get-Process devenv -ErrorAction SilentlyContinue) { throw 'Close Visual Studio before the vs-* configurations.' }
        $vsId = & $vswhere -latest -products * -property instanceId
        $vsMajor = (& $vswhere -latest -products * -property installationVersion).Split('.')[0]
        $settings = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\$vsMajor.0_$vsId\settings.json"
        $original = [IO.File]::ReadAllBytes($settings)
        $since = Get-Date
        try {
            $json = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json -AsHashtable
            $json['debugging.xamlHotReload.enableXamlHotReload'] = ($config -eq 'vs-on')
            $json['debugging.xamlHotReload.enableWinui'] = ($config -eq 'vs-on')
            $json['environment.security.trust.locations'] = @(@($json['environment.security.trust.locations']) | Where-Object { $_ }) +
                @(@{ location = "$PSScriptRoot\"; locationType = 'folder'; trustedBy = 'system' })
            Set-Content $settings ($json | ConvertTo-Json -Depth 10)
            # F5 passes no arguments, so the app writes to its default location; pick up the newest CSV.
            $defaultDir = Join-Path ([IO.Path]::GetTempPath()) 'winui-diag-repro'
            $devenv = Start-Process (Join-Path $vsPath 'Common7\IDE\devenv.exe') -ArgumentList "`"$(Join-Path $PSScriptRoot 'ReproApp.sln')`"", '/Command', 'Debug.Start' -PassThru
            $deadline = (Get-Date).AddMinutes(60)
            do {
                Start-Sleep -Seconds 5
                $app = Get-Process ReproApp -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $since }
                $started = $started -or $app
            } while ((-not $started -or $app) -and (Get-Date) -lt $deadline)
            $latest = Get-ChildItem $defaultDir -Filter 'run-*.csv' -ErrorAction SilentlyContinue | Where-Object LastWriteTime -ge $since |
                Sort-Object LastWriteTime | Select-Object -Last 1
            if (-not $latest) { throw "Visual Studio did not produce a result for $config." }
            Copy-Item $latest.FullName $csv
        }
        finally {
            Get-Process devenv -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $since } | ForEach-Object {
                if (-not $_.CloseMainWindow() -or -not $_.WaitForExit(20000)) { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
            }
            [IO.File]::WriteAllBytes($settings, $original)
            $started = $null
        }
    }
    else {
        $go = Join-Path $OutDir "$config.go"
        Remove-Item $go -ErrorAction SilentlyContinue
        if ($config -eq 'env') { $env:ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO = '1' }
        try { $app = Start-Process $exe -ArgumentList '--out', "`"$csv`"", '--go', "`"$go`"", '--rounds', $Rounds, '--navs', $Navs, '--churn', $Churn -PassThru }
        finally { Remove-Item env:ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 4
        if ($config -like 'tap*') {
            $udk = $app.Modules | Where-Object ModuleName -eq 'Microsoft.Internal.FrameworkUdk.dll' | Select-Object -First 1 -ExpandProperty FileName
            $init = if ($config -eq 'tap-advise') { 'advise=1' } else { 'advise=0' }
            $hr = & (Join-Path $bin 'Inject.exe') $app.Id (Join-Path $bin 'ReproTap.dll') $udk $init
            if ([int]$hr -ne 0) { throw "InitializeXamlDiagnosticsEx failed: $hr" }
            Start-Sleep -Seconds 2
        }
        Set-Content $go ''
        $app.WaitForExit()
    }
    $csvs += $csv
}
Show-Summary $csvs
