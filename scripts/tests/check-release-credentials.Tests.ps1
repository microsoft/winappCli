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
            # The failure mode is specifically an HTTP 200 carrying {"count":0,"value":[]}: Azure
            # DevOps does NOT 401/403 when the caller lacks endpoint read permission, so an empty
            # success is indistinguishable from absence. A connection-refused error would exercise
            # a different branch entirely and would not catch a regression here, so this stands up
            # a real listener on loopback and serves that exact body.
            #
            # Loopback only - scripts/tests must stay offline because build-cli.ps1 runs them
            # during a real release build.
            $script:listener = [System.Net.HttpListener]::new()
            $script:port = Get-Random -Minimum 14000 -Maximum 14999
            $script:listener.Prefixes.Add("http://127.0.0.1:$($script:port)/")
            $script:listener.Start()

            $script:pump = [powershell]::Create()
            [void]$script:pump.AddScript({
                    param($l)
                    while ($l.IsListening) {
                        try {
                            $ctx = $l.GetContext()
                            $bytes = [Text.Encoding]::UTF8.GetBytes('{"count":0,"value":[]}')
                            $ctx.Response.StatusCode = 200
                            $ctx.Response.ContentType = 'application/json'
                            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                            $ctx.Response.Close()
                        }
                        catch { break }
                    }
                }).AddArgument($script:listener)
            [void]$script:pump.BeginInvoke()

            $script:result = Invoke-Checker -ScriptArgs @(
                '-AdoOrganizationUri', "http://127.0.0.1:$($script:port)/",
                '-AdoProject', 'pde-oss',
                '-AdoAccessToken', 'irrelevant',
                '-ServiceConnections', 'conn-a', 'conn-b'
            )
        }

        AfterAll {
            if ($script:listener) { $script:listener.Stop(); $script:listener.Close() }
            if ($script:pump) { $script:pump.Dispose() }
        }

        It 'reports one inconclusive warning instead of a failure per connection' {
            $script:result.Output | Should -Match '\[WARN\] Service connections'
            $script:result.Output | Should -Not -Match '\[FAIL\] Service connection'
        }

        It 'tells the operator how to make the check work' {
            $script:result.Output | Should -Match "Grant the build service 'Read'"
        }
    }

    Context 'a connection that is not among the visible endpoints' {
        BeforeAll {
            # Partial permission grants are the subtler half of the same bug: endpoint `Read` is
            # granted per connection, so seeing SOME endpoints proves nothing about a specific one.
            # Serving a non-empty list that omits the requested name must still be inconclusive.
            $script:listener2 = [System.Net.HttpListener]::new()
            $script:port2 = Get-Random -Minimum 15000 -Maximum 15999
            $script:listener2.Prefixes.Add("http://127.0.0.1:$($script:port2)/")
            $script:listener2.Start()

            $script:pump2 = [powershell]::Create()
            [void]$script:pump2.AddScript({
                    param($l)
                    $body = '{"count":1,"value":[{"name":"some-other-connection","type":"github","isReady":true}]}'
                    while ($l.IsListening) {
                        try {
                            $ctx = $l.GetContext()
                            $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                            $ctx.Response.StatusCode = 200
                            $ctx.Response.ContentType = 'application/json'
                            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                            $ctx.Response.Close()
                        }
                        catch { break }
                    }
                }).AddArgument($script:listener2)
            [void]$script:pump2.BeginInvoke()

            $script:result2 = Invoke-Checker -ScriptArgs @(
                '-AdoOrganizationUri', "http://127.0.0.1:$($script:port2)/",
                '-AdoProject', 'pde-oss',
                '-AdoAccessToken', 'irrelevant',
                '-ServiceConnections', 'missing-connection'
            )
        }

        AfterAll {
            if ($script:listener2) { $script:listener2.Stop(); $script:listener2.Close() }
            if ($script:pump2) { $script:pump2.Dispose() }
        }

        It 'warns rather than declaring it missing' {
            # Partial visibility must not recreate build 20260914.1's false failure.
            $script:result2.Output | Should -Match '\[WARN\] Service connection'
            $script:result2.Output | Should -Not -Match '\[FAIL\] Service connection'
        }

        It 'says it cannot tell deleted from not-readable' {
            $script:result2.Output | Should -Match 'cannot tell which'
        }
    }

    Context 'a visible connection that is not ready' {
        BeforeAll {
            # The one definitive bad verdict this check can still produce, so it must survive the
            # move to inconclusive-by-default.
            $script:listener3 = [System.Net.HttpListener]::new()
            $script:port3 = Get-Random -Minimum 16000 -Maximum 16999
            $script:listener3.Prefixes.Add("http://127.0.0.1:$($script:port3)/")
            $script:listener3.Start()

            $script:pump3 = [powershell]::Create()
            [void]$script:pump3.AddScript({
                    param($l)
                    $body = '{"count":1,"value":[{"name":"half-built","type":"azurerm","isReady":false}]}'
                    while ($l.IsListening) {
                        try {
                            $ctx = $l.GetContext()
                            $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                            $ctx.Response.StatusCode = 200
                            $ctx.Response.ContentType = 'application/json'
                            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                            $ctx.Response.Close()
                        }
                        catch { break }
                    }
                }).AddArgument($script:listener3)
            [void]$script:pump3.BeginInvoke()

            $script:result3 = Invoke-Checker -ScriptArgs @(
                '-AdoOrganizationUri', "http://127.0.0.1:$($script:port3)/",
                '-AdoProject', 'pde-oss',
                '-AdoAccessToken', 'irrelevant',
                '-ServiceConnections', 'half-built'
            )
        }

        AfterAll {
            if ($script:listener3) { $script:listener3.Stop(); $script:listener3.Close() }
            if ($script:pump3) { $script:pump3.Dispose() }
        }

        It 'still fails definitively' {
            $script:result3.Output | Should -Match "\[FAIL\] Service connection 'half-built'"
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
