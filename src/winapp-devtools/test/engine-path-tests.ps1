# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
[CmdletBinding()]
param([string]$EngineRoot = (Join-Path $PSScriptRoot '..\..\..\artifacts\devtools'))
$ErrorActionPreference = 'Stop'
$EngineRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EngineRoot)
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$smoke = Join-Path $repo 'scripts\test-devtools-engine.ps1'
$schema = Join-Path $repo 'scripts\generate-devtools-schema.ps1'
$build = Join-Path $repo 'src\winapp-devtools\build-devtools.ps1'
$units = Join-Path $repo 'scripts\test-native-units.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ("winapp-engine-paths-" + [guid]::NewGuid().ToString('N'))
$a = Join-Path $root 'process'
$b = Join-Path $root 'powershell'
$checks = 0
$failures = 0
function Check([bool]$condition, [string]$message) {
    $script:checks++
    if ($condition) { Write-Host "PASS $message" }
    else { $script:failures++; Write-Host "FAIL $message" }
}
function Failure([scriptblock]$action) {
    try { & $action | Out-Host; return '' }
    catch { return $_.Exception.ToString() }
}
$location = Get-Location
$processDirectory = [Environment]::CurrentDirectory
try {
    foreach ($dir in @($a, $b)) {
        foreach ($arch in @('x64', 'arm64')) {
            $target = Join-Path $dir "engine\win-$arch"
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            foreach ($name in @('WinApp.DevTools.Native.dll', 'WinApp.DevTools.Managed.dll', 'winapp-devtools-schema.json')) {
                Copy-Item -LiteralPath (Join-Path $EngineRoot "win-$arch\$name") -Destination $target
            }
            Set-Content -LiteralPath (Join-Path $target 'WinApp.DevTools.Native.pdb') 'presence-only symbol fixture'
        }
    }
    [Environment]::CurrentDirectory = $a
    Set-Location -LiteralPath $b
    Check ((Get-Location).Path -ne [Environment]::CurrentDirectory) 'PS and process directories really differ'
    foreach ($arch in @('x64', 'arm64')) {
        Set-Content -LiteralPath (Join-Path $b "engine\win-$arch\WinApp.DevTools.Native.dll") 'not a PE image'
    }
    $errorText = Failure { & $smoke -EngineRoot 'engine' }
    Write-Host "Corrupt-image refusal: $errorText"
    Check (-not [string]::IsNullOrEmpty($errorText)) 'corrupt PS-selected DLL is refused instead of loading process-directory DLL'
    foreach ($arch in @('x64', 'arm64')) {
        Copy-Item -LiteralPath (Join-Path $EngineRoot "win-$arch\WinApp.DevTools.Native.dll") -Destination (Join-Path $b "engine\win-$arch")
        Set-Content -LiteralPath (Join-Path $a "engine\win-$arch\WinApp.DevTools.Native.dll") 'not a PE image'
    }
    $errorText = Failure { & $smoke -EngineRoot 'engine' }
    Check ([string]::IsNullOrEmpty($errorText)) 'valid PS-selected artifacts work despite corrupt process-directory copies'

    & $schema -OutFile 'new-schema\schema.json'
    Check (Test-Path -LiteralPath (Join-Path $b 'new-schema\schema.json')) 'schema output is created under PS location'
    Check (-not (Test-Path -LiteralPath (Join-Path $a 'new-schema\schema.json'))) 'schema output does not leak into process directory'
    & $schema -OutFile 'new-schema\schema.json' -Check
    Check ($LASTEXITCODE -eq 0) 'schema check uses the same PS-selected output'

    $errorText = Failure { & $build -EngineOut 'new-engine' -VcVars 'absent-vcvars.bat' }
    Check ($errorText.Contains((Join-Path $b 'absent-vcvars.bat'))) 'build resolves relative VcVars before refusal'
    Check (Test-Path -LiteralPath (Join-Path $b 'new-engine')) 'nonexistent EngineOut is created under PS location'
    Check (-not (Test-Path -LiteralPath (Join-Path $a 'new-engine'))) 'EngineOut is not created under process directory'
    $errorText = Failure { & $units -VcVars 'absent-vcvars.bat' }
    Check ($errorText.Contains((Join-Path $b 'absent-vcvars.bat'))) 'unit build resolves relative VcVars before refusal'

    $nonFilesystemCompiler = 'Env:' + [guid]::NewGuid().ToString('N')
    foreach ($action in @(
        { & $smoke -EngineRoot 'Env:TEMP' },
        { & $schema -OutFile 'Env:TEMP' },
        { & $build -EngineOut 'Env:TEMP' -VcVars 'absent-vcvars.bat' },
        { & $build -EngineOut (Join-Path $b 'new-engine') -VcVars $nonFilesystemCompiler },
        { & $units -VcVars $nonFilesystemCompiler }
    )) {
        $errorText = Failure $action
        Check ($errorText -match 'filesystem') 'non-filesystem input gets an explicit filesystem diagnostic'
    }
} finally {
    Set-Location -LiteralPath $location.Path
    [Environment]::CurrentDirectory = $processDirectory
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
Write-Host "Engine path checks=$checks passed=$($checks - $failures) failed=$failures skipped=0"
if ($failures) { throw "$failures engine path checks failed" }
