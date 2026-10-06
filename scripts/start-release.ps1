#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Automate the release process for Windows App Development CLI
.DESCRIPTION
    This script automates the release workflow:
    1. Verifies you are on the main branch with a clean working tree and latest changes
    2. Reads and confirms the version from version.json
    3. Creates and pushes a rel/v{version} branch to origin (triggers the release pipeline)
    4. Returns to main, bumps the patch version in version.json
    5. Creates a PR to merge the version bump back into main

    Prerequisites:
    - Git must be installed and authenticated with push access to origin
    - GitHub CLI (gh) must be installed and authenticated (for PR creation)
.PARAMETER SkipConfirmation
    Skip the interactive confirmation prompt before creating the release branch
.PARAMETER DryRun
    Show what would happen without making any changes (no branches created, no pushes, no PRs)
.PARAMETER Version
    Override the release version instead of reading from version.json. The override
    must be Major.Minor.Patch, must not match an existing rel/v* branch on origin,
    and must be strictly higher than every previously released version. When this
    parameter is used, locally modified version.json and scripts/start-release.ps1
    are tolerated (version.json is restored from HEAD; the in-flight script edits
    stay on main only and are not committed to the release or bump branches), so
    you do not have to land a version-bump PR (or a script tweak) before cutting
    the release.
.EXAMPLE
    .\scripts\start-release.ps1
.EXAMPLE
    .\scripts\start-release.ps1 -SkipConfirmation
.EXAMPLE
    .\scripts\start-release.ps1 -DryRun
.EXAMPLE
    .\scripts\start-release.ps1 -Version 0.3.0
#>

param(
    [switch]$SkipConfirmation = $false,
    [switch]$DryRun = $false,
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
$VersionFilePath = Join-Path $ProjectRoot "version.json"

# ─── Helpers ────────────────────────────────────────────────────────────────────

function Write-Step  { param([string]$msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Info  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Gray }
function Write-Ok    { param([string]$msg) Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Yellow }

function Confirm-Step {
    param([string]$Prompt)
    if ($script:SkipConfirmation -or $script:DryRun) { return }
    Write-Host ""
    $response = Read-Host "  $Prompt (y/N)"
    if ($response -notin @("y", "Y", "yes", "Yes")) {
        Write-Warn "Release cancelled by user."
        exit 0
    }
}

function Invoke-GitOrDryRun {
    param([string]$Description, [string[]]$Arguments)
    if ($DryRun) {
        Write-Warn "[DRY RUN] git $($Arguments -join ' ')"
    } else {
        Write-Info "git $($Arguments -join ' ')"
        & git @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
        }
    }
}

function Invoke-GhOrDryRun {
    param([string]$Description, [string[]]$Arguments)
    if ($DryRun) {
        Write-Warn "[DRY RUN] gh $($Arguments -join ' ')"
    } else {
        Write-Info "gh $($Arguments -join ' ')"
        & gh @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
        }
    }
}

# ─── Pre-flight checks ─────────────────────────────────────────────────────────

Push-Location $ProjectRoot
try {
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║     Windows App Development CLI - Release    ║" -ForegroundColor Magenta
    Write-Host "╚══════════════════════════════════════════════╝" -ForegroundColor Magenta

    if ($DryRun) {
        Write-Host ""
        Write-Warn "DRY RUN MODE — no changes will be made"
    }

    # 1. Check we are on main
    Write-Step "Checking current branch..."
    $currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($currentBranch -ne "main") {
        Write-Error "You must be on the 'main' branch to start a release. Current branch: '$currentBranch'. Please run: git checkout main"
        exit 1
    }
    Write-Ok "On branch: main"

    # 2. Check for clean working tree
    Write-Step "Checking working tree..."
    $status = git status --porcelain
    if ($status) {
        # When the caller supplies an explicit -Version, allow a small allowlist
        # of files to be dirty so you don't have to land a version-bump PR (or a
        # tweak to this script) just to cut a release. version.json is restored
        # from HEAD; the release script itself is left as-is so the in-flight
        # edits keep applying for the rest of this run.
        $allowDirtyWithOverride = @('version.json', 'scripts/start-release.ps1')
        $dirtyLines = @($status -split "`n" | Where-Object { $_.Trim() })

        $disallowed = @()
        $needsRestore = $false
        foreach ($line in $dirtyLines) {
            if ($line -notmatch '^\s*[MARCD]+\s+(.+?)\s*$') {
                $disallowed += $line
                continue
            }
            $path = $matches[1].Trim() -replace '\\', '/'
            if (-not $Version -or $allowDirtyWithOverride -notcontains $path) {
                $disallowed += $line
            } elseif ($path -eq 'version.json') {
                $needsRestore = $true
            }
        }

        if ($disallowed.Count -gt 0) {
            Write-Error "Working tree is not clean. Please commit or stash your changes first.`n$($disallowed -join "`n")"
            exit 1
        }

        if ($needsRestore) {
            Write-Warn "version.json has local changes; restoring from HEAD because -Version was provided"
            Invoke-GitOrDryRun -Description "Restore version.json" -Arguments @("checkout", "--", "version.json")
        }
        if ($Version -and ($dirtyLines | Where-Object { $_ -match 'scripts/start-release\.ps1' })) {
            Write-Warn "scripts/start-release.ps1 has local changes; allowed because -Version was provided (will not be committed to the release branch)"
        }
    }
    Write-Ok "Working tree is clean"

    # 3. Pull latest from origin
    Write-Step "Pulling latest from origin/main..."
    Invoke-GitOrDryRun -Description "Fetch and pull latest" -Arguments @("pull", "--ff-only", "origin", "main")
    Write-Ok "Up to date with origin/main"

    # 4. Determine the release version (override or version.json)
    if ($Version) {
        Write-Step "Using release version override: $Version"
        $releaseVersion = $Version.Trim()
    } else {
        Write-Step "Reading version from version.json..."
        if (-not (Test-Path $VersionFilePath)) {
            Write-Error "version.json not found at: $VersionFilePath"
            exit 1
        }

        $versionJson = Get-Content $VersionFilePath -Raw | ConvertFrom-Json
        $releaseVersion = $versionJson.version
        if (-not $releaseVersion) {
            Write-Error "Could not read 'version' property from version.json"
            exit 1
        }
    }
    Write-Ok "Release version: $releaseVersion"

    Confirm-Step "Is '$releaseVersion' the correct version to release?"

    # Parse version components
    if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') {
        Write-Error "Version '$releaseVersion' is not in the expected Major.Minor.Patch format"
        exit 1
    }
    $versionParts = $releaseVersion -split '\.'
    $major = [int]$versionParts[0]
    $minor = [int]$versionParts[1]
    $patch = [int]$versionParts[2]

    $releaseBranch = "rel/v$releaseVersion"
    $nextPatch = $patch + 1
    $nextVersion = "$major.$minor.$nextPatch"
    $bumpBranch = "bump/v$nextVersion"

    # 5. Check that the release branch doesn't already exist, and (when an
    #    override is supplied) that the version is strictly higher than every
    #    previously released rel/v* branch.
    Write-Step "Checking for existing release branch..."
    $remoteBranches = git ls-remote --heads origin "rel/v*" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to list remote release branches from origin"
        exit 1
    }

    $releasedVersions = @()
    foreach ($line in ($remoteBranches -split "`n")) {
        if ($line -match 'refs/heads/rel/v(.+)$') {
            $releasedVersions += $matches[1].Trim()
        }
    }

    if ($releasedVersions -contains $releaseVersion) {
        Write-Error "Release branch '$releaseBranch' already exists on origin. Has this version already been released?"
        exit 1
    }
    Write-Ok "Branch '$releaseBranch' does not exist yet — good to go"

    if ($Version) {
        Write-Step "Validating override version is higher than all released versions..."
        $newVer = [version]$releaseVersion
        $highestExisting = $null
        $highestVer = $null
        foreach ($v in $releasedVersions) {
            # Strip any prerelease suffix (e.g. 0.1.0-rc1) for comparison purposes
            $core = ($v -split '-', 2)[0]
            if ($core -notmatch '^\d+\.\d+\.\d+$') { continue }
            $ver = [version]$core
            if ($null -eq $highestVer -or $ver -gt $highestVer) {
                $highestVer = $ver
                $highestExisting = $v
            }
        }

        if ($highestVer -and $newVer -le $highestVer) {
            Write-Error "Override version '$releaseVersion' is not higher than the latest released version '$highestExisting'."
            exit 1
        }
        Write-Ok "Override version '$releaseVersion' is higher than latest released ('$highestExisting')"
    }

    # 6. Confirm with user
    Write-Host ""
    Write-Host "  ┌─────────────────────────────────────────────┐" -ForegroundColor White
    Write-Host "  │  Release Plan                               │" -ForegroundColor White
    Write-Host "  │                                             │" -ForegroundColor White
    Write-Host "  │  Release version : $releaseVersion$((' ' * (25 - $releaseVersion.Length)))│" -ForegroundColor White
    Write-Host "  │  Release branch  : $releaseBranch$((' ' * (25 - $releaseBranch.Length)))│" -ForegroundColor White
    Write-Host "  │  Next dev version: $nextVersion$((' ' * (25 - $nextVersion.Length)))│" -ForegroundColor White
    Write-Host "  │  Bump branch     : $bumpBranch$((' ' * (25 - $bumpBranch.Length)))│" -ForegroundColor White
    Write-Host "  │                                             │" -ForegroundColor White
    Write-Host "  │  Steps:                                     │" -ForegroundColor White
    Write-Host "  │  1. Create & push $releaseBranch$((' ' * (18 - $releaseBranch.Length)))│" -ForegroundColor White
    Write-Host "  │  2. Bump version.json to $nextVersion$((' ' * (12 - $nextVersion.Length)))│" -ForegroundColor White
    Write-Host "  │  3. Create PR to merge bump into main       │" -ForegroundColor White
    Write-Host "  └─────────────────────────────────────────────┘" -ForegroundColor White

    Confirm-Step "Does this release plan look correct? Proceed?"

    # ─── Step 1: Create and push the release branch ─────────────────────────────

    Write-Step "Step 1/3: Creating release branch '$releaseBranch'..."
    Invoke-GitOrDryRun -Description "Create release branch" -Arguments @("checkout", "-b", $releaseBranch)
    Write-Ok "Local branch '$releaseBranch' created"

    # When -Version is supplied, version.json on main is out-of-date relative to
    # the release we're cutting. Write it on the release branch and commit so
    # the release pipeline sees the correct version.
    if ($Version) {
        Write-Step "Writing override version '$releaseVersion' into version.json on release branch..."

        # If the user is iterating on this script itself, that change traveled
        # with the checkout. Restore it from HEAD on the release branch so it
        # doesn't get committed into the release. PowerShell has already parsed
        # the file, so this doesn't affect the running script.
        $scriptRel = 'scripts/start-release.ps1'
        $scriptStillDirty = (git status --porcelain -- $scriptRel) -ne $null -and
                            (git status --porcelain -- $scriptRel).Trim() -ne ''
        if ($scriptStillDirty) {
            Write-Info "Reverting in-flight changes to $scriptRel on release branch (they stay on main only)"
            Invoke-GitOrDryRun -Description "Restore release script on release branch" -Arguments @("checkout", "--", $scriptRel)
        }

        if ($DryRun) {
            Write-Warn "[DRY RUN] Would update version.json to $releaseVersion and commit on $releaseBranch"
        } else {
            $newVersionJson = @{ version = $releaseVersion } | ConvertTo-Json
            Set-Content -Path $VersionFilePath -Value $newVersionJson -NoNewline

            # Synchronize version-dependent plugin manifests and build release artifacts.
            Write-Info "Running build to regenerate version-dependent files..."
            $buildScript = Join-Path $PSScriptRoot "build-cli.ps1"
            & $buildScript -SkipTests -SkipNpm -SkipMsix
            if ($LASTEXITCODE -ne 0) {
                throw "Build failed with exit code $LASTEXITCODE"
            }
            Write-Ok "Build completed successfully"
        }
        Invoke-GitOrDryRun -Description "Stage version override" -Arguments @("add", "--all")
        Invoke-GitOrDryRun -Description "Commit version override" -Arguments @("commit", "-m", "Set version to $releaseVersion for release")
    }

    Confirm-Step "Push '$releaseBranch' to origin? This will kick off the release pipeline"

    Invoke-GitOrDryRun -Description "Push release branch" -Arguments @("push", "-u", "origin", $releaseBranch)
    Write-Ok "Release branch '$releaseBranch' pushed to origin"

    # ─── Step 2: Go back to main and bump the version ───────────────────────────

    Write-Step "Step 2/3: Bumping version to $nextVersion..."
    Invoke-GitOrDryRun -Description "Switch back to main" -Arguments @("checkout", "main")

    # Create the bump branch from main
    Invoke-GitOrDryRun -Description "Create bump branch" -Arguments @("checkout", "-b", $bumpBranch)

    # Update version.json
    if ($DryRun) {
        Write-Warn "[DRY RUN] Would update version.json: $releaseVersion -> $nextVersion"
    } else {
        $newVersionJson = @{ version = $nextVersion } | ConvertTo-Json
        Set-Content -Path $VersionFilePath -Value $newVersionJson -NoNewline
        Write-Info "Updated version.json: $releaseVersion -> $nextVersion"
    }

    # Run a full build to regenerate LLM docs and any other version-dependent files
    Write-Info "Running full build to regenerate version-dependent files..."
    $buildScript = Join-Path $PSScriptRoot "build-cli.ps1"
    if ($DryRun) {
        Write-Warn "[DRY RUN] Would run: $buildScript -SkipTests"
    } else {
        & $buildScript -SkipTests -SkipNpm -SkipMsix
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
        Write-Ok "Build completed successfully"
    }

    # Stage all changes (version.json + regenerated docs/artifacts)
    if ($Version) {
        # Don't carry in-flight edits to this script onto the bump branch either.
        $scriptRel = 'scripts/start-release.ps1'
        $scriptStillDirty = (git status --porcelain -- $scriptRel) -ne $null -and
                            (git status --porcelain -- $scriptRel).Trim() -ne ''
        if ($scriptStillDirty) {
            Write-Info "Reverting in-flight changes to $scriptRel on bump branch"
            Invoke-GitOrDryRun -Description "Restore release script on bump branch" -Arguments @("checkout", "--", $scriptRel)
        }
    }
    Invoke-GitOrDryRun -Description "Stage all changes" -Arguments @("add", "--all")
    Invoke-GitOrDryRun -Description "Commit version bump" -Arguments @("commit", "-m", "Bump version to $nextVersion for development")

    Confirm-Step "Push version bump branch '$bumpBranch' to origin and create PR?"

    Invoke-GitOrDryRun -Description "Push bump branch" -Arguments @("push", "-u", "origin", $bumpBranch)
    Write-Ok "Bump branch '$bumpBranch' pushed to origin"

    # ─── Step 3: Create a PR for the version bump ───────────────────────────────

    Write-Step "Step 3/3: Creating pull request..."

    # Build the PR details
    $prTitle = "Bump version to $nextVersion for development"
    $prBody  = "Auto-generated version bump after releasing v$releaseVersion.`n`nThis PR bumps the patch version in ``version.json`` from ``$releaseVersion`` to ``$nextVersion`` so that prerelease builds pick up the new version number."

    # Check that gh CLI is available
    $ghAvailable = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $ghAvailable -and -not $DryRun) {
        $encodedTitle = [System.Uri]::EscapeDataString($prTitle)
        $encodedBody  = [System.Uri]::EscapeDataString($prBody)
        $prUrl = "https://github.com/microsoft/winappcli/compare/main...$($bumpBranch)?expand=1&title=$encodedTitle&body=$encodedBody"
        Write-Warn "GitHub CLI (gh) is not installed. Open this link to create the PR:"
        Write-Host ""
        Write-Host "    $prUrl" -ForegroundColor Yellow
        Write-Host ""
    } else {
        Invoke-GhOrDryRun -Description "Create pull request" -Arguments @(
            "pr", "create",
            "--base", "main",
            "--head", $bumpBranch,
            "--title", $prTitle,
            "--body", $prBody
        )
        Write-Ok "Pull request created"
    }

    # ─── Done ───────────────────────────────────────────────────────────────────

    # Return to main so you're in a good state
    Invoke-GitOrDryRun -Description "Switch back to main" -Arguments @("checkout", "main")

    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║  Release started successfully!               ║" -ForegroundColor Green
    Write-Host "╠══════════════════════════════════════════════╣" -ForegroundColor Green
    Write-Host "║                                              ║" -ForegroundColor Green
    Write-Host "║  • Release branch '$releaseBranch' pushed$((' ' * (14 - $releaseBranch.Length)))║" -ForegroundColor Green
    Write-Host "║  • Version bump PR created for $nextVersion$((' ' * (10 - $nextVersion.Length)))║" -ForegroundColor Green
    Write-Host "║                                              ║" -ForegroundColor Green
    Write-Host "║  Next steps:                                 ║" -ForegroundColor Green
    Write-Host "║  1. Monitor the release pipeline              ║" -ForegroundColor Green
    Write-Host "║  2. Review & merge the version bump PR       ║" -ForegroundColor Green
    Write-Host "╚══════════════════════════════════════════════╝" -ForegroundColor Green
    Write-Host ""

} catch {
    Write-Host ""
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host ""
    Write-Warn "The release process did not complete. You may need to manually clean up:"
    Write-Warn "  - Check your current branch: git rev-parse --abbrev-ref HEAD"
    Write-Warn "  - Switch back to main:       git checkout main"
    if ($releaseBranch) {
        Write-Warn "  - Delete local release branch: git branch -D $releaseBranch"
    }
    if ($bumpBranch) {
        Write-Warn "  - Delete local bump branch:    git branch -D $bumpBranch"
        Write-Warn "  - Restore version.json:        git checkout -- version.json"
    }

    exit 1
} finally {
    Pop-Location
}
