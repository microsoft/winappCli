# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$VcVars,
    [switch]$CompileShipping,
    [switch]$EditorTests,
    [switch]$OverlayTests
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$src = Join-Path $root 'src\winapp-devtools\native\WinApp.DevTools.Native'
$test = Join-Path $root 'src\winapp-devtools\test'
$projection = Join-Path $src 'generated'
if (-not (Test-Path "$projection\winrt\impl\Microsoft.UI.Xaml.Data.0.h")) {
    throw 'Requires the existing native SDK projection; this test does not restore or generate it.'
}
if (-not $VcVars) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $VcVars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$exe = Join-Path $OutDir 'native-binding-semantics-tests.exe'
$sources = @(
    "$test\native-binding-semantics-tests.cpp",
    "$test\bindinganswer-tests.cpp", "$test\pathwalk-tests.cpp", "$test\pathprobe-tests.cpp",
    "$src\DevToolsBindingAnswer.cpp", "$src\DevToolsRead.cpp",
    "$src\DevToolsPathWalk.cpp", "$src\DevToolsPathProbe.cpp", "$src\DevToolsPathProbeRead.cpp", "$src\DevToolsProtocol.cpp"
)
$args = ($sources | ForEach-Object { "`"$_`"" }) -join ' '
# Deliberately serial and headless: no app, dispatcher, pipe, activation or registration.
$cl = "cl /nologo /std:c++20 /EHsc /W4 /WX /I`"$src`" /I`"$projection`" $args /Fe:`"$exe`" /Fo:`"$OutDir\\`" /link runtimeobject.lib ole32.lib oleaut32.lib"
& $env:ComSpec /c "call `"$VcVars`" >nul && $cl"
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
& $exe
if ($LASTEXITCODE -ne 0) { throw "Native binding semantics failed: $LASTEXITCODE" }
$surfaceExe = Join-Path $OutDir 'surface-lifetime-tests.exe'
$surfaceCompile = "cl /nologo /std:c++20 /EHsc /W3 /WX /DUNICODE /I`"$projection`" `"$test\surface-lifetime-tests.cpp`" /Fe:`"$surfaceExe`" /Fo:`"$OutDir\\`" /link runtimeobject.lib ole32.lib user32.lib WindowsApp.lib"
& $env:ComSpec /c "call `"$VcVars`" >nul && $surfaceCompile"
if ($LASTEXITCODE -ne 0) { throw "Surface lifetime compilation failed: $LASTEXITCODE" }
& $surfaceExe
if ($LASTEXITCODE -ne 0) { throw "Surface lifetime tests failed: $LASTEXITCODE" }
if ($CompileShipping -or $EditorTests -or $OverlayTests) {
    $shipping = @('DevToolsBinding.cpp', 'DevToolsBindingInstall.cpp', 'DevToolsTap.cpp', 'DevToolsWindow.cpp',
                  'DevToolsProtocolSchema.cpp')
    if ($EditorTests -or $OverlayTests) {
        $shipping += @('DevToolsSurface.cpp', 'DevToolsTrust.cpp', 'DevToolsSourcePath.cpp', 'DevToolsEvents.cpp',
            'DevToolsOverlay.cpp', 'DevToolsAppXaml.cpp', 'DevToolsPathSyntax.cpp', 'DevToolsBindingRelay.cpp',
            'DevToolsBindingRow.cpp', 'DevToolsAuthored.cpp', 'DevToolsTreeLayout.cpp', 'DevToolsCrash.cpp',
            'DevToolsPerf.cpp', 'DevToolsUiDispatch.cpp')
    }
    foreach ($unit in $shipping) {
        $compile = "cl /nologo /c /std:c++20 /EHsc /W3 /WX /DUNICODE /I`"$projection`" `"$src\$unit`" /Fo:`"$OutDir\\`""
        & $env:ComSpec /c "call `"$VcVars`" >nul && $compile"
        if ($LASTEXITCODE -ne 0) { throw "$unit compilation failed: $LASTEXITCODE" }
    }
    $harnesses = @()
    if ($EditorTests) { $harnesses += @{ Name = 'binding-editor-semantics-tests'; Included = 'DevToolsWindow.cpp' } }
    if ($OverlayTests) { $harnesses += @{ Name = 'overlay-geometry-semantics-tests'; Included = 'DevToolsOverlay.cpp' } }
    foreach ($harness in $harnesses) {
        $objects = @($shipping | Where-Object { $_ -ne $harness.Included } | ForEach-Object {
            Join-Path $OutDir ($_ -replace '\.cpp$', '.obj')
        })
        $objects += @('DevToolsBindingAnswer', 'DevToolsRead', 'DevToolsPathWalk', 'DevToolsPathProbe',
                      'DevToolsPathProbeRead', 'DevToolsProtocol') | ForEach-Object { Join-Path $OutDir "$_.obj" }
        $quoted = ($objects | ForEach-Object { "`"$_`"" }) -join ' '
        $harnessExe = Join-Path $OutDir "$($harness.Name).exe"
        $compile = "cl /nologo /std:c++20 /EHsc /W3 /WX /DUNICODE /I`"$projection`" `"$test\$($harness.Name).cpp`" $quoted /Fo:`"$OutDir\\`" /Fe:`"$harnessExe`" /link ole32.lib oleaut32.lib uuid.lib runtimeobject.lib advapi32.lib user32.lib gdi32.lib shell32.lib dbghelp.lib dwmapi.lib winmm.lib WindowsApp.lib"
        & $env:ComSpec /c "call `"$VcVars`" >nul && $compile"
        if ($LASTEXITCODE -ne 0) { throw "$($harness.Name) compilation failed: $LASTEXITCODE" }
        & $harnessExe
        if ($LASTEXITCODE -ne 0) { throw "$($harness.Name) failed: $LASTEXITCODE" }
    }
}
