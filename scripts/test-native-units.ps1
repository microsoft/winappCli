# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Build and run the native unit tests (DevToolsTreeLayout tree logic, DevToolsProtocol JSON reader/schema, DevToolsTrust
  posture/pipe-security, DevToolsSinkBase COM event-sink base).

.DESCRIPTION
  Compiles src/winapp-devtools/test/*-tests.cpp against the REAL shipping headers/sources under
  src/winapp-devtools/native/WinApp.DevTools.Native -- not copies of them -- and runs the result. Exits non-zero if any
  assertion fails.

  Selected tests include deliberately broken negative controls to verify their assertions detect failure.

  Requires MSVC (auto-discovered via vswhere, same discovery as build-devtools.ps1). No Windows App SDK,
  no running app or desktop. Tests use unique pipes and non-UI threads on the build host.

.EXAMPLE
  pwsh scripts/test-native-units.ps1
#>
[CmdletBinding()]
param(
    [string]$VcVars,
    # Source/comment acceptance tests only: file/text/census helpers, no UI, pipes or application process.
    [switch]$AuthoredOnly,
    # Bounded inspector acceptance policy tests; no windows, pipes, or app activation.
    [switch]$InspectorOnly,
    # Compile the accept-loop tests against a reduced listener pool. The bounds the loop has to hold are
    # properties of "more concurrent clients than slots", which at the shipping depth of 32 needs 32
    # simultaneous connections before it can even be approached; a shallow pool reaches saturation cheaply.
    # CI leaves this unset so the gate runs at the shipping depth.
    [ValidateRange(1, 63)]
    [int]$ListenerPoolSize
)
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$srcDir   = Join-Path $repoRoot "src\winapp-devtools\native\WinApp.DevTools.Native"
$testDir  = Join-Path $repoRoot "src\winapp-devtools\test"
$testCpps = @(
    (Join-Path $testDir "treelayout-tests.cpp"),
    (Join-Path $testDir "protocol-tests.cpp"),
    (Join-Path $testDir "trust-tests.cpp"),
    (Join-Path $testDir "crash-tests.cpp"),
    (Join-Path $testDir "sourcepath-tests.cpp"),
    (Join-Path $testDir "sink-tests.cpp"),
    (Join-Path $testDir "toolbarcorner-tests.cpp"),
    (Join-Path $testDir "toolbar-tests.cpp"),
    (Join-Path $testDir "selectionplacement-tests.cpp"),
    (Join-Path $testDir "inspector-acceptance-tests.cpp"),
    (Join-Path $testDir "selectiontracking-tests.cpp"),
    (Join-Path $testDir "binding-relay-tests.cpp"),
    (Join-Path $testDir "binding-transfer-fixture.cpp"),
    (Join-Path $testDir "bindingrow-tests.cpp"),
    (Join-Path $testDir "pathwalk-tests.cpp"),
    (Join-Path $testDir "pathprobe-tests.cpp"),
    (Join-Path $testDir "pathsyntax-tests.cpp"),
    (Join-Path $testDir "read-tests.cpp"),
    (Join-Path $testDir "authored-tests.cpp"),
    (Join-Path $testDir "pickroute-tests.cpp"),
    (Join-Path $testDir "perf-tests.cpp")
    (Join-Path $testDir "uidispatch-tests.cpp")
    (Join-Path $testDir "focus-subscription-tests.cpp"),
    (Join-Path $testDir "overlaystate-tests.cpp"),
    (Join-Path $testDir "ownedstate-tests.cpp"),
    (Join-Path $testDir "resourceoverrides-tests.cpp"),
    (Join-Path $testDir "batch-tests.cpp"),
    (Join-Path $testDir "query-tests.cpp"),
    (Join-Path $testDir "bindinganswer-tests.cpp"),
    (Join-Path $testDir "resourceinline-tests.cpp"),
    (Join-Path $testDir "shellopen-tests.cpp"),
    (Join-Path $testDir "styleedit-tests.cpp"),
    (Join-Path $testDir "pipeaccept-tests.cpp"),
    (Join-Path $testDir "framing-tests.cpp"),
    (Join-Path $testDir "events-tests.cpp")
)
$unitCpps = @(
    (Join-Path $srcDir "DevToolsTreeLayout.cpp"),
    (Join-Path $srcDir "DevToolsProtocol.cpp"),
    (Join-Path $srcDir "DevToolsProtocolSchema.cpp"),
    (Join-Path $srcDir "DevToolsTrust.cpp"),
    (Join-Path $srcDir "DevToolsSourcePath.cpp"),
    (Join-Path $srcDir "DevToolsBindingRelay.cpp"),
    (Join-Path $srcDir "DevToolsBindingRow.cpp"),
    (Join-Path $srcDir "DevToolsPathWalk.cpp"),
    (Join-Path $srcDir "DevToolsPathProbe.cpp"),
    (Join-Path $srcDir "DevToolsPathSyntax.cpp"),
    (Join-Path $srcDir "DevToolsRead.cpp"),
    (Join-Path $srcDir "DevToolsBindingAnswer.cpp"),
    (Join-Path $srcDir "DevToolsAuthored.cpp"),
    (Join-Path $srcDir "DevToolsPerf.cpp")
    (Join-Path $srcDir "DevToolsUiDispatch.cpp")
    (Join-Path $srcDir "DevToolsEvents.cpp")
)
$outDir   = Join-Path $testDir "bin"
if ($InspectorOnly -and $AuthoredOnly) { throw 'Choose only one focused test suite.' }
if ($InspectorOnly) {
    $testCpps = @((Join-Path $testDir "inspector-acceptance-tests.cpp"))
    $unitCpps = @((Join-Path $srcDir "DevToolsProtocol.cpp"))
    $outDir = Join-Path $outDir "inspector"
}
if ($AuthoredOnly) {
    $testCpps = @((Join-Path $testDir "authored-tests.cpp"))
    $unitCpps = @(
        (Join-Path $srcDir "DevToolsAuthored.cpp"),
        (Join-Path $srcDir "DevToolsProtocol.cpp"),
        (Join-Path $srcDir "DevToolsSourcePath.cpp")
    )
    $outDir = Join-Path $outDir "authored"
}

foreach ($p in ($testCpps + $unitCpps)) {
    if (-not (Test-Path $p)) { throw "missing source: $p" }
}

# Same MSVC discovery build-devtools.ps1 uses, so the two stay in step.
if (-not $VcVars) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
        if ($vsPath) { $VcVars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat" }
    }
    if (-not $VcVars) { $VcVars = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat" }
}
$provider = $null
$drive = $null
$VcVars = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($VcVars, [ref]$provider, [ref]$drive)
if ($provider.Name -ne 'FileSystem') { throw 'VcVars must be a filesystem path.' }
if (-not (Test-Path $VcVars)) { throw "vcvars not found: $VcVars (pass -VcVars <path>)" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exe = Join-Path $outDir "native-unit-tests.exe"

# Remove the old binary so a skipped or failed compile cannot execute stale tests.
Remove-Item $exe -Force -ErrorAction SilentlyContinue

if ($InspectorOnly) { Write-Host "==> Building headless inspector acceptance tests..." }
elseif ($AuthoredOnly) { Write-Host "==> Building native source/comment acceptance tests..." }
else { Write-Host "==> Building native unit tests (DevToolsTreeLayout + DevToolsProtocol + DevToolsTrust + DevToolsSourcePath + DevToolsSinkBase + DevToolsToolbarCorner + DevToolsToolbar + DevToolsSelectionPlacement + DevToolsSelectionTracking + DevToolsBindingRelay + DevToolsBindingRow + DevToolsRead)..." }
$sources = (($testCpps + $unitCpps) | ForEach-Object { "`"$_`"" }) -join ' '
$poolDefine = ''
if ($AuthoredOnly) { $poolDefine += ' /DWINAPP_DEVTOOLS_AUTHORED_TESTS_ONLY' }
if ($InspectorOnly) { $poolDefine += ' /DWINAPP_DEVTOOLS_INSPECTOR_TESTS_ONLY' }
if ($PSBoundParameters.ContainsKey('ListenerPoolSize')) {
    $poolDefine += " /DWINAPP_DEVTOOLS_LISTENER_POOL_SIZE=$ListenerPoolSize"
    Write-Host "    listener pool overridden to $ListenerPoolSize"
}
# ole32/uuid are for DevToolsSinkBase; advapi32 is for DevToolsTrust; user32/winmm are for DevToolsPerf's jank sampler.
# No desktop or Windows App SDK is required; the perf suite never arms the sampler.
# /MP compiles the translation units in parallel. They are independent and each has its own /Fo target, so this
# only changes how long the build takes -- it matters because the pool-depth sweep rebuilds the whole suite once
# per depth.
$responseFile = Join-Path $outDir 'native-unit-tests.rsp'
"/nologo /MP /std:c++17 /EHsc /W4 /WX /DWINAPP_DEVTOOLS_EVENTS_FAULT_INJECTION /DWINAPP_DEVTOOLS_BINDING_FAULT_INJECTION$poolDefine /I`"$srcDir`" $sources /Fe:`"$exe`" /Fo:`"$outDir\\`" /link ole32.lib uuid.lib runtimeobject.lib advapi32.lib user32.lib winmm.lib xmllite.lib shell32.lib" |
    Set-Content -LiteralPath $responseFile -Encoding utf8NoBOM
$cl = "cl @`"$responseFile`""
& $env:ComSpec /c "call `"$VcVars`" >nul 2>&1 && $cl"
# $ErrorActionPreference='Stop' does NOT trip on a native tool's exit code, so check it explicitly.
if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $exe)) { throw "compile reported success but produced no binary at $exe" }

Write-Host "==> Running native unit tests..."
Push-Location $repoRoot
try { & $exe } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "native unit tests FAILED (exit $LASTEXITCODE)" }
Write-Host "    native unit tests passed."
