$ErrorActionPreference = 'Stop'
$skillPath = Join-Path $PSScriptRoot '..\..\plugins\winui\agent-plugin\skills\winui-ui-testing\SKILL.md'
$content = Get-Content -LiteralPath $skillPath -Raw
$blocks = @([regex]::Matches($content, '(?ms)^```powershell\r?\n(.*?)^```'))
if (-not $blocks.Count) { throw 'No PowerShell examples found.' }
foreach ($block in $blocks) {
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput(
        $block.Groups[1].Value, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors.Message -join "`n") }
    foreach ($command in $ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'winapp'
    }, $true)) {
        $parent = $command.Parent
        while ($parent -and $parent -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) {
            $parent = $parent.Parent
        }
        if (-not $parent -or $parent.Name -ne 'Invoke-WinAppChecked') {
            throw 'An example bypasses the checked/scoped command helper.'
        }
    }
}
$batch = @($blocks | Where-Object { $_.Groups[1].Value -match '# ui-tests\.ps1' })
if ($batch.Count -ne 1) { throw 'Expected exactly one ui-tests.ps1 template.' }
$source = $batch[0].Groups[1].Value
if ($source -notmatch "\[Parameter\(Mandatory\)\]\[ValidateSet\('sandbox', 'local'\)\]\[string\]\`$Target" -or
    $source -match "\`$Target = 'sandbox'") {
    throw 'Batch testing must use the target selected before execution, not impose a Sandbox default.'
}
if ($content -match '2>\$null|BuildAndRun\.ps1|\$inspection\.elements') {
    throw 'Unsafe error suppression, legacy rebuild instruction, or obsolete inspection schema.'
}
if ($source -notmatch 'UiTargetArgs' -or $source -notmatch 'ProcessScope' -or
    $source -notmatch '\$inspection\.windows' -or $source -notmatch 'WINAPP_UI_WORKFLOW_ID') {
    throw 'Missing scoped target, workflow, or windows/elements contract.'
}

# Verify actual native exit scoping too: a local $LASTEXITCODE sentinel would
# mask the global value set by a native process, something a mock alone misses.
$tokens = $null; $parseErrors = $null
$batchAst = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
$checkedHelper = $batchAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-WinAppChecked'
}, $true)
. ([scriptblock]::Create($checkedHelper.Extent.Text))
Set-Alias -Name winapp -Value (Join-Path $PSHOME 'pwsh.exe') -Scope Local
try {
    $global:LASTEXITCODE = 99
    Invoke-WinAppChecked @('-NoProfile', '-Command', 'exit 0')
    $threw = $false
    try { Invoke-WinAppChecked @('-NoProfile', '-Command', 'exit 7') } catch { $threw = $true }
    if (-not $threw) { throw 'Native failure was not enforced by the template helper.' }
} finally {
    Remove-Item Alias:\winapp
}
$additions = ($blocks | Where-Object { $_.Groups[1].Value.TrimStart().StartsWith('Test-UI ') } |
    ForEach-Object { $_.Groups[1].Value }) -join "`n"
if (-not $additions) { throw 'Expected scoped advanced/picker/persistence examples.' }

# A child PowerShell process supplies a mock winapp function. No real CLI, app,
# Sandbox, user session, input, or capture is touched by these tests.
$scratch = Join-Path $PSScriptRoot ('.ui-sandbox-regression-' + [guid]::NewGuid().ToString('N'))
$driver = @'
param([string]$Case, [string]$Template)
$ErrorActionPreference = 'Stop'
$global:Calls = [Collections.Generic.List[object]]::new()
$global:PickerOpened = $false
function global:winapp {
    $arguments = @($args)
    $global:Calls.Add(@{ arguments = $arguments; workflowId = $env:WINAPP_UI_WORKFLOW_ID })
    if ($Case -ne 'stale-exit') { $global:LASTEXITCODE = 0 }
    if ($arguments[0] -eq 'run') {
        if ($Case -in 'launch-error', 'local-launch-error') { $global:LASTEXITCODE = 7; return 'launch failed' }
        if ($Case -eq 'missing-pid') { return '{"Sandbox":true,"ProcessScope":"sandbox","ExecutionTarget":{"selector":"sandbox"}}' }
        if ($Case -eq 'wrong-scope') { return '{"ProcessId":321,"ProcessScope":"local","UiTargetArgs":"-a 321"}' }
        if ($Case -eq 'local') { return '{"ProcessId":321}' }
        return '{"ProcessId":321,"ProcessScope":"sandbox","UiTargetArgs":"--on sandbox -a 321"}'
    }
    if ($arguments[0] -eq 'target') {
        if ($arguments[1] -eq 'snapshot') { return '{"workRoot":"C:\\WinApp\\work"}' }
        if ($arguments[1] -eq 'exec' -and '-Command' -in $arguments) { return '{"UserName":"TestUser"}' }
        return
    }
    if ($arguments[0] -ne 'ui') { throw 'Unexpected mock command.' }
    $verb = $arguments[1]
    if (($Case -eq 'early-native-failure' -and $verb -eq 'set-value') -or
        ($Case -eq 'inspect-error' -and $verb -eq 'inspect') -or
        ($Case -eq 'screenshot-error' -and $verb -eq 'screenshot') -or
        ($Case -eq 'yield-error' -and $verb -eq 'yield')) {
        $global:LASTEXITCODE = 7
        return 'mock native failure'
    }
    switch ($verb) {
        'list-windows' {
            if ($Case -eq 'empty-window') { return '[]' }
            if ($Case -eq 'malformed-window') { return 'not JSON' }
            if ($global:PickerOpened -and '-a' -notin $arguments) {
                return '[{"hwnd":"123","title":"App","processId":321},{"hwnd":"789","title":"Open","processId":999,"ownerHwnd":999},{"hwnd":"456","title":"Open","processId":654,"ownerHwnd":123}]'
            }
            return '[{"hwnd":"123","title":"App"},{"hwnd":"124","title":"Secondary"}]'
        }
        'inspect' {
            if ($Case -eq 'empty-inspection') { return '{"windows":[]}' }
            if ($Case -eq 'obsolete-inspection') { return '{"elements":[{"type":"Button","automationId":"BtnSave"}]}' }
            if ($Case -eq 'no-app-controls') { return '{"windows":[{"elements":[{"type":"Window"}]}]}' }
            if ($Case -eq 'missing-id') { return '{"windows":[{"elements":[{"type":"Button","name":"Save"}]}]}' }
            if ($Case -eq 'nested-missing-id') { return '{"windows":[{"elements":[{"type":"TabItem","automationId":"NavHome","children":[{"type":"Button","name":"Save"}]}]}]}' }
            if ($Case -eq 'nested-valid-id') { return '{"windows":[{"elements":[{"type":"Window","children":[{"type":"Button","name":"Save","automationId":"BtnSave"}]}]}]}' }
            return '{"windows":[{"elements":[{"type":"Button","name":"Save","automationId":"BtnSave","className":"Button"}]}]}'
        }
        'invoke' { if ($arguments[2] -eq 'BtnOpenFile') { $global:PickerOpened = $true } }
        'get-value' { return '{"text":"TestUser"}' }
        { $_ -in 'screenshot', 'record' } {
            if ($Case -eq 'undelivered-capture') { return }
            $path = $arguments[[array]::IndexOf($arguments, '--output') + 1]
            if ($verb -eq 'screenshot' -and '-w' -notin $arguments) {
                $path = Join-Path (Split-Path $path) ([IO.Path]::GetFileNameWithoutExtension($path) + '-123.png')
            }
            [IO.File]::WriteAllBytes($path, [byte[]]@(1, 2, 3, 4))
        }
    }
}
$env:WINAPP_UI_WORKFLOW_ID = 'outer-workflow'
$parameters = @{ WorkflowId = 'regression-flow'; Target = 'sandbox' }
if ($Case -in 'local', 'local-reuse', 'local-launch-error') { $parameters.Target = 'local' }
if ($Case -eq 'local-reuse') { $parameters.AppPid = 321; $parameters.AppScope = 'local' }
if ($Case -eq 'reuse') { $parameters.AppPid = 321; $parameters.AppScope = 'sandbox' }
if ($Case -eq 'reuse-mismatch') { $parameters.AppPid = 321; $parameters.AppScope = 'local' }
& $Template @parameters
$code = $LASTEXITCODE
@{ calls = @($global:Calls.ToArray()); restoredWorkflowId = $env:WINAPP_UI_WORKFLOW_ID } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath '.\calls.json'
exit $code
'@
$cases = @(
    @{ Name = 'pass'; Exit = 0 }
    @{ Name = 'local'; Exit = 0 }
    @{ Name = 'local-reuse'; Exit = 0 }
    @{ Name = 'local-launch-error'; Exit = 1 }
    @{ Name = 'reuse'; Exit = 0 }
    @{ Name = 'extensions'; Exit = 0 }
    @{ Name = 'early-native-failure'; Exit = 1 }
    @{ Name = 'empty-window'; Exit = 1 }
    @{ Name = 'malformed-window'; Exit = 1 }
    @{ Name = 'empty-inspection'; Exit = 1 }
    @{ Name = 'obsolete-inspection'; Exit = 1 }
    @{ Name = 'no-app-controls'; Exit = 1 }
    @{ Name = 'missing-id'; Exit = 1 }
    @{ Name = 'nested-missing-id'; Exit = 1 }
    @{ Name = 'nested-valid-id'; Exit = 0 }
    @{ Name = 'inspect-error'; Exit = 1 }
    @{ Name = 'screenshot-error'; Exit = 1 }
    @{ Name = 'undelivered-capture'; Exit = 1 }
    @{ Name = 'yield-error'; Exit = 1 }
    @{ Name = 'wrong-scope'; Exit = 1 }
    @{ Name = 'missing-pid'; Exit = 1 }
    @{ Name = 'launch-error'; Exit = 1 }
    @{ Name = 'reuse-mismatch'; Exit = 1 }
    @{ Name = 'stale-exit'; Exit = 1 }
)
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $driverPath = Join-Path $scratch 'mock-driver.ps1'
    $driver | Set-Content -LiteralPath $driverPath
    $templatePath = Join-Path $scratch 'ui-tests.ps1'
    $source | Set-Content -LiteralPath $templatePath
    $extendedPath = Join-Path $scratch 'extended-ui-tests.ps1'
    $source.Replace("    Test-UI 'Final screenshot delivered'", "$additions`n    Test-UI 'Final screenshot delivered'") |
        Set-Content -LiteralPath $extendedPath
    foreach ($case in $cases) {
        $directory = Join-Path $scratch $case.Name
        New-Item -ItemType Directory -Path $directory | Out-Null
        Push-Location $directory
        try {
            $template = if ($case.Name -eq 'extensions') { $extendedPath } else { $templatePath }
            $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $driverPath -Case $case.Name -Template $template 2>&1
            if ($LASTEXITCODE -ne $case.Exit) { throw "$($case.Name): unexpected exit $LASTEXITCODE. $output" }
            $report = Get-Content -LiteralPath '.\test-results.json' -Raw | ConvertFrom-Json
            $log = Get-Content -LiteralPath '.\calls.json' -Raw | ConvertFrom-Json
            if (($report.failed -gt 0) -ne ($case.Exit -ne 0)) { throw "$($case.Name): false PASS/FAIL report." }
            if ($case.Name -eq 'missing-pid' -and
                -not @($report.results | Where-Object { $_.detail -match 'discover the app in that target' }).Count) {
                throw 'Scope-only unpackaged guest launch must explain how to supply the app PID.'
            }
            if ($log.restoredWorkflowId -ne 'outer-workflow') { throw "$($case.Name): workflow ID leaked." }
            foreach ($call in $log.calls) {
                $arguments = @($call.arguments)
                if ($call.workflowId -ne 'regression-flow') { throw 'Cooperating command lost workflow identity.' }
                if ($arguments[0] -notin 'ui', 'run') { continue }
                if ($case.Name -in 'local', 'local-reuse', 'local-launch-error') {
                    if ('--on' -in $arguments) { throw 'Selected local mode unexpectedly routed remotely.' }
                } else {
                    $onIndex = [array]::IndexOf($arguments, '--on')
                    if ($onIndex -lt 0 -or $arguments[$onIndex + 1] -ne 'sandbox') {
                        throw "Unscoped guest command: $($arguments -join ' ')"
                    }
                }
                if ($arguments[0] -eq 'ui' -and $arguments[1] -ne 'yield') {
                    if ($arguments[1] -eq 'screenshot' -and
                        ('-w' -notin $arguments -or $arguments[[array]::IndexOf($arguments, '-w') + 1] -ne '123')) {
                        throw 'Screenshot must select the known main window for an exact output path.'
                    }
                    if ($case.Name -eq 'extensions' -and $arguments[1] -eq 'list-windows' -and '-a' -notin $arguments) {
                        if ('-w' -in $arguments) { throw 'Picker discovery must enumerate the target, not one window.' }
                    } elseif ('-w' -in $arguments) {
                        if ('-a' -in $arguments) { throw 'Window calls must not carry an app selector too.' }
                    } elseif ('-a' -notin $arguments -or $arguments[[array]::IndexOf($arguments, '-a') + 1] -ne '321') {
                        throw 'UI command lost app PID.'
                    }
                }
            }
            if ($case.Name -eq 'early-native-failure' -and
                @($log.calls | Where-Object { $_.arguments[1] -eq 'invoke' -and $_.arguments[2] -eq 'BtnSave' }).Count) {
                throw 'A multi-command test continued after its first native failure.'
            }
            if ($case.Name -in 'reuse', 'local-reuse' -and @($log.calls | Where-Object { $_.arguments[0] -eq 'run' }).Count) {
                throw 'A valid scoped PID was unnecessarily relaunched.'
            }
            if ($case.Name -eq 'reuse-mismatch' -and @($log.calls).Count) { throw 'Mismatched PID reached the CLI.' }
            if ($case.Exit -eq 0) {
                if (@($report.screenshots).Count -lt 2) { throw 'Evidence paths were not reported.' }
                foreach ($image in $report.screenshots) {
                    if (-not (Test-Path -LiteralPath $image -PathType Leaf)) { throw 'Evidence not on host.' }
                }
            }
            if ($case.Name -eq 'extensions') {
                foreach ($verb in 'send-keys', 'hover', 'drag', 'touch', 'pen', 'record') {
                    if (-not @($log.calls | Where-Object { $_.arguments[1] -eq $verb }).Count) {
                        throw "Advanced example did not exercise $verb."
                    }
                }
                if (-not @($log.calls | Where-Object { '-w' -in $_.arguments -and '456' -in $_.arguments }).Count) {
                    throw 'Picker HWND was not exercised.'
                }
                if (-not @($log.calls | Where-Object { $_.arguments[1] -eq 'list-windows' -and '-a' -notin $_.arguments }).Count) {
                    throw 'Picker discovery must enumerate windows beyond the app PID.'
                }
                if (@($log.calls | Where-Object { '-w' -in $_.arguments -and '789' -in $_.arguments }).Count) {
                    throw 'A picker not owned by the app was selected.'
                }
                $exec = @($log.calls | Where-Object { $_.arguments[0] -eq 'target' -and $_.arguments[1] -eq 'exec' })
                if ($exec.Count -ne 2) { throw 'Guest persistence/setup did not execute in target.' }
                foreach ($transfer in @($log.calls | Where-Object { $_.arguments[1] -in 'push', 'pull' })) {
                    $guestPath = if ($transfer.arguments[1] -eq 'push') { $transfer.arguments[4] } else { $transfer.arguments[3] }
                    if ([IO.Path]::IsPathRooted($guestPath)) { throw 'Guest transfer path must be workRoot-relative.' }
                }
            }
        } finally {
            Pop-Location
        }
    }
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
Write-Host "PASS: $($blocks.Count) PowerShell examples parse; $($cases.Count) mocked UI scenarios, routing, evidence, and failure checks."
# Expected failing child cases leave LASTEXITCODE nonzero; Actions propagates it.
exit 0
