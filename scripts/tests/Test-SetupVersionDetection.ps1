#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SkillPath = (Join-Path $PSScriptRoot '..\..\plugins\winui\agent-plugin\skills\winui-setup\SKILL.md')
)

$ErrorActionPreference = 'Stop'
$text = Get-Content -LiteralPath $SkillPath -Raw
$match = [regex]::Match($text, '(?s)```powershell\r?\n(\$minimumDotNet.*?)```')
if (-not $match.Success) { throw 'Setup prerequisite detection block not found.' }
$check = [scriptblock]::Create($match.Groups[1].Value)

function dotnet { '10.0.100 [C:\test-sdk]' }
function winapp { $script:versionOutput }
function Get-ItemProperty { @{ AllowDevelopmentWithoutDevLicense = 1 } }

$cases = @(
    @{ Text = '0.6.3-prerelease.39'; Expected = $false }
    @{ Text = '0.6.9'; Expected = $false }
    @{ Text = '0.7.0'; Expected = $true }
    @{ Text = 'v0.7.0+build.123'; Expected = $true }
    @{ Text = '0.7.0-preview.1'; Expected = $false }
    @{ Text = '0.8.0'; Expected = $true }
    @{ Text = 'Update available: 0.7.0'; Expected = $false }
    @{ Text = "Update available: 0.8.0`n0.6.3"; Expected = $false }
)

foreach ($case in $cases) {
    $script:versionOutput = $case.Text -split "`n"
    . $check
    if ($winappOk -ne $case.Expected) {
        throw "Unexpected setup result for '$($case.Text)': $winappOk, expected $($case.Expected)."
    }
}
Write-Host "Passed $($cases.Count) setup version-detection cases."
