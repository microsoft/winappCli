# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Build the standalone native inspector and managed binding host.
.DESCRIPTION
  Requires MSVC with the requested C++ toolset, a Windows SDK and .NET 10 SDK.
  Writes an internal engine artifact, not an integrated CLI or release package.
  Run architecture builds serially: generated headers and native outputs are shared.
.EXAMPLE
  .\src\winapp-devtools\build-devtools.ps1 -Arch x64 -Configuration Release -EngineOut .\artifacts\devtools\win-x64
#>
[CmdletBinding()]
param(
    [string]$VcVars,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [string]$EngineOut,
    [switch]$SkipNativeUnitTests,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

function Assert-BuildSucceeded {
    param([Parameter(Mandatory)][string]$What)
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

if ($SelfTest) {
    & $env:ComSpec /c 'exit 19'
    $observed = $false
    try { Assert-BuildSucceeded 'negative control' }
    catch { $observed = $_.Exception.Message -match 'exit code 19' }
    if (-not $observed) { throw 'Build failure control did not fail.' }
    & $env:ComSpec /c 'exit 0'
    Assert-BuildSucceeded 'positive control'
    Write-Host 'Build exit-code self-test passed.'
    exit 0
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$nativeDir = Join-Path $PSScriptRoot 'native\WinApp.DevTools.Native'
if (-not $EngineOut) { $EngineOut = Join-Path $repoRoot "artifacts\devtools\win-$Arch" }
$provider = $null
$drive = $null
$EngineOut = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EngineOut, [ref]$provider, [ref]$drive)
if ($provider.Name -ne 'FileSystem') { throw 'EngineOut must be a filesystem path.' }
New-Item -ItemType Directory -Path $EngineOut -Force | Out-Null
$vcVarsFile = if ($Arch -eq 'arm64') { 'vcvarsamd64_arm64.bat' } else { 'vcvars64.bat' }
$component = if ($Arch -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
if (-not $VcVars) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'MSVC discovery requires vswhere; specify -VcVars.' }
    $vsPath = & $vswhere -latest -products * -requires $component -property installationPath
    if (-not $vsPath) { throw "No Visual Studio installation has $component." }
    $VcVars = Join-Path $vsPath "VC\Auxiliary\Build\$vcVarsFile"
}
$VcVars = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($VcVars, [ref]$provider, [ref]$drive)
if ($provider.Name -ne 'FileSystem') { throw 'VcVars must be a filesystem path.' }
if (-not (Test-Path $VcVars)) { throw "Compiler environment not found: $VcVars" }

function Ensure-CppWinRTProjection {
    param([string]$NativeDir)
    $projDir = Join-Path $NativeDir 'projection'
    dotnet restore (Join-Path $projDir 'WinApp.DevTools.Native.Projection.csproj') --nologo | Out-Host
    Assert-BuildSucceeded 'Projection restore'
    $assets = Get-Content (Join-Path $projDir 'obj\project.assets.json') -Raw | ConvertFrom-Json
    $pkgRoot = $assets.project.restore.packagesPath
    if (-not $pkgRoot) { throw 'Projection restore did not report its package cache.' }
    function Get-PkgVersion([string]$name) {
        $entry = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like "$name/*" })
        if ($entry.Count -ne 1) { throw "Expected one resolved version of $name." }
        return ($entry[0] -split '/')[1]
    }
    $winuiVer = Get-PkgVersion 'Microsoft.WindowsAppSDK.WinUI'
    $foundVer = Get-PkgVersion 'Microsoft.WindowsAppSDK.Foundation'
    $ieVer = Get-PkgVersion 'Microsoft.WindowsAppSDK.InteractiveExperiences'
    $wv2Ver = Get-PkgVersion 'Microsoft.Web.WebView2'
    $cppwVer = Get-PkgVersion 'Microsoft.Windows.CppWinRT'
    $muxDir = Join-Path $pkgRoot "microsoft.windowsappsdk.winui\$winuiVer\metadata"
    $foundDir = Join-Path $pkgRoot "microsoft.windowsappsdk.foundation\$foundVer\metadata"
    $ieRoot = Join-Path $pkgRoot "microsoft.windowsappsdk.interactiveexperiences\$ieVer\metadata"
    $ieDir = Get-ChildItem $ieRoot -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'Microsoft.UI.winmd') } |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    $wv2Winmd = Join-Path $pkgRoot "microsoft.web.webview2\$wv2Ver\lib\Microsoft.Web.WebView2.Core.winmd"
    $cppwinrt = Join-Path $pkgRoot "microsoft.windows.cppwinrt\$cppwVer\bin\cppwinrt.exe"
    foreach ($inputPath in @($muxDir, $foundDir, $ieDir, $wv2Winmd, $cppwinrt)) {
        if (-not $inputPath -or -not (Test-Path $inputPath)) { throw "Missing projection input: $inputPath" }
    }
    $unionRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
    $sdkVer = Get-ChildItem $unionRoot -Directory | Where-Object { $_.Name -match '^10\.' } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
    if (-not $sdkVer) { throw 'Windows SDK UnionMetadata not found.' }
    $genDir = Join-Path $NativeDir 'generated'
    $stampFile = Join-Path $genDir '.projection-stamp'
    $stamp = "winui=$winuiVer;found=$foundVer;ie=$ieVer;wv2=$wv2Ver;cppw=$cppwVer;sdk=$sdkVer;shape=mux+winfoundation"
    Write-Host "Projection inputs: $stamp"
    $required = @('winrt\Microsoft.UI.Xaml.Controls.h', 'winrt\Windows.Foundation.Collections.h', 'winrt\base.h')
    if ((Test-Path $stampFile) -and ((Get-Content $stampFile -Raw).Trim() -eq $stamp) -and
        -not @($required | Where-Object { -not (Test-Path (Join-Path $genDir $_)) }).Count) {
        return $genDir
    }
    if (Test-Path $genDir) { Remove-Item -LiteralPath $genDir -Recurse -Force }
    New-Item -ItemType Directory -Path $genDir -Force | Out-Null
    # Generate base.h with the same tool: SDK and generated projection stamps must match.
    & $cppwinrt -in $muxDir -in $ieDir -in $foundDir -in $wv2Winmd -in $sdkVer -ref $sdkVer `
        -include Microsoft -include Windows.Foundation -out $genDir | Out-Host
    Assert-BuildSucceeded 'C++/WinRT projection'
    & $cppwinrt -base -out $genDir | Out-Host
    Assert-BuildSucceeded 'C++/WinRT base'
    foreach ($header in $required) {
        if (-not (Test-Path (Join-Path $genDir $header))) { throw "Generated header missing: $header" }
    }
    Set-Content $stampFile $stamp -NoNewline
    return $genDir
}

$schemaPath = Join-Path $EngineOut 'winapp-devtools-schema.json'
& pwsh -NoProfile -File (Join-Path $repoRoot 'scripts\generate-devtools-schema.ps1') -OutFile $schemaPath
Assert-BuildSucceeded 'DevTools agreement and generation'
& (Join-Path $PSScriptRoot 'gen-xaml-resources.ps1') -SelfTest
& (Join-Path $PSScriptRoot 'gen-xaml-resources.ps1')
& (Join-Path $PSScriptRoot 'gen-xaml-resources.ps1') `
    -XamlDir (Join-Path $nativeDir 'xaml-window') -OutFile (Join-Path $nativeDir 'DevToolsWindowXaml.g.h')
$projectionInclude = Ensure-CppWinRTProjection -NativeDir $nativeDir
$optFlags = if ($Configuration -eq 'Release') { '/O2 /DNDEBUG' } else { '/Od' }
$sources = @(
    'DevToolsTap.cpp', 'DevToolsSurface.cpp', 'DevToolsProtocol.cpp', 'DevToolsProtocolSchema.cpp', 'DevToolsTrust.cpp',
    'DevToolsSourcePath.cpp', 'DevToolsEvents.cpp', 'DevToolsOverlay.cpp', 'DevToolsRead.cpp', 'DevToolsWindow.cpp',
    'DevToolsAppXaml.cpp', 'DevToolsBinding.cpp', 'DevToolsBindingAnswer.cpp', 'DevToolsBindingInstall.cpp',
    'DevToolsPathProbe.cpp', 'DevToolsPathProbeRead.cpp', 'DevToolsPathWalk.cpp', 'DevToolsPathSyntax.cpp',
    'DevToolsBindingRelay.cpp', 'DevToolsBindingRow.cpp', 'DevToolsAuthored.cpp', 'DevToolsTreeLayout.cpp',
    'DevToolsCrash.cpp', 'DevToolsPerf.cpp', 'DevToolsUiDispatch.cpp'
)
$flags = "/nologo /MP /std:c++20 /W3 /WX /MT /EHsc /Zi /DUNICODE $optFlags /I`"$projectionInclude`""
$libs = 'ole32.lib oleaut32.lib uuid.lib runtimeobject.lib advapi32.lib user32.lib gdi32.lib shell32.lib dbghelp.lib dwmapi.lib winmm.lib WindowsApp.lib'
$cl = "cl $flags /LD $($sources -join ' ') DevToolsTap.def /Fe:WinApp.DevTools.Native.dll /Fd:WinApp.DevTools.Native.pdb /link /DEBUG /PDB:WinApp.DevTools.Native.pdb $libs"
Write-Host "Building win-$Arch with $VcVars"
& $env:ComSpec /c "call `"$VcVars`" >nul && cd /d `"$nativeDir`" && $cl"
Assert-BuildSucceeded 'Native inspector build'
foreach ($name in @('WinApp.DevTools.Native.dll', 'WinApp.DevTools.Native.pdb')) {
    Copy-Item (Join-Path $nativeDir $name) (Join-Path $EngineOut $name) -Force
}

$hostArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if (-not $SkipNativeUnitTests -and $Arch -eq $hostArch) {
    & pwsh -NoProfile -File (Join-Path $repoRoot 'scripts\test-native-units.ps1') -VcVars $VcVars
    Assert-BuildSucceeded 'Native unit tests'
    & pwsh -NoProfile -File (Join-Path $repoRoot 'scripts\test-native-binding-semantics.ps1') `
        -VcVars $VcVars -OutDir (Join-Path $PSScriptRoot 'test\bin\binding-semantics') -EditorTests -OverlayTests
    Assert-BuildSucceeded 'Native binding semantics and editor tests'
    $testDir = Join-Path $PSScriptRoot 'test'
    $fixture = Join-Path $testDir 'bin\binding-transfer-fixture.dll'
    $fixtureCl = "cl $flags /LD /DWINAPP_DEVTOOLS_BINDING_FIXTURE_DLL /I`"$nativeDir`" `"$testDir\binding-transfer-fixture.cpp`" DevToolsBindingRelay.obj DevToolsProtocol.obj DevToolsProtocolSchema.obj /Fe:`"$fixture`" /Fo:`"$testDir\bin\binding-transfer-fixture.obj`" /Fd:`"$testDir\bin\binding-transfer-fixture.pdb`" /link $libs"
    & $env:ComSpec /c "call `"$VcVars`" >nul && cd /d `"$nativeDir`" && $fixtureCl"
    Assert-BuildSucceeded 'Binding transport test fixture build'
    $testExe = Join-Path $testDir 'bin\native-runtime-tests.exe'
    if (Test-Path $testExe) { Remove-Item -LiteralPath $testExe -Force }
    # The harness includes the real dispatcher; link the remaining runtime objects without another tap.
    $objects = ($sources | Where-Object { $_ -notin @('DevToolsTap.cpp', 'DevToolsOverlay.cpp') } | ForEach-Object { $_ -replace '\.cpp$', '.obj' }) -join ' '
    $testCl = "cl $flags `"$testDir\native-runtime-tests.cpp`" `"$testDir\overlay-comment-fixture.cpp`" $objects /Fe:`"$testExe`" /Fo:`"$testDir\bin\\`" /Fd:`"$testDir\bin\native-runtime-tests.pdb`" /link $libs"
    & $env:ComSpec /c "call `"$VcVars`" >nul && cd /d `"$nativeDir`" && $testCl"
    Assert-BuildSucceeded 'Native dispatcher test build'
    & $testExe
    $dispatcherTestExit = $LASTEXITCODE
    $windowTestExe = Join-Path $testDir 'bin\window-runtime-tests.exe'
    $windowObjects = ($sources | Where-Object { $_ -ne 'DevToolsWindow.cpp' } | ForEach-Object { $_ -replace '\.cpp$', '.obj' }) -join ' '
    $windowTestCl = "cl $flags `"$testDir\window-runtime-tests.cpp`" $windowObjects /Fe:`"$windowTestExe`" /Fo:`"$testDir\bin\window-runtime-tests.obj`" /Fd:`"$testDir\bin\window-runtime-tests.pdb`" /link $libs"
    & $env:ComSpec /c "call `"$VcVars`" >nul && cd /d `"$nativeDir`" && $windowTestCl"
    Assert-BuildSucceeded 'Native window test build'
    & $windowTestExe
    Assert-BuildSucceeded 'Native window tests'
    if ($dispatcherTestExit -ne 0) { throw "Native dispatcher tests failed (exit code $dispatcherTestExit)." }
} else {
    Write-Host "SKIPPED native execution: target=$Arch host=$hostArch explicitSkip=$SkipNativeUnitTests"
}

$agentProject = Join-Path $PSScriptRoot 'agent\WinApp.DevTools.Managed\WinApp.DevTools.Managed.csproj'
dotnet build $agentProject -c $Configuration --nologo
Assert-BuildSucceeded 'Managed binding host'
$agent = Join-Path $PSScriptRoot "agent\WinApp.DevTools.Managed\bin\$Configuration\net8.0-windows10.0.19041.0\win-x64\WinApp.DevTools.Managed.dll"
Copy-Item $agent (Join-Path $EngineOut 'WinApp.DevTools.Managed.dll') -Force
Import-Module (Join-Path $repoRoot 'scripts\DevToolsEngine.psm1') -Force
$missing = Get-MissingDevToolsEnginePayload -Directory $EngineOut
if ($missing) { throw "Missing staged engine files: $($missing -join ', ')" }
Write-Host "Standalone engine staged: $EngineOut"
