# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Check standalone engine composition, PE metadata and host-architecture native loading.
.EXAMPLE
  .\scripts\test-devtools-engine.ps1 -EngineRoot .\artifacts\devtools
#>
[CmdletBinding()]
param(
    [string]$EngineRoot = (Join-Path $PSScriptRoot '..\artifacts\devtools'),
    [switch]$RuntimeOnly
)
$ErrorActionPreference = 'Stop'
$provider = $null
$drive = $null
$EngineRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EngineRoot, [ref]$provider, [ref]$drive)
if ($provider.Name -ne 'FileSystem') { throw 'EngineRoot must be a filesystem path.' }
Import-Module (Join-Path $PSScriptRoot 'DevToolsEngine.psm1') -Force
$failures = [Collections.Generic.List[string]]::new()
$checks = 0
function Assert-Engine([bool]$Condition, [string]$Message) {
    $script:checks++
    if (-not $Condition) { $failures.Add($Message); Write-Host "FAIL $Message" }
    else { Write-Host "PASS $Message" }
}
$hostArch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
Add-Type -Namespace DevToolsSmoke -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32", SetLastError=true, CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr LoadLibraryW(string path);
[System.Runtime.InteropServices.DllImport("kernel32", SetLastError=true)]
public static extern System.IntPtr GetProcAddress(System.IntPtr module, string name);
[System.Runtime.InteropServices.DllImport("kernel32")]
public static extern bool FreeLibrary(System.IntPtr module);
'@
$skipped = 0
foreach ($arch in @('x64', 'arm64')) {
    $dir = Join-Path $EngineRoot "win-$arch"
    $payload = @(Get-DevToolsEnginePayload)
    if (-not $RuntimeOnly) { $payload += @('WinApp.DevTools.Native.pdb', 'winapp-devtools-schema.json') }
    foreach ($name in $payload) {
        Assert-Engine (Test-Path (Join-Path $dir $name) -PathType Leaf) "win-$arch contains $name"
    }
    $tap = Join-Path $dir 'WinApp.DevTools.Native.dll'
    if (Test-Path $tap) {
        $stream = [IO.File]::OpenRead($tap)
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $expected = if ($arch -eq 'x64') { 'Amd64' } else { 'Arm64' }
            Assert-Engine ($pe.PEHeaders.CoffHeader.Machine.ToString() -eq $expected) "win-$arch native PE machine"
        } finally { $pe.Dispose(); $stream.Dispose() }
        if ($arch -eq $hostArch) {
            $module = [DevToolsSmoke.Native]::LoadLibraryW($tap)
            Assert-Engine ($module -ne [IntPtr]::Zero) "win-$arch LoadLibrary"
            if ($module -ne [IntPtr]::Zero) {
                try {
                    foreach ($export in @('DllGetClassObject', 'DllCanUnloadNow')) {
                        Assert-Engine ([DevToolsSmoke.Native]::GetProcAddress($module, $export) -ne [IntPtr]::Zero) $export
                    }
                } finally { [DevToolsSmoke.Native]::FreeLibrary($module) | Out-Null }
            }
        } else {
            $skipped++
            Write-Host "SKIP win-$arch native execution on $hostArch"
        }
    }
    $agent = Join-Path $dir 'WinApp.DevTools.Managed.dll'
    if (Test-Path $agent) {
        $stream = [IO.File]::OpenRead($agent)
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $machine = $pe.PEHeaders.CoffHeader.Machine.ToString()
            $flags = $pe.PEHeaders.CorHeader.Flags
            Assert-Engine (($machine -in @('I386', 'Unknown')) -and
                ($flags -band [Reflection.PortableExecutable.CorFlags]::ILOnly) -and
                -not ($flags -band [Reflection.PortableExecutable.CorFlags]::Requires32Bit)) "win-$arch agent AnyCPU IL"
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $moduleInitializer = $false
            $types = @()
            foreach ($handle in $metadata.TypeDefinitions) {
                $type = $metadata.GetTypeDefinition($handle)
                $name = $metadata.GetString($type.Name)
                $types += $name
                if ($name -eq '<Module>') {
                    foreach ($methodHandle in $type.GetMethods()) {
                        $method = $metadata.GetMethodDefinition($methodHandle)
                        if ($metadata.GetString($method.Name) -eq '.cctor') { $moduleInitializer = $true }
                    }
                }
            }
            Assert-Engine (-not $moduleInitializer) "win-$arch agent has no eager module initializer"
            Assert-Engine ('StartupHook' -in $types -and 'BindingDiagnosis' -in $types) "win-$arch contains binding host"
            Assert-Engine ('WatchdogSelfExit' -notin $types -and 'XamlReconciler' -notin $types) "win-$arch excludes watcher types"
            $systemRuntime = @($metadata.AssemblyReferences | ForEach-Object {
                $reference = $metadata.GetAssemblyReference($_)
                if ($metadata.GetString($reference.Name) -eq 'System.Runtime') { $reference.Version.Major }
            })
            Assert-Engine ($systemRuntime.Count -eq 1 -and $systemRuntime[0] -eq 8) "win-$arch agent targets System.Runtime 8"
        } finally { $pe.Dispose(); $stream.Dispose() }
    }
    $schemaPath = Join-Path $dir 'winapp-devtools-schema.json'
    if (-not $RuntimeOnly -and (Test-Path $schemaPath)) {
        $schema = Get-Content $schemaPath -Raw | ConvertFrom-Json
        Assert-Engine (@($schema.methods).Count -gt 0 -and @($schema.events).Count -gt 0) "win-$arch compiled public contract shape"
    }
}
$neutralFiles = @('WinApp.DevTools.Managed.dll')
if (-not $RuntimeOnly) { $neutralFiles += 'winapp-devtools-schema.json' }
foreach ($name in $neutralFiles) {
    $left = Join-Path $EngineRoot "win-x64\$name"
    $right = Join-Path $EngineRoot "win-arm64\$name"
    Assert-Engine ((Test-Path $left) -and (Test-Path $right) -and
        (Get-FileHash $left).Hash -eq (Get-FileHash $right).Hash) "architecture-neutral $name is identical"
}
Write-Host "Engine checks=$checks passed=$($checks - $failures.Count) failed=$($failures.Count) execution-skips=$skipped"
if ($failures.Count) { throw ($failures -join "`n") }
