<#
.SYNOPSIS
    Fails when a change adds a package version that nuget.org published too recently for the
    internal feed that the ADO builds use.

.DESCRIPTION
    GitHub Actions restores from nuget.org, but the ADO pipelines restore only from the internal
    pde-oss_Internal feed, which refuses most nuget.org versions until they are about 6.6 days
    old. A version bump, or a new transitive version it pulls in, can pass GitHub Actions and
    then fail every ADO build with NU1102.

    This script restores every project the ADO build restores, once at the base commit and once
    at the current checkout, and checks the nuget.org publish time of every package version the
    change adds. Versions already in the base are skipped, so packs that come from the installed
    .NET SDK rather than the change aren't flagged. It needs no credentials.

.PARAMETER BaseRef
    The commit to compare against, such as the pull request's base.

.PARAMETER MinimumAgeHours
    How old a new version must be. The feed's cutoff is about 160 hours; 168 leaves some margin.

.PARAMETER ServiceIndex
    The NuGet V3 service index to read publish times from.

.EXAMPLE
    .\scripts\test-package-age.ps1 -BaseRef origin/main
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaseRef,
    [double]$MinimumAgeHours = 168,
    [string]$ServiceIndex = 'https://api.nuget.org/v3/index.json'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# Restores the projects in $Root the way the ADO build does and returns the package versions
# they resolve, keyed by lowercase id/version.
function Get-ResolvedPackages([string]$Root, [string]$Work) {
    # winui-crash-app is a manual repro app that no build or test restores.
    $projects = @(git -C $Root ls-files 'src/*.csproj' |
        Where-Object { $_ -notlike '*/winui-crash-app/*' } |
        ForEach-Object { Join-Path $Root $_ })
    if ($projects.Count -eq 0) { throw "No projects found under $Root\src." }

    $packages = @{}
    $collect = {
        foreach ($project in $projects) {
            $assetsPath = Join-Path (Split-Path $project) 'obj\project.assets.json'
            if (-not (Test-Path $assetsPath)) { continue }
            $assets = Get-Content -Raw $assetsPath | ConvertFrom-Json -AsHashtable
            foreach ($key in $assets.libraries.Keys) {
                if ($assets.libraries[$key].type -eq 'package') { $packages[$key.ToLowerInvariant()] = $key }
            }
            # Runtime and ILCompiler packs are downloaded outside the package graph, with exact ranges.
            foreach ($framework in $assets.project.frameworks.Values) {
                foreach ($download in @($framework.downloadDependencies)) {
                    if (-not $download) { continue }
                    $version = $download.version.Trim('[', ']', ' ').Split(',')[0].Trim()
                    $packages["$($download.name)/$version".ToLowerInvariant()] = "$($download.name)/$version"
                }
            }
        }
    }

    $solution = Join-Path $Work 'restore.slnx'
    Set-Content -Path $solution -Value ("<Solution>`n" + (($projects | ForEach-Object { "  <Project Path=`"$_`" />" }) -join "`n") + "`n</Solution>")
    Write-Host "Restoring $($projects.Count) projects in $Root..."
    dotnet restore $solution | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Restore failed in $Root." }
    & $collect

    # The CLI is published self-contained with Native AOT for each architecture, which needs
    # runtime and ILCompiler packs that a restore without a runtime identifier doesn't download.
    $cli = Join-Path $Root 'src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj'
    foreach ($rid in 'win-x64', 'win-arm64') {
        dotnet restore $cli -r $rid | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Restore for $rid failed in $Root." }
        & $collect
    }
    $packages
}

$work = Join-Path ([IO.Path]::GetTempPath()) "winapp-package-age-$PID"
$baseRoot = Join-Path $work 'base'
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    git -C $repoRoot worktree add --detach --quiet $baseRoot $BaseRef
    if ($LASTEXITCODE -ne 0) { throw "Could not check out $BaseRef." }
    $base = Get-ResolvedPackages $baseRoot $work
    $head = Get-ResolvedPackages $repoRoot $work
}
finally {
    git -C $repoRoot worktree remove --force $baseRoot 2>$null
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$added = @($head.GetEnumerator() | Where-Object { -not $base.ContainsKey($_.Key) })
if ($added.Count -eq 0) {
    Write-Host "No package versions added compared with $BaseRef." -ForegroundColor Green
    exit 0
}

$index = Invoke-RestMethod $ServiceIndex -MaximumRetryCount 3
$registrations = ($index.resources | Where-Object { $_.'@type' -eq 'RegistrationsBaseUrl/3.6.0' } | Select-Object -First 1).'@id'
if (-not $registrations) { throw "$ServiceIndex has no RegistrationsBaseUrl/3.6.0 resource." }

Write-Host "Checking when the $($added.Count) package versions added since $BaseRef were published..."
$results = $added | ForEach-Object -ThrottleLimit 16 -Parallel {
    $id, $version = $_.Key -split '/', 2
    $published = $null
    try {
        $leaf = Invoke-RestMethod "$($using:registrations)$id/$version.json" -MaximumRetryCount 3 -RetryIntervalSec 2
        $published = [DateTimeOffset]$leaf.published
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    }
    [pscustomobject]@{ Name = $_.Value; Published = $published }
}

$now = [DateTimeOffset]::UtcNow
foreach ($result in $results | Sort-Object Name) {
    if (-not $result.Published) { Write-Host "  $($result.Name): not on nuget.org, skipped" }
    else { Write-Host "  $($result.Name): published $($result.Published.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"))" }
}

$tooNew = @($results | Where-Object { $_.Published -and ($now - $_.Published).TotalHours -lt $MinimumAgeHours } | Sort-Object Name)
foreach ($result in $tooNew) {
    $id, $version = $result.Name -split '/', 2
    $hours = [int][Math]::Floor(($now - $result.Published).TotalHours)
    $eligible = $result.Published.AddHours($MinimumAgeHours).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")
    $message = "$id $version was published to nuget.org $hours hours ago. The internal feed that the ADO builds " +
        "restore from doesn't serve most versions until they are about 7 days old, so the ADO build would fail " +
        "with NU1102. Keep the previous version until $eligible."
    if ($env:GITHUB_ACTIONS) { Write-Host "::error title=Package too new for the internal feed::$message" } else { Write-Host $message -ForegroundColor Red }
}
if ($tooNew.Count -gt 0) { exit 1 }
Write-Host "All $($results.Count) added package versions were published at least $MinimumAgeHours hours ago." -ForegroundColor Green
