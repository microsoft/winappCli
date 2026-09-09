#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Regression test for the WinUI analyzer's graceful self-deactivation contract.
.DESCRIPTION
    The package's .targets stands the analyzer down when a consumer (or a future
    Windows App SDK that bundles these analyzers) sets
    WindowsAppSDKProvidesWinUIAnalyzer=true: it drops the analyzer from @(Analyzer)
    and skips its XAML AdditionalFiles target, so a project referencing both never
    sees duplicate WUIxxxx diagnostics.

    This test imports the REAL .targets into a throwaway project with a fake
    @(Analyzer)/@(Page) set and exercises both branches via `dotnet msbuild` (no
    NuGet restore, no compiler). It fails the build if either branch regresses.

    The xUnit suite runs Roslyn in-memory and cannot cover MSBuild targets, so this
    guards the contract that lives entirely in the .targets file.
.EXITCODE
    0 on success; 1 on any contract violation or setup failure.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$AnalyzerName = 'Microsoft.WindowsAppSDK.Analyzers'
$TargetsPath = Join-Path $PSScriptRoot "..\Microsoft.WindowsAppSDK.Analyzers\Microsoft.WindowsAppSDK.Analyzers.targets"
$TargetsPath = (Resolve-Path $TargetsPath).Path

$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "winui-analyzer-standdown-$PID"
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

# CoreCompile writes the surviving @(Analyzer)/@(AdditionalFiles) to a file, so the
# assertions do not depend on console verbosity or message formatting.
$ProjContent = @"
<Project>
  <ItemGroup>
    <Analyzer Include="C:\fake\$AnalyzerName.dll" />
    <Analyzer Include="C:\fake\SomeOther.Analyzer.dll" />
    <Page Include="MainPage.xaml" />
    <ApplicationDefinition Include="App.xaml" />
  </ItemGroup>
  <Import Project="$TargetsPath" />
  <Target Name="CoreCompile">
    <WriteLinesToFile File="`$(ResultFile)" Overwrite="true"
      Lines="@(Analyzer->'ANALYZER=%(Filename)');@(AdditionalFiles->'ADDFILE=%(Identity)')" />
  </Target>
</Project>
"@
$ProjPath = Join-Path $TempDir "test.proj"
Set-Content -Path $ProjPath -Value $ProjContent -Encoding UTF8

$Failures = @()

function Invoke-Branch([string]$Label, [string[]]$ExtraArgs) {
    $resultFile = Join-Path $TempDir "result-$Label.txt"
    $msbuildArgs = @($ProjPath, '/t:CoreCompile', '/nologo', '/v:quiet', "/p:ResultFile=$resultFile") + $ExtraArgs
    & dotnet msbuild @msbuildArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "msbuild failed for branch '$Label' (exit $LASTEXITCODE)" }
    if (-not (Test-Path $resultFile)) { throw "branch '$Label' produced no result file" }
    return Get-Content $resultFile
}

try {
    # Branch 1 — property unset: analyzer stays, XAML gets injected.
    $off = Invoke-Branch 'off' @()
    if ($off -notcontains "ANALYZER=$AnalyzerName") { $Failures += "Default build removed the analyzer (expected it present)." }
    if ($off -notcontains 'ADDFILE=MainPage.xaml')  { $Failures += "Default build skipped XAML injection (expected MainPage.xaml)." }

    # Branch 2 — property=true: analyzer removed, XAML skipped, other analyzers untouched.
    $on = Invoke-Branch 'on' @('/p:WindowsAppSDKProvidesWinUIAnalyzer=true')
    if ($on -contains "ANALYZER=$AnalyzerName")     { $Failures += "Stand-down did NOT remove the analyzer (WindowsAppSDKProvidesWinUIAnalyzer=true)." }
    if ($on -notcontains 'ANALYZER=SomeOther.Analyzer') { $Failures += "Stand-down wrongly removed an unrelated analyzer." }
    if ($on -contains 'ADDFILE=MainPage.xaml')      { $Failures += "Stand-down did NOT skip XAML injection." }
}
finally {
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}

if ($Failures.Count -gt 0) {
    Write-Host "[FAIL] WinUI analyzer stand-down contract:" -ForegroundColor Red
    $Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "[PASS] WinUI analyzer stand-down contract (both branches verified)." -ForegroundColor Green
exit 0
