<#
.SYNOPSIS
  Validates benchmark trial result files against benchmarks\results.schema.json.
.DESCRIPTION
  Beyond the schema, checks that each file agrees with itself and with where it is stored:
  `passed` matches the required checks, `improvements` matches the improvement checks,
  and the file is named <scenario>__<arm>__<model>__<trial>.json inside a folder named
  after its release.
.EXAMPLE
  pwsh benchmarks\validate-results.ps1 -Path .\v0.7.1
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$Schema = (Join-Path $PSScriptRoot 'results.schema.json')
)

$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $Path -Filter *.json -File -Recurse)
if (-not $files) { throw "No .json files under $Path" }

$schemaText = Get-Content -LiteralPath $Schema -Raw
$problems = [System.Collections.Generic.List[string]]::new()

foreach ($file in $files) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $fail = { param($m) $problems.Add("$($file.FullName): $m") }

    $schemaErrors = $null
    if (-not (Test-Json -Json $text -Schema $schemaText -ErrorAction SilentlyContinue -ErrorVariable schemaErrors)) {
        & $fail (($schemaErrors | ForEach-Object { $_.Exception.Message }) -join '; ')
        continue
    }

    $r = $text | ConvertFrom-Json
    $required = @($r.checks | Where-Object kind -eq 'required')
    $improvements = @($r.checks | Where-Object kind -eq 'improvement')

    $expectPassed = -not $r.error -and -not @($required | Where-Object { -not $_.passed })
    if ($r.passed -ne $expectPassed) { & $fail "passed is $($r.passed) but the required checks say $expectPassed" }

    $met = @($improvements | Where-Object passed).Count
    if ($r.improvements.met -ne $met -or $r.improvements.total -ne $improvements.Count) {
        & $fail "improvements is $($r.improvements.met)/$($r.improvements.total) but the checks say $met/$($improvements.Count)"
    }

    $ids = @($r.checks.id)
    if ($ids.Count -ne @($ids | Select-Object -Unique).Count) { & $fail 'check ids are not unique' }

    $expectName = '{0}__{1}__{2}__{3}.json' -f $r.scenario, $r.arm, $r.model, $r.trial
    if ($file.Name -ne $expectName) { & $fail "file name should be $expectName" }
    if ($file.Directory.Name -ne $r.release) { & $fail "folder should be named $($r.release)" }
}

if ($problems) { $problems | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Host "OK: $($files.Count) result file(s) valid."
