<#
.SYNOPSIS
    Restores every .NET project under src\ from the internal pde-oss_Internal feed only.

.DESCRIPTION
    The ADO pipelines restore from pde-oss_Internal (.pipelines\release-nuget.config), while
    GitHub Actions restores from nuget.org. The internal feed saves new upstream versions only
    some days after nuget.org publishes them, so a version bump can pass GitHub Actions and then
    fail every ADO build with NU1102. This script runs the same restore the ADO build runs, with
    an empty package cache so packages already on this machine can't hide a missing one.

.PARAMETER Token
    An Azure DevOps access token for the feed. Leave empty to rely on an installed
    Azure Artifacts credential provider.

.EXAMPLE
    .\scripts\test-internal-feed-restore.ps1 -Token $token
#>
[CmdletBinding()]
param(
    [string]$Token = $env:INTERNAL_FEED_TOKEN
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) "winapp-feed-restore-$PID"
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    $config = Join-Path $work 'nuget.config'
    Copy-Item (Join-Path $repoRoot '.pipelines\release-nuget.config') $config
    if ($Token) {
        dotnet nuget update source pde-oss_Internal --configfile $config --username token --password $Token --store-password-in-clear-text | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to add the feed credentials.' }
    }

    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    # winui-crash-app is a manual repro app that no build or test restores.
    $projects = @(git -C $repoRoot ls-files 'src/*.csproj' |
        Where-Object { $_ -notlike '*/winui-crash-app/*' } |
        ForEach-Object { Join-Path $repoRoot $_ })
    if ($projects.Count -eq 0) { throw 'No projects found under src\.' }

    $solution = Join-Path $work 'restore.slnx'
    Set-Content -Path $solution -Value ("<Solution>`n" + (($projects | ForEach-Object { "  <Project Path=`"$_`" />" }) -join "`n") + "`n</Solution>")

    $failed = $false
    Write-Host "Restoring $($projects.Count) projects from pde-oss_Internal..."
    dotnet restore $solution --configfile $config
    if ($LASTEXITCODE -ne 0) { $failed = $true }

    # The CLI is published self-contained with Native AOT for each architecture, which needs
    # runtime and ILCompiler packs that a restore without a runtime identifier doesn't download.
    $cli = Join-Path $repoRoot 'src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj'
    foreach ($rid in 'win-x64', 'win-arm64') {
        Write-Host "Restoring the CLI for $rid..."
        dotnet restore $cli -r $rid --configfile $config
        if ($LASTEXITCODE -ne 0) { $failed = $true }
    }

    if ($failed) {
        $message = 'A package is missing from the pde-oss_Internal feed, so the ADO build will fail to restore. ' +
            'For NU1102 on a version that nuget.org already has, the feed has not saved that version yet; ' +
            'it usually does a few days after nuget.org publishes it. Keep the previous version until then.'
        if ($env:GITHUB_ACTIONS) { Write-Host "::error title=Internal feed restore::$message" } else { Write-Error $message -ErrorAction Continue }
        exit 1
    }
    Write-Host 'All packages restore from pde-oss_Internal.' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
