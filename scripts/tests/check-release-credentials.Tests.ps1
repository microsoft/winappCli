#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
    Pester tests for scripts/check-release-credentials.ps1.

    The weekly rehearsal relies on this script to be *decisive*: a genuinely dead credential
    has to be reported clearly, and an inconclusive result (the checker itself lacking permission)
    must only warn. Getting that split wrong makes the whole rehearsal untrustworthy - either
    it cries wolf every week and gets ignored, or it stays green while a release is doomed.

    These tests must stay OFFLINE. build-cli.ps1 runs scripts/tests during CI *and during the
    real release build*, so a test that reaches api.github.com would let a GitHub outage block
    a release. Every case below exercises a path that returns before any network call: no
    GitHub token and no ADO collection URI.
#>

BeforeAll {
    $script:CheckScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'check-release-credentials.ps1'

    # Runs the script in a child pwsh so its `throw` becomes an exit code, and so the ambient
    # GH_TOKEN of a developer machine cannot leak in and turn these into live network tests.
    function Invoke-Checker {
        param([string[]]$ScriptArgs = @())

        $envPrefix = ''
        foreach ($key in @('GH_TOKEN', 'GITHUB_TOKEN', 'SYSTEM_ACCESSTOKEN', 'SYSTEM_COLLECTIONURI', 'SYSTEM_TEAMPROJECT')) {
            $envPrefix += "`$env:$key = ''; "
        }

        # Parameter names must stay UNQUOTED. Quoting a switch makes PowerShell treat
        # it as a positional value rather than a switch, which silently shifts every later
        # argument by one and binds a junk value to -GitHubToken.
        $rendered = $ScriptArgs | ForEach-Object {
            if ($_ -match '^-[A-Za-z]') { $_ } else { "'" + ($_ -replace "'", "''") + "'" }
        }
        $command = "$envPrefix & '$script:CheckScript' $($rendered -join ' ')"

        $output = & pwsh -NoProfile -NonInteractive -Command $command 2>&1 | Out-String
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = $output
        }
    }
}

Describe 'check-release-credentials.ps1' {

    Context 'no credentials available' {
        BeforeAll {
            $script:result = Invoke-Checker -ScriptArgs @()
        }

        It 'fails the run' {
            $script:result.ExitCode | Should -Not -Be 0
        }

        It 'reports the missing GitHub token as FAIL, not WARN' {
            $script:result.Output | Should -Match '\[FAIL\] GitHub token'
        }

        It 'names the variable to set' {
            $script:result.Output | Should -Match 'GH_TOKEN'
        }

        It 'prints a PASS/WARN/FAIL tally' {
            $script:result.Output | Should -Match 'PASS: \d+\s+WARN: \d+\s+FAIL: \d+'
        }

        It 'lists the failing checks at the end' {
            $script:result.Output | Should -Match 'Failing checks:'
        }
    }

    Context 'fork configuration validation' {
        It 'fails a fork value that is not owner/repo, even without a token' {
            # Format is token-independent, so a bad variable group value must not be masked
            # by an unrelated token failure.
            $result = Invoke-Checker -ScriptArgs @('-WingetPkgsFork', 'not-a-repo')

            $result.Output | Should -Match "not in owner/repo form"
        }

        It 'warns rather than fails for a well-formed fork it cannot check' {
            $result = Invoke-Checker -ScriptArgs @('-MSLearnDocsFork', 'owner/repo')

            $result.Output | Should -Match '\[WARN\] MS Learn docs fork push access'
        }

        It 'warns when a fork is not configured at all' {
            $result = Invoke-Checker -ScriptArgs @()

            $result.Output | Should -Match '\[WARN\] winget-pkgs fork push access'
        }
    }

    Context 'service connection checks outside Azure Pipelines' {
        BeforeAll {
            $script:result = Invoke-Checker -ScriptArgs @('-ServiceConnections', 'some-connection')
        }

        It 'warns rather than fails when there is no collection URI' {
            $script:result.Output | Should -Match '\[WARN\] Service connections'
        }

        It 'never reports a connection as missing when it could not look it up' {
            # A false "not found" would send someone hunting for a deleted connection that exists.
            $script:result.Output | Should -Not -Match '\[FAIL\] Service connection'
        }
    }

    Context 'service connections the build identity cannot enumerate' {
        BeforeAll {
            # Regression test for build 20260914.1, where the first real weekly rehearsal failed
            # with four "Not found" FAILs against four connections that all existed and were ready.
            #
            # Azure DevOps does NOT return 401/403 when the caller lacks endpoint read permission -
            # it returns HTTP 200 with an empty list, which is indistinguishable from "absent". The
            # script must therefore probe its own visibility first and report inconclusive, rather
            # than accusing every named connection of not existing.
            #
            # 127.0.0.1:1 is unroutable and refuses immediately, so this exercises the probe-failure
            # branch without leaving the machine - scripts/tests must stay offline because
            # build-cli.ps1 runs them during a real release build.
            $script:result = Invoke-Checker -ScriptArgs @(
                '-AdoOrganizationUri', 'http://127.0.0.1:1/',
                '-AdoProject', 'pde-oss',
                '-AdoAccessToken', 'irrelevant',
                '-ServiceConnections', 'conn-a', 'conn-b'
            )
        }

        It 'reports one inconclusive warning instead of a failure per connection' {
            $script:result.Output | Should -Match '\[WARN\] Service connections'
            $script:result.Output | Should -Not -Match '\[FAIL\] Service connection'
        }

        It 'tells the operator how to make the check work' {
            $script:result.Output | Should -Match "Grant the build service 'Read'"
        }
    }

    Context 'offline safety' {
        It 'completes without any network access when every probe is skippable' {
            # Guards the property this whole file depends on: with no tokens,
            # the script must reach its summary without calling out. If this file ever starts
            # making live requests, a GitHub outage can block a release build.
            $result = Invoke-Checker -ScriptArgs @()

            $result.Output | Should -Match '=== Summary ==='
            $result.Output | Should -Not -Match 'status 401'
        }
    }
}
