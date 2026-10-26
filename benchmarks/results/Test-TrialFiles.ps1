#Requires -Version 7.4
<#
.SYNOPSIS
    Checks benchmark trial files against trial.schema.json.
.DESCRIPTION
    Runs the JSON Schema, then rules a schema cannot express: 'passed' must be true exactly when every
    required check passed, 'result.summary' must match the checks, a tasks trial needs at least one
    required check, and every apps gate is required.
    Exits 1 if any file is invalid. See README.md in this folder.
.EXAMPLE
    pwsh benchmarks\results\Test-TrialFiles.ps1 -Path benchmarks\results\examples
.EXAMPLE
    pwsh benchmarks\results\Test-TrialFiles.ps1 -Path v0.7.1\winforms-msix__cli-only__sonnet-5.5__1.json
#>
[CmdletBinding()]
param(
    # Trial files, or folders searched (recursively) for *.json.
    [Parameter(Mandatory, Position = 0)][string[]]$Path,
    [string]$SchemaPath = (Join-Path $PSScriptRoot 'trial.schema.json')
)

$ErrorActionPreference = 'Stop'

function Get-SemanticErrors {
    param($Trial)
    $errors = [System.Collections.Generic.List[string]]::new()
    if ($Trial.status -ne 'completed') { return $errors }

    # Normalise both benchmarks to (required, ok) pairs; apps checks with status 'na' do not count.
    if ($Trial.benchmark -eq 'tasks') {
        $items = @($Trial.result.checks | ForEach-Object { [pscustomobject]@{ Required = [bool]$_.required; Ok = [bool]$_.passed } })
    }
    else {
        $all = @($Trial.result.gates) + @($Trial.result.qualityChecks) + @($Trial.result.requirements)
        $items = @($all | Where-Object { $_.status -ne 'na' } | ForEach-Object { [pscustomobject]@{ Required = [bool]$_.required; Ok = $_.status -eq 'pass' } })
        $gates = @($Trial.result.gates)
        foreach ($need in 'builds', 'launches') {
            $g = $gates | Where-Object id -eq $need
            if (-not $g) { $errors.Add("apps: gates must include '$need'.") }
            elseif (-not $g.required) { $errors.Add("apps: gate '$need' must be required.") }
        }
        foreach ($g in ($gates | Where-Object { -not $_.required })) { $errors.Add("apps: gate '$($g.id)' must be required.") }
    }

    $required = @($items | Where-Object Required)
    $improvements = @($items | Where-Object { -not $_.Required })
    if ($Trial.benchmark -eq 'tasks' -and -not $required) { $errors.Add('tasks: no check is marked required, so pass/fail is undefined.') }

    $expected = -not @($required | Where-Object { -not $_.Ok })
    if ($Trial.passed -ne $expected) {
        $errors.Add("passed is $($Trial.passed) but the required checks say $expected (passed = every required check passed).")
    }

    $want = [ordered]@{
        requiredMet       = @($required | Where-Object Ok).Count
        requiredTotal     = $required.Count
        improvementsMet   = @($improvements | Where-Object Ok).Count
        improvementsTotal = $improvements.Count
    }
    foreach ($k in $want.Keys) {
        if ($Trial.result.summary.$k -ne $want[$k]) {
            $errors.Add("result.summary.$k is $($Trial.result.summary.$k) but the checks give $($want[$k]).")
        }
    }
    return $errors
}

$files = foreach ($p in $Path) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Path not found: $p" }
    $item = Get-Item -LiteralPath $p
    if ($item.PSIsContainer) { Get-ChildItem -LiteralPath $p -Filter *.json -File -Recurse } else { $item }
}
if (-not $files) { throw "No .json files found under: $($Path -join ', ')" }

$bad = 0
foreach ($file in $files) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $problems = [System.Collections.Generic.List[string]]::new()

    $schemaErrors = @()
    $valid = Test-Json -Json $text -SchemaFile $SchemaPath -ErrorAction SilentlyContinue -ErrorVariable schemaErrors
    if (-not $valid) {
        foreach ($e in $schemaErrors) { $problems.Add($e.Exception.Message) }
        if (-not $problems.Count) { $problems.Add('Does not match the schema.') }
    }
    else {
        foreach ($e in (Get-SemanticErrors -Trial ($text | ConvertFrom-Json -Depth 20))) { $problems.Add($e) }
    }

    if ($problems.Count) {
        $bad++
        Write-Host "FAIL $($file.FullName)" -ForegroundColor Red
        foreach ($pr in $problems) { Write-Host "     $pr" }
    }
    else { Write-Host "ok   $($file.FullName)" -ForegroundColor Green }
}

Write-Host "$($files.Count - $bad) of $($files.Count) trial file(s) valid."
if ($bad) { exit 1 }
