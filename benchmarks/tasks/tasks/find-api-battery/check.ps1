param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'find-api-battery'

function Get-CodeText([string]$Path) {
    # Source without comments, so a member named only in a comment doesn't count.
    $text = Get-Content -Raw -LiteralPath $Path
    return ($text -replace '(?s)/\*.*?\*/', '' -replace '//[^\r\n]*', '')
}

function Invoke-Build([string]$Project) {
    $out = & dotnet build $Project -nologo -v q 2>&1 | Out-String
    $code = $LASTEXITCODE
    $errors = @($out -split "`r?`n" | Where-Object { $_ -match ': error ' } | ForEach-Object { ($_ -replace '\s+\[[^\]]+\]$', '').Trim() } | Select-Object -Unique -First 4)
    return [pscustomobject]@{ Ok = ($code -eq 0); Detail = "dotnet build exit $code$(if ($errors) { '; ' + ($errors -join ' | ') })" }
}

$sources = @(Get-ChildItem -LiteralPath $Workspace -Recurse -File -Include '*.cs', '*.csproj' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$project = @($sources | Where-Object Extension -eq '.csproj' | Sort-Object { $_.Name -ne 'PowerInfo.csproj' }) | Select-Object -First 1
$battery = @($sources | Where-Object Name -eq 'Battery.cs') | Select-Object -First 1
if (-not $project -or -not $battery) {
    [void](Add-Check $r 'builds' $false "project: $([bool]$project); Battery.cs: $([bool]$battery)" -Required)
    Complete-CheckResult -Result $r -OutFile $OutFile
    return
}

$build = Invoke-Build $project.FullName
# The fixture already builds, so building is a guard: it must still hold but isn't progress.
[void](Add-Check $r 'builds' $build.Ok $build.Detail -Guard)

# The method must exist on PowerStatus, be callable with no arguments, and return a value.
$probe = Join-Path $project.DirectoryName 'BenchProbe.g.cs'
Set-Content -LiteralPath $probe -Value 'internal static class BenchProbe { internal static object? Probe() => global::PowerInfo.PowerStatus.GetBatteryPercent(); }' -Encoding utf8NoBOM
try { $probeBuild = Invoke-Build $project.FullName } finally { Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue }
[void](Add-Check $r 'method-callable' ($build.Ok -and $probeBuild.Ok) "PowerInfo.PowerStatus.GetBatteryPercent() called from a probe file: $($probeBuild.Detail)" -Required)

$code = Get-CodeText $battery.FullName
$all = (@($sources | Where-Object Extension -eq '.cs' | ForEach-Object { Get-CodeText $_.FullName }) -join "`n")
$usesBattery = $code -cmatch '\bAggregateBattery\b|\bFromIdAsync\s*\(' -and $code -cmatch '\bGetReport\s*\(' -and $all -cmatch '\bWindows\.Devices\.Power\b'
[void](Add-Check $r 'battery-api' $usesBattery 'Battery.cs gets a report from Windows.Devices.Power.Battery (AggregateBattery or FromIdAsync, then GetReport())' -Required)

$remaining = $code -cmatch '\bRemainingCapacityInMilliwattHours\b'
$full = $code -cmatch '\bFullChargeCapacityInMilliwattHours\b'
$divides = $code -match '[^/*]/[^/*=]'
[void](Add-Check $r 'percent-from-capacity' ($remaining -and $full -and $divides) "RemainingCapacityInMilliwattHours: $remaining; FullChargeCapacityInMilliwattHours: $full; division: $divides" -Required)

# Guard: no hand-written type or member that stands in for the real API and makes the code compile.
$stubDecl = [regex]::Matches($all, '\b(class|struct|record|interface)\s+(Battery|BatteryReport)\b|\b(?!return\b|await\b|new\b)[\w<>?\[\],]+\s+(AggregateBattery|GetReport|RemainingCapacityInMilliwattHours|FullChargeCapacityInMilliwattHours)\s*(\(|\{|=>)')
[void](Add-Check $r 'no-stand-ins' (-not $stubDecl.Count) ($(if ($stubDecl.Count) { "declares: $(@($stubDecl | ForEach-Object Value) -join ', ')" } else { 'no workspace type or member imitates the battery API' })) -Guard)

$tfm = (& dotnet msbuild $project.FullName -nologo -getProperty:TargetFramework -getProperty:TargetFrameworks 2>$null | Out-String)
[void](Add-Check $r 'windows-sdk-tfm' ($tfm -match 'net\d+\.\d+-windows10\.') "evaluated: $(($tfm -replace '\s+', ' ').Trim())")

$r.facts.project = $project.FullName
Complete-CheckResult -Result $r -OutFile $OutFile
