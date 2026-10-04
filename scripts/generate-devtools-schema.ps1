#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate DevTools registry/dispatch agreement and generate the standalone engine schema.
.PARAMETER Check
    Compare the existing output without modifying it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutFile,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $repoRoot 'src\winapp-devtools\native\WinApp.DevTools.Native'
$registryPath = Join-Path $nativeDir 'DevToolsProtocolSchema.inc'
$tapPath = Join-Path $nativeDir 'DevToolsTap.cpp'
$eventsPath = Join-Path $nativeDir 'DevToolsEvents.cpp'
$errorPath = Join-Path $nativeDir 'DevToolsProtocol.h'
$provider = $null
$drive = $null
$schemaPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile, [ref]$provider, [ref]$drive)
if ($provider.Name -ne 'FileSystem') { throw 'OutFile must be a filesystem path.' }

function Fail-Gate([System.Collections.Generic.List[string]]$Failures) {
    Write-Host 'DevTools agreement gate FAILED:' -ForegroundColor Red
    foreach ($failure in $Failures) {
        Write-Host "  $failure" -ForegroundColor Red
    }
    exit 1
}

$registry = [System.IO.File]::ReadAllText($registryPath)
$types = [ordered]@{}
$methods = [System.Collections.Generic.List[object]]::new()
$events = [System.Collections.Generic.List[object]]::new()
$errors = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($line in $registry -split "`r?`n") {
    if ($line -match '^\s*WINAPP_DEVTOOLS_PROTOCOL_TYPE\(\s*(\w+)\s*,\s*R"devtools\((.*)\)devtools"\s*\)\s*$') {
        $name = $Matches[1]
        if ($types.Contains($name)) { $failures.Add("Duplicate schema type: $name") }
        try { $types[$name] = $Matches[2] | ConvertFrom-Json -Depth 100 }
        catch { $failures.Add("Invalid JSON for schema type ${name}: $($_.Exception.Message)") }
        continue
    }
    if ($line -match '^\s*WINAPP_DEVTOOLS_PROTOCOL_METHOD\(\s*(\w+)\s*,\s*L"([^"]+)"\s*,\s*L"([^"]+)"\s*,\s*DevToolsAccess::(\w+)\s*,\s*DevToolsVisibility::(\w+)\s*,\s*(\w+)\s*,\s*(\w+)\s*\)\s*$') {
        $methods.Add([pscustomobject][ordered]@{
            id = $Matches[1]
            name = $Matches[2]
            capability = $Matches[3]
            access = $Matches[4].ToLowerInvariant()
            visibility = $Matches[5].ToLowerInvariant()
            paramsType = $Matches[6]
            resultType = $Matches[7]
        })
        continue
    }
    if ($line -match '^\s*WINAPP_DEVTOOLS_PROTOCOL_EVENT\(\s*(\w+)\s*,\s*L"([^"]+)"\s*,\s*L"([^"]+)"\s*,\s*(\w+)\s*\)\s*$') {
        $events.Add([pscustomobject][ordered]@{
            id = $Matches[1]
            name = $Matches[2]
            capability = $Matches[3]
            paramsType = $Matches[4]
        })
        continue
    }
    if ($line -match '^\s*WINAPP_DEVTOOLS_PROTOCOL_ERROR\(\s*(\w+)\s*,\s*(-?\d+)\s*,\s*L"([^"]+)"\s*\)\s*$') {
        $errors.Add([pscustomobject][ordered]@{
            name = $Matches[1]
            code = [int]$Matches[2]
            token = $Matches[3]
        })
    }
}

$typeNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($name in $types.Keys) { $null = $typeNames.Add($name) }

if ($types.Count -eq 0) { $failures.Add('No WINAPP_DEVTOOLS_PROTOCOL_TYPE entries were parsed.') }
if ($methods.Count -eq 0) { $failures.Add('No WINAPP_DEVTOOLS_PROTOCOL_METHOD entries were parsed.') }
if ($events.Count -eq 0) { $failures.Add('No WINAPP_DEVTOOLS_PROTOCOL_EVENT entries were parsed.') }
if ($errors.Count -eq 0) { $failures.Add('No WINAPP_DEVTOOLS_PROTOCOL_ERROR entries were parsed.') }

$methodNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($method in $methods) {
    if (-not $methodNames.Add($method.name)) { $failures.Add("Duplicate schema method: $($method.name)") }
    if ($method.access -notin @('read', 'ui', 'mutation')) {
        $failures.Add("Method $($method.name) has an unrecognized DevToolsAccess: $($method.access)")
    }
    if ($method.visibility -notin @('public', 'internal')) {
        $failures.Add("Method $($method.name) has an unrecognized DevToolsVisibility: $($method.visibility)")
    }
}
$eventNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($event in $events) {
    if (-not $eventNames.Add($event.name)) { $failures.Add("Duplicate schema event: $($event.name)") }
}

foreach ($method in $methods) {
    if (-not $typeNames.Contains($method.paramsType)) {
        $failures.Add("Method $($method.name) references missing params type $($method.paramsType)")
    }
    if (-not $typeNames.Contains($method.resultType)) {
        $failures.Add("Method $($method.name) references missing result type $($method.resultType)")
    }
}
foreach ($event in $events) {
    if (-not $typeNames.Contains($event.paramsType)) {
        $failures.Add("Event $($event.name) references missing params type $($event.paramsType)")
    }
}

# HandleRpc is the dispatch implementation. DevTools.cancel is reserved and deliberately outside the compiled
# registry; it has a fixed unsupported response but is never an advertised capability. Every other literal
# `m == L"..."` comparison, public OR CLI-only Internal.*, is a registry entry.
# DevToolsTap.cpp pulls its parts in with #include "DevToolsTap.*.inc"; read them in place.
$tap = [regex]::Replace([System.IO.File]::ReadAllText($tapPath), '(?m)^#include "(DevToolsTap\.\w+\.inc)"\s*$', {
    param($m) [System.IO.File]::ReadAllText((Join-Path $nativeDir $m.Groups[1].Value))
})
$handleStart = $tap.IndexOf('static std::wstring HandleRpc(')
$handleEnd = $tap.IndexOf('static std::wstring HandleRpcLine(', $handleStart + 1)
if ($handleStart -lt 0 -or $handleEnd -lt 0) {
    $failures.Add('Could not locate HandleRpc for dispatch validation.')
}
else {
    $handleRpc = $tap.Substring($handleStart, $handleEnd - $handleStart)
    $dispatched = @([regex]::Matches($handleRpc, 'm\s*==\s*L"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -cne 'DevTools.cancel' })
    $dispatchNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $dispatched) { $null = $dispatchNames.Add($name) }
    $missingDispatch = @($methods.name | Where-Object { -not $dispatchNames.Contains($_) })
    $missingSchema = @($dispatchNames | Where-Object { -not $methodNames.Contains($_) })
    if ($missingDispatch) { $failures.Add("Schema methods not dispatched: $($missingDispatch -join ', ')") }
    if ($missingSchema) { $failures.Add("Dispatched methods missing from schema: $($missingSchema -join ', ')") }
}

# Emit only public methods, matching negotiation. Internal methods remain access-gated in the registry.
$publicMethods = @($methods | Where-Object { $_.visibility -eq 'public' })

# The dispatcher accepts this constraint on every method and refuses unsupported scoped operations.
foreach ($paramType in @($methods.paramsType | Select-Object -Unique)) {
    $type = $types[$paramType]
    if (-not $type.properties) {
        $type | Add-Member -NotePropertyName properties -NotePropertyValue ([pscustomobject]@{})
    }
    $type.properties | Add-Member -NotePropertyName window -NotePropertyValue ([pscustomobject][ordered]@{
        type = 'string'
        pattern = '^[1-9][0-9]*$'
        description = 'Constrain execution to this live process-owned HWND; unsupported scoped operations fail.'
    }) -Force
    $type.properties | Add-Member -NotePropertyName root -NotePropertyValue ([pscustomobject][ordered]@{
        type = 'string'
        pattern = '^[1-9][0-9]*$'
        description = 'Constrain execution to this live opaque visual-tree subtree handle, intersected with window when supplied.'
    }) -Force
}

$emitterText = $tap + "`n" + [System.IO.File]::ReadAllText($eventsPath)
$emittedEvents = @([regex]::Matches($emitterText, 'DevToolsRpcEvent\(L"([^"]+)"') |
    ForEach-Object { $_.Groups[1].Value })
if ($emitterText -match '\\"method\\":\\"VisualTree\.invalidated\\"') {
    $emittedEvents += 'VisualTree.invalidated'
}
$emitterNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($name in $emittedEvents) { $null = $emitterNames.Add($name) }
$missingEventEmitter = @($events.name | Where-Object { -not $emitterNames.Contains($_) })
$missingEventSchema = @($emitterNames | Where-Object { -not $eventNames.Contains($_) })
if ($missingEventEmitter) { $failures.Add("Schema events not emitted: $($missingEventEmitter -join ', ')") }
if ($missingEventSchema) { $failures.Add("Emitted events missing from schema: $($missingEventSchema -join ', ')") }

$implementedErrors = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
$errorSource = [System.IO.File]::ReadAllText($errorPath)
foreach ($match in [regex]::Matches($errorSource, 'constexpr\s+int\s+(\w+)\s*=\s*(-?\d+)')) {
    $implementedErrors[$match.Groups[1].Value] = [int]$match.Groups[2].Value
}
foreach ($protocolError in $errors) {
    if (-not $implementedErrors.ContainsKey($protocolError.name)) {
        $failures.Add("Schema error missing from DevToolsErr: $($protocolError.name)")
    }
    elseif ($implementedErrors[$protocolError.name] -ne $protocolError.code) {
        $failures.Add("Schema error code disagrees for $($protocolError.name): schema=$($protocolError.code), implementation=$($implementedErrors[$protocolError.name])")
    }
}
$errorNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($protocolError in $errors) { $null = $errorNames.Add($protocolError.name) }
foreach ($name in $implementedErrors.Keys) {
    if (-not $errorNames.Contains($name)) { $failures.Add("DevToolsErr missing from schema: $name") }
}

if ($failures.Count -gt 0) { Fail-Gate $failures }

$domainNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$domains = [System.Collections.Generic.List[string]]::new()
foreach ($method in $publicMethods) {
    if ($domainNames.Add($method.capability)) { $domains.Add($method.capability) }
}
$methodSchemas = @($publicMethods | ForEach-Object {
    [ordered]@{
        name = $_.name
        capability = $_.capability
        access = $_.access
        params = [ordered]@{ '$ref' = "#/`$defs/$($_.paramsType)" }
        result = [ordered]@{ '$ref' = "#/`$defs/$($_.resultType)" }
    }
})
$eventSchemas = @($events | ForEach-Object {
    [ordered]@{
        name = $_.name
        capability = $_.capability
        params = [ordered]@{ '$ref' = "#/`$defs/$($_.paramsType)" }
    }
})
$errorSchemas = @($errors | ForEach-Object {
    [ordered]@{ name = $_.name; code = $_.code; token = $_.token }
})
$accessMap = [ordered]@{}
foreach ($method in $publicMethods) { $accessMap[$method.name] = $method.access }

# Emit only types reachable from public methods and events.
function Get-RefNames($node) {
    $refs = [System.Collections.Generic.List[string]]::new()
    function Walk($n) {
        if ($null -eq $n) { return }
        if ($n -is [System.Management.Automation.PSCustomObject]) {
            foreach ($prop in $n.PSObject.Properties) {
                if ($prop.Name -eq '$ref' -and $prop.Value -is [string]) { $refs.Add(($prop.Value -split '/')[-1]) }
                else { Walk $prop.Value }
            }
        }
        elseif ($n -is [System.Collections.IEnumerable] -and $n -isnot [string]) {
            foreach ($item in $n) { Walk $item }
        }
    }
    Walk $node
    return $refs
}
$reachableTypes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$typeQueue = [System.Collections.Generic.Queue[string]]::new()
foreach ($method in $publicMethods) { $typeQueue.Enqueue($method.paramsType); $typeQueue.Enqueue($method.resultType) }
foreach ($event in $events) { $typeQueue.Enqueue($event.paramsType) }
while ($typeQueue.Count -gt 0) {
    $name = $typeQueue.Dequeue()
    if (-not $reachableTypes.Add($name)) { continue }
    if ($types.Contains($name)) {
        foreach ($ref in (Get-RefNames $types[$name])) { $typeQueue.Enqueue($ref) }
    }
}
$publicTypes = [ordered]@{}
foreach ($name in $types.Keys) { if ($reachableTypes.Contains($name)) { $publicTypes[$name] = $types[$name] } }

$schema = [ordered]@{
    '$schema' = 'https://json-schema.org/draft/2020-12/schema'
    schemaVersion = '1.0'
    protocol = [ordered]@{ name = 'winapp-devtools'; version = '1'; transport = 'ndjson-json-rpc-2.0' }
    capabilities = [ordered]@{
        domains = $domains
        methods = @($publicMethods.name)
        events = @($events.name)
        access = $accessMap
    }
    methods = $methodSchemas
    events = $eventSchemas
    errors = $errorSchemas
    '$defs' = $publicTypes
}
$schemaText = (($schema | ConvertTo-Json -Depth 100) -replace "`r`n", "`n") + "`n"

if ($Check) {
    $drift = [System.Collections.Generic.List[string]]::new()
    foreach ($generated in @(
        [pscustomobject]@{ Path = $schemaPath; Text = $schemaText }
    )) {
        if (-not (Test-Path $generated.Path)) {
            $drift.Add("Missing generated file: $($generated.Path)")
            continue
        }
        $committed = ([System.IO.File]::ReadAllText($generated.Path)) -replace "`r`n", "`n"
        if ($committed -ne $generated.Text) {
            $drift.Add("Generated file is out of date: $($generated.Path)")
        }
    }
    if ($drift.Count -gt 0) { Fail-Gate $drift }
    Write-Host 'DevTools agreement gate passed; generated schema is current.' -ForegroundColor Green
    exit 0
}

foreach ($generated in @(
    [pscustomobject]@{ Path = $schemaPath; Text = $schemaText }
)) {
    $current = if (Test-Path $generated.Path) {
        ([System.IO.File]::ReadAllText($generated.Path)) -replace "`r`n", "`n"
    } else {
        $null
    }
    if ($current -eq $generated.Text) {
        Write-Host "Up to date: $($generated.Path)"
        continue
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($generated.Path)) | Out-Null
    [System.IO.File]::WriteAllText($generated.Path, $generated.Text, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Generated $($generated.Path)"
}
Write-Host 'DevTools agreement gate passed.'
