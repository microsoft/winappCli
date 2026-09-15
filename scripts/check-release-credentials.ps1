#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Read-only validation of the credentials and service connections a release depends on.

.DESCRIPTION
    Releases have been blocked more than once by a credential that was still configured but
    no longer usable: an expired PAT, a token that lost push rights on a fork, a service
    connection that was rotated or de-authorized. None of that is visible until the release
    is already running.

    This script checks all of it without publishing anything. It performs GET requests only:
    it never syncs or pushes to a fork, never creates a GitHub release, and never signs.

    Every check reports PASS, WARN, or FAIL. WARN means "could not determine" (usually a
    permission gap in the checker itself, not the credential); FAIL means the credential is
    definitively unusable and the next release would break. The exit code is non-zero only
    when there is at least one FAIL.

.PARAMETER GitHubToken
    The PAT used by the release for release-notes generation, WinGet submission and the
    MS Learn docs PR (GITHUB_TOKEN_2). Falls back to $env:GH_TOKEN.

.PARAMETER WingetPkgsFork
    owner/repo of the winget-pkgs fork wingetcreate pushes through.

.PARAMETER MSLearnDocsFork
    owner/repo of the windows-dev-docs-pr fork the docs PR is pushed to.

.PARAMETER AdoOrganizationUri
    Collection URI, e.g. https://dev.azure.com/microsoft/. Falls back to
    $env:SYSTEM_COLLECTIONURI. Skipped when absent (i.e. when running locally).

.PARAMETER AdoProject
    ADO project holding the service connections. Falls back to $env:SYSTEM_TEAMPROJECT.

.PARAMETER AdoAccessToken
    Token used to read service endpoints. Falls back to $env:SYSTEM_ACCESSTOKEN.

.PARAMETER ServiceConnections
    Names of the service connections the release requires.

.PARAMETER MinimumTokenLifetimeDays
    Warn when the PAT expires within this many days. Default: 21, so a warning shows up at
    least three weekly runs before the token actually dies.

.EXAMPLE
    $env:GH_TOKEN = 'ghp_...'
    .\scripts\check-release-credentials.ps1 -WingetPkgsFork me/winget-pkgs -MSLearnDocsFork me/windows-dev-docs-pr
#>

param(
    [string]$GitHubToken = '',
    [string]$WingetPkgsFork = '',
    [string]$MSLearnDocsFork = '',
    [string]$AdoOrganizationUri = '',
    [string]$AdoProject = '',
    [string]$AdoAccessToken = '',
    [string[]]$ServiceConnections = @(),
    [int]$MinimumTokenLifetimeDays = 21
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [ValidateSet('PASS', 'WARN', 'FAIL')][string]$Status,
        [string]$Check,
        [string]$Detail
    )

    $script:Results.Add([pscustomobject]@{
            Status = $Status
            Check  = $Check
            Detail = $Detail
        })

    $color = switch ($Status) {
        'PASS' { 'Green' }
        'WARN' { 'Yellow' }
        'FAIL' { 'Red' }
    }
    Write-Host ("[{0}] {1}: {2}" -f $Status, $Check, $Detail) -ForegroundColor $color
}

function Invoke-GitHubApi {
    <#
        Returns a hashtable with Ok/StatusCode/Content/Headers instead of throwing, so a 404
        on one repo does not abort the remaining checks.
    #>
    param(
        [string]$Path,
        [string]$Token
    )

    $headers = @{
        'Accept'               = 'application/vnd.github+json'
        'User-Agent'           = 'winappcli-release-dryrun'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    if ($Token) {
        $headers['Authorization'] = "Bearer $Token"
    }

    try {
        $response = Invoke-WebRequest -Uri "https://api.github.com$Path" -Headers $headers -Method Get -UseBasicParsing
        return @{
            Ok         = $true
            StatusCode = [int]$response.StatusCode
            Content    = ($response.Content | ConvertFrom-Json)
            Headers    = $response.Headers
        }
    }
    catch {
        $status = 0
        $headers = $null
        if ($_.Exception.PSObject.Properties.Name -contains 'Response' -and $_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
            # Surface headers on the error path too: a 403 is only distinguishable as rate
            # limiting (vs. an authorization failure) by its x-ratelimit / retry-after headers.
            try { $headers = $_.Exception.Response.Headers } catch { $headers = $null }
        }
        return @{
            Ok         = $false
            StatusCode = $status
            Content    = $null
            Headers    = $headers
            Message    = $_.Exception.Message
        }
    }
}

function Get-HeaderValue {
    param($Headers, [string]$Name)

    if (-not $Headers) { return $null }

    # Two shapes reach here: the IDictionary from a successful Invoke-WebRequest, and the
    # HttpResponseHeaders off an exception's Response (which has no .Keys).
    if ($Headers -is [System.Net.Http.Headers.HttpResponseHeaders]) {
        $values = $null
        if ($Headers.TryGetValues($Name, [ref]$values)) {
            return ($values -join ', ')
        }
        return $null
    }

    if (-not ($Headers.PSObject.Properties.Name -contains 'Keys')) { return $null }

    # Invoke-WebRequest surfaces headers as string[] on pwsh and string on Windows PowerShell.
    foreach ($key in $Headers.Keys) {
        if ($key -ieq $Name) {
            $value = $Headers[$key]
            if ($value -is [array]) { return ($value -join ', ') }
            return [string]$value
        }
    }
    return $null
}

# ---------------------------------------------------------------------------
# GitHub PAT
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '=== GitHub token ===' -ForegroundColor Cyan

if (-not $GitHubToken) {
    $GitHubToken = if ($env:GH_TOKEN) { $env:GH_TOKEN } elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { '' }
}

$tokenScopes = $null

if (-not $GitHubToken) {
    Add-Result -Status 'FAIL' -Check 'GitHub token' -Detail 'No token supplied. Pass -GitHubToken or set GH_TOKEN.'
}
else {
    $user = Invoke-GitHubApi -Path '/user' -Token $GitHubToken

    if (-not $user.Ok) {
        if ($user.StatusCode -eq 401) {
            Add-Result -Status 'FAIL' -Check 'GitHub token' -Detail 'Token was rejected (401). It has expired or been revoked - regenerate it and update the variable group.'
        }
        elseif ($user.StatusCode -eq 403) {
            # GitHub returns 403 for BOTH authorization failures and rate limiting, so the status
            # alone is not a verdict. Rate-limit responses carry x-ratelimit-remaining: 0 (primary)
            # or a retry-after header (secondary); those say nothing about the credential.
            $remaining = Get-HeaderValue -Headers $user.Headers -Name 'x-ratelimit-remaining'
            $retryAfter = Get-HeaderValue -Headers $user.Headers -Name 'retry-after'
            if ($retryAfter -or $remaining -eq '0') {
                Add-Result -Status 'WARN' -Check 'GitHub token' -Detail 'Rate limited (403). The token is valid but throttled right now - inconclusive.'
            }
            else {
                Add-Result -Status 'FAIL' -Check 'GitHub token' -Detail 'Token was forbidden (403). It is likely SSO-unauthorized for the org, or its scopes were reduced.'
            }
        }
        else {
            # DNS failure, timeout, proxy, or a GitHub 5xx all land here with StatusCode 0 or 5xx.
            # None of those say anything about the credential, and reporting them as FAIL would
            # turn a GitHub blip into a red weekly build that sends someone rotating a live token.
            Add-Result -Status 'WARN' -Check 'GitHub token' -Detail "Could not reach GET /user (status $($user.StatusCode)): $($user.Message). This is inconclusive - the token was not proven bad."
        }
    }
    else {
        Add-Result -Status 'PASS' -Check 'GitHub token' -Detail "Authenticated as '$($user.Content.login)'."

        $tokenScopes = Get-HeaderValue -Headers $user.Headers -Name 'X-OAuth-Scopes'
        if ($null -ne $tokenScopes) {
            $scopeList = @($tokenScopes -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            Add-Result -Status 'PASS' -Check 'GitHub token scopes' -Detail "Scopes: $(if ($scopeList) { $scopeList -join ', ' } else { '(none)' })"

            # wingetcreate forks and pushes; the docs job pushes a branch. Both need repo write.
            $hasRepoWrite = ($scopeList -contains 'repo') -or ($scopeList -contains 'public_repo')
            if (-not $hasRepoWrite) {
                Add-Result -Status 'FAIL' -Check 'GitHub token scopes' -Detail "Neither 'repo' nor 'public_repo' is granted. WinGet submission and the MS Learn docs PR both push branches and will fail."
            }
        }
        else {
            # Fine-grained PATs and GitHub App tokens do not return this header.
            Add-Result -Status 'WARN' -Check 'GitHub token scopes' -Detail 'No X-OAuth-Scopes header (fine-grained PAT or app token). Per-repository permissions are checked below instead.'
        }

        $expiry = Get-HeaderValue -Headers $user.Headers -Name 'github-authentication-token-expiration'
        if ($expiry) {
            $parsed = [datetime]::MinValue
            # Header format is "2026-01-30 15:04:05 UTC".
            if ([datetime]::TryParse(($expiry -replace ' UTC$', 'Z'), [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal, [ref]$parsed)) {
                $daysLeft = [int][math]::Floor(($parsed - [datetime]::UtcNow).TotalDays)
                if ($daysLeft -lt 0) {
                    Add-Result -Status 'FAIL' -Check 'GitHub token expiry' -Detail "Token expired on $expiry."
                }
                elseif ($daysLeft -le $MinimumTokenLifetimeDays) {
                    Add-Result -Status 'WARN' -Check 'GitHub token expiry' -Detail "Token expires in $daysLeft day(s) on $expiry. Regenerate it before the next release."
                }
                else {
                    Add-Result -Status 'PASS' -Check 'GitHub token expiry' -Detail "Expires in $daysLeft day(s) on $expiry."
                }
            }
            else {
                Add-Result -Status 'WARN' -Check 'GitHub token expiry' -Detail "Could not parse expiration header value '$expiry'."
            }
        }
        else {
            Add-Result -Status 'WARN' -Check 'GitHub token expiry' -Detail 'No expiration header returned. The token may be non-expiring, which is worth reviewing.'
        }

        # Rate limit is deliberately not checked: it is WARN-only and never actionable on a
        # pipeline PAT, and a real exhaustion shows up as a failure in the checks above.
    }
}

# ---------------------------------------------------------------------------
# Repository access
# ---------------------------------------------------------------------------

function Test-RepoAccess {
    param(
        [string]$Repo,
        [string]$Label,
        [switch]$RequirePush
    )

    if (-not $Repo) {
        Add-Result -Status 'WARN' -Check $Label -Detail 'Not configured - skipping. Set the corresponding pipeline variable to enable this check.'
        return
    }

    # Format is validated even without a token: a malformed variable group value is a real
    # configuration error, and it must not be masked by an unrelated token failure.
    if ($Repo -notmatch '^[\w.-]+/[\w.-]+$') {
        Add-Result -Status 'FAIL' -Check $Label -Detail "'$Repo' is not in owner/repo form."
        return
    }

    if (-not $GitHubToken) {
        Add-Result -Status 'WARN' -Check $Label -Detail "'$Repo' is well-formed, but access could not be checked without a token."
        return
    }

    $result = Invoke-GitHubApi -Path "/repos/$Repo" -Token $GitHubToken
    if (-not $result.Ok) {
        if ($result.StatusCode -eq 404) {
            Add-Result -Status 'FAIL' -Check $Label -Detail "'$Repo' was not found, or the token cannot see it. If the fork was deleted, recreate it - wingetcreate and the docs PR both depend on it."
        }
        elseif ($result.StatusCode -eq 403) {
            # Same ambiguity as GET /user: 403 covers both authorization and rate limiting.
            $remaining = Get-HeaderValue -Headers $result.Headers -Name 'x-ratelimit-remaining'
            $retryAfter = Get-HeaderValue -Headers $result.Headers -Name 'retry-after'
            if ($retryAfter -or $remaining -eq '0') {
                Add-Result -Status 'WARN' -Check $Label -Detail "Rate limited reading '$Repo' (403). Inconclusive - access was not disproved."
            }
            else {
                Add-Result -Status 'FAIL' -Check $Label -Detail "Access to '$Repo' was forbidden (403). The token is likely SSO-unauthorized for the org."
            }
        }
        else {
            # Transport failure, rate limit, or a GitHub 5xx. None of those disprove push access,
            # and reporting them as FAIL would turn a GitHub blip into a red weekly build.
            Add-Result -Status 'WARN' -Check $Label -Detail "Could not reach '$Repo' (status $($result.StatusCode)). Inconclusive - access was not disproved."
        }
        return
    }

    if (-not $RequirePush) {
        Add-Result -Status 'PASS' -Check $Label -Detail "'$Repo' is reachable."
        return
    }

    $permissions = $result.Content.permissions
    if (-not $permissions) {
        Add-Result -Status 'WARN' -Check $Label -Detail "'$Repo' is reachable but the response carried no permissions block, so push access could not be confirmed."
        return
    }

    if ($permissions.push) {
        Add-Result -Status 'PASS' -Check $Label -Detail "'$Repo' is reachable and the token has push access."
    }
    else {
        Add-Result -Status 'FAIL' -Check $Label -Detail "The token cannot push to '$Repo'. The release job that pushes a branch there will fail."
    }
}

# Runs with or without a token: the format check is token-independent. The product repo itself is
# not checked - the job checked it out minutes earlier, so a failure there is not reachable here.
Test-RepoAccess -Repo $WingetPkgsFork -Label 'winget-pkgs fork push access' -RequirePush
Test-RepoAccess -Repo $MSLearnDocsFork -Label 'MS Learn docs fork push access' -RequirePush

# ---------------------------------------------------------------------------
# ADO service connections
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '=== Azure DevOps service connections ===' -ForegroundColor Cyan

if (-not $AdoOrganizationUri) { $AdoOrganizationUri = $env:SYSTEM_COLLECTIONURI }
if (-not $AdoProject) { $AdoProject = $env:SYSTEM_TEAMPROJECT }
if (-not $AdoAccessToken) { $AdoAccessToken = $env:SYSTEM_ACCESSTOKEN }

if (-not $ServiceConnections) {
    Add-Result -Status 'WARN' -Check 'Service connections' -Detail 'No connection names supplied - skipping.'
}
elseif (-not $AdoOrganizationUri -or -not $AdoProject) {
    Add-Result -Status 'WARN' -Check 'Service connections' -Detail 'Not running in Azure Pipelines (no collection URI/project) - skipping.'
}
elseif (-not $AdoAccessToken) {
    Add-Result -Status 'WARN' -Check 'Service connections' -Detail 'SYSTEM_ACCESSTOKEN is not available. Map it into the step env to enable this check.'
}
else {
    $encodedProject = [uri]::EscapeDataString($AdoProject)
    $baseUri = "$($AdoOrganizationUri.TrimEnd('/'))/$encodedProject/_apis/serviceendpoint/endpoints"
    $adoHeaders = @{
        Authorization = "Bearer $AdoAccessToken"
        Accept        = 'application/json'
    }

    # What this check can and cannot prove.
    #
    # Azure DevOps does NOT return 401/403 when the caller cannot see a service endpoint - it
    # returns HTTP 200 with an empty list, which is byte-identical to "this connection does not
    # exist". Worse, endpoint `Read` is granted PER CONNECTION, so being able to see one endpoint
    # says nothing about being able to see another. Absence is therefore never provable from here.
    #
    # So: a visible connection yields a real verdict (present, and ready or not), and anything
    # invisible is reported as inconclusive rather than missing. Build 20260914.1 failed the first
    # weekly rehearsal with four "Not found" verdicts against four connections that all existed and
    # were ready; that must not happen again, and a partial permission grant must not recreate it.
    #
    # One unfiltered call rather than one per name: the same response answers every name, and it
    # also tells us whether the identity has any endpoint visibility at all.
    #
    # includeFailed=true is load-bearing. It defaults to FALSE, which omits endpoints with
    # isReady:false - exactly the ones the readiness verdict below exists to catch. Without it a
    # broken connection is invisible and gets downgraded to "not visible" (WARN) instead of FAIL.
    # Verified against pde-oss: the default call returned 19 endpoints, all isReady:true, while
    # includeFailed=true returned 21 - the two extras both isReady:false.
    $visible = $null
    $probeFailure = $null
    try {
        $all = Invoke-RestMethod -Uri "$baseUri`?includeFailed=true&api-version=7.1" -Headers $adoHeaders -Method Get
        $visible = @()
        if ($all.count -gt 0) { $visible = @($all.value) }
    }
    catch {
        $probeStatus = 0
        if ($_.Exception.PSObject.Properties.Name -contains 'Response' -and $_.Exception.Response) {
            $probeStatus = [int]$_.Exception.Response.StatusCode
        }
        $probeFailure = "status $probeStatus"
    }

    if ($null -eq $visible) {
        Add-Result -Status 'WARN' -Check 'Service connections' -Detail "Could not list service endpoints (the API returned $probeFailure), so none could be verified."
    }
    elseif ($visible.Count -eq 0) {
        Add-Result -Status 'WARN' -Check 'Service connections' -Detail "The build identity cannot see any service endpoint in '$AdoProject', so none could be verified. Grant the build service 'Read' on the service connections to enable this check."
    }
    else {
        foreach ($name in $ServiceConnections) {
            if (-not $name) { continue }

            $endpoint = $visible | Where-Object { $_.name -eq $name } | Select-Object -First 1

            if (-not $endpoint) {
                # Cannot distinguish "deleted" from "Read not granted on this one", because the
                # grant is per connection. Reporting this as missing is what broke build 20260914.1.
                Add-Result -Status 'WARN' -Check "Service connection '$name'" -Detail "Not visible to the build identity. It was renamed or deleted, or 'Read' was not granted on it specifically - this check cannot tell which."
                continue
            }

            $isReady = $true
            if ($endpoint.PSObject.Properties.Name -contains 'isReady') {
                $isReady = [bool]$endpoint.isReady
            }

            if ($isReady) {
                Add-Result -Status 'PASS' -Check "Service connection '$name'" -Detail "Present and ready (type: $($endpoint.type))."
            }
            else {
                # Visible AND explicitly not ready: the one definitive bad verdict available here.
                Add-Result -Status 'FAIL' -Check "Service connection '$name'" -Detail 'Present but not ready. It is likely mid-rotation or misconfigured.'
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

$passed = @($script:Results | Where-Object { $_.Status -eq 'PASS' }).Count
$warned = @($script:Results | Where-Object { $_.Status -eq 'WARN' }).Count
$failed = @($script:Results | Where-Object { $_.Status -eq 'FAIL' }).Count

Write-Host ''
Write-Host '=== Summary ===' -ForegroundColor Cyan
Write-Host "PASS: $passed  WARN: $warned  FAIL: $failed"

if ($failed -gt 0) {
    Write-Host ''
    Write-Host 'Failing checks:' -ForegroundColor Red
    foreach ($r in $script:Results | Where-Object { $_.Status -eq 'FAIL' }) {
        Write-Host "  - $($r.Check): $($r.Detail)" -ForegroundColor Red
    }
    throw "$failed release credential check(s) failed."
}

Write-Host 'All release credential checks passed.' -ForegroundColor Green
