#Requires -Version 7.4
<#
.SYNOPSIS
    Checks benchmark trial files against trial.schema.json.
.DESCRIPTION
    Runs the JSON Schema, then two rules a schema cannot express: 'passed' must agree with the
    per-check results, and a tasks trial needs at least one required check.
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

    if ($Trial.benchmark -eq 'tasks') {
        $required = @($Trial.result.checks | Where-Object { $_.required })
        if (-not $required) { $errors.Add('tasks: no check is marked required, so pass/fail is undefined.') }
        $expected = $required.Count -gt 0 -and -not @($required | Where-Object { -not $_.passed })
        if ($required -and $Trial.passed -ne $expected) {
            $errors.Add("passed is $($Trial.passed) but the required checks say $expected (passed = every required check passed).")
        }
    }
    elseif ($Trial.benchmark -eq 'apps') {
        $expected = -not @($Trial.result.gates | Where-Object { $_.status -eq 'fail' })
        if ($Trial.passed -ne $expected) {
            $errors.Add("passed is $($Trial.passed) but the gates say $expected (passed = no gate failed).")
        }
        $ids = @($Trial.result.gates.id)
        foreach ($need in 'builds', 'launches') {
            if ($need -notin $ids) { $errors.Add("apps: gates must include '$need'.") }
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
