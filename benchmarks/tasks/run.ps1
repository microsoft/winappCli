#Requires -Version 7.4
<#
.SYNOPSIS
    Task-completion benchmark: do the winapp and WinUI agent plugins help Copilot CLI finish real
    Windows app tasks? Each run happens in a fresh Windows Sandbox. See README.md.
#>
[CmdletBinding()]
param(
    [string[]]$Task,
    [string[]]$Configuration,
    [string[]]$Model,
    [int]$Iterations,
    [switch]$Pilot,
    [switch]$Validate,
    [double]$MaxCredits,
    [string]$OutDir,
    [string]$CopilotVersion,
    [string]$Winapp,
    [string]$WinAppPlugin,
    [string]$WinUIPlugin,
    [switch]$KeepSandbox,
    [switch]$Plan,
    [switch]$Lint,
    [string]$Summarize
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'lib\TaskBench.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\agents\lib\Benchmark.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\agents\lib\ScenarioLint.psm1') -Force

function Split-List([string[]]$Value) { if (-not $Value) { return @() } return @($Value | ForEach-Object { $_ -split ',' } | ForEach-Object Trim | Where-Object { $_ }) }

$config = Get-Content -Raw (Join-Path $PSScriptRoot 'config.json') | ConvertFrom-Json
$resolve = { param($p) [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $p)) }
$pluginPaths = [ordered]@{
    winapp = if ($WinAppPlugin) { (Resolve-Path $WinAppPlugin).Path } else { & $resolve $config.plugins.winapp }
    winui  = if ($WinUIPlugin) { (Resolve-Path $WinUIPlugin).Path } else { & $resolve $config.plugins.winui }
}
$allTasks = Get-TaskDefinitions -Root (Join-Path $PSScriptRoot 'tasks')

# --- Summarize an existing results folder ------------------------------------------------
if ($Summarize) {
    $runs = @(Get-Content (Join-Path $Summarize 'runs.jsonl') | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    Write-TaskSummary -Runs $runs -Path (Join-Path $Summarize 'summary.md') -Header @{ 'Results' = $Summarize }
    Write-Host "Wrote $(Join-Path $Summarize 'summary.md')"
    return
}

# --- Lint: prompts must read like a developer and not echo plugin vocabulary --------------
if ($Lint) {
    $corpus = Get-LintCorpus -PluginPaths @($pluginPaths.Values)
    $skillNames = @($pluginPaths.Values | ForEach-Object { Get-PluginSkillNames -PluginPath $_ })
    $errors = 0
    foreach ($t in $allTasks) {
        $issues = @()
        if ($t.Prompt -match '\bwinapp\b') { $issues += 'ERROR names winapp' }
        foreach ($s in $skillNames) { if ($t.Prompt -match [regex]::Escape($s)) { $issues += "ERROR names skill $s" } }
        $leak = Get-ScenarioLeakage -Text $t.Prompt -Corpus $corpus -Allow $t.Allow
        foreach ($b in $leak.SharedBigrams) { $issues += "WARN shares '$($b.Bigram)' with $($b.Documents -join ', ')" }
        if ($leak.MaxJaccard -gt 0.10) { $issues += "WARN Jaccard $([Math]::Round($leak.MaxJaccard, 3)) with $($leak.MaxJaccardDocument)" }
        $errors += @($issues | Where-Object { $_ -like 'ERROR*' }).Count
        Write-Host ("{0,-24} {1}" -f $t.Id, $(if ($issues) { $issues -join '; ' } else { 'ok' }))
    }
    if ($errors) { exit 1 }
    return
}

# --- Run list ------------------------------------------------------------------------------
$tasks = $allTasks
if ($Pilot) { $tasks = @($tasks | Where-Object Pilot) }
$taskFilter = Split-List $Task
if ($taskFilter) {
    $unknown = @($taskFilter | Where-Object { $_ -notin $allTasks.Id })
    if ($unknown) { throw "Unknown task(s): $($unknown -join ', '). Known: $($allTasks.Id -join ', ')" }
    $tasks = @($tasks | Where-Object Id -in $taskFilter)
}
$configs = if ($Configuration) { Split-List $Configuration } else { @($config.configurations) }
foreach ($c in $configs) { [void](Get-ConfigurationSpec $c) }
$models = if ($Model) { Split-List $Model } else { @($config.models) }
$iterationCount = if ($Iterations) { $Iterations } else { [int]$config.iterations }

$runList = [System.Collections.Generic.List[object]]::new()
if ($Validate) {
    # Checker validation: the reference solution must pass and doing nothing must fail.
    foreach ($t in $tasks) {
        foreach ($mode in 'solve', 'noop') {
            if ($mode -eq 'solve' -and -not $t.Solve) { continue }
            $runList.Add([pscustomobject]@{ Task = $t; Configuration = 'none'; Model = $mode; Iteration = 1; Mode = $mode })
        }
    }
}
else {
    foreach ($i in 1..$iterationCount) { foreach ($m in $models) { foreach ($c in $configs) { foreach ($t in $tasks) {
                    $runList.Add([pscustomobject]@{ Task = $t; Configuration = $c; Model = $m; Iteration = $i; Mode = 'agent' })
                } } } }
}

if ($Plan) {
    Write-Host "Tasks ($($tasks.Count)): $($tasks.Id -join ', ')"
    Write-Host "Configurations: $($configs -join ', ')  Models: $($models -join ', ')  Iterations: $iterationCount"
    Write-Host "Sessions: $($runList.Count)  (~$([Math]::Round($runList.Count * [double]$config.creditsPerRunEstimate)) credits at $($config.creditsPerRunEstimate)/run)"
    $runList | Group-Object { $_.Task.Id } | ForEach-Object { Write-Host ("  {0,-24} {1}" -f $_.Name, $_.Count) }
    return
}

# --- Prerequisites ---------------------------------------------------------------------------
$token = $env:COPILOT_GITHUB_TOKEN ? $env:COPILOT_GITHUB_TOKEN : $env:GH_TOKEN
if (-not $Validate -and -not $token) { throw 'Set COPILOT_GITHUB_TOKEN (or GH_TOKEN). It is passed to the sandbox in memory only.' }
$wsb = (Get-Command wsb -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $wsb) { throw 'The Windows Sandbox CLI (wsb) is not available. Enable Windows Sandbox (Windows 11 24H2+) and retry.' }
$wsb = $wsb.Source
$running = @((& $wsb list --raw | ConvertFrom-Json).WindowsSandboxEnvironments | ForEach-Object Id)
if (@($running).Count) { throw "A Windows Sandbox is already running ($(@($running) -join ', ')). Windows allows one at a time; close it first. This script never stops a Sandbox it did not start." }

# --- Tools cache: Copilot CLI build, winapp, SDK tools, harness apps ----------------------------
$cache = Join-Path $env:LOCALAPPDATA 'winapp-task-bench\tools'
New-Item -ItemType Directory -Force $cache | Out-Null
$copilotExe = (Get-Command copilot -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$pinned = if ($CopilotVersion) { $CopilotVersion } elseif ($config.copilotVersion) { $config.copilotVersion } else {
    ((& $copilotExe --version 2>&1 | Out-String) -match '(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)') | Out-Null; $Matches[1].TrimEnd('.')
}
$pkgSource = Join-Path $env:LOCALAPPDATA "copilot\pkg\win32-x64\$pinned"
if (-not (Test-Path $pkgSource)) { throw "Copilot CLI build $pinned is not on this machine ($pkgSource). Run 'copilot --prefer-version $pinned --version' once, or pass -CopilotVersion." }
$copilotCache = Join-Path $cache 'copilot'
$pkgCache = Join-Path $copilotCache "pkg\win32-x64\$pinned"
if (-not (Test-Path "$pkgCache\.extraction-complete")) {
    robocopy $pkgSource $pkgCache /E /XF 'inuse.*.lock' /NFL /NDL /NJH /NJS /NP | Out-Null
}
Copy-Item $copilotExe (Join-Path $copilotCache 'copilot.exe') -Force

$winappDir = if ($Winapp -eq 'release') {
    $rel = Join-Path $cache "winapp-$($config.winapp.release)"
    if (-not (Test-Path "$rel\winapp.exe")) {
        $zip = Join-Path $cache 'winappcli-x64.zip'
        & gh release download $config.winapp.release -R microsoft/winappCli -p 'winappcli-x64.zip' -O $zip --clobber
        Expand-Archive $zip $rel -Force
    }
    $rel
}
elseif ($Winapp) { (Resolve-Path $Winapp).Path }
else { & $resolve $config.winapp.localBuild }
if (-not (Test-Path (Join-Path $winappDir 'winapp.exe'))) { throw "No winapp.exe in $winappDir. Run: dotnet publish src\winapp-CLI\WinApp.Cli -c Release -r win-x64 --self-contained -o artifacts\cli\win-x64 (vswhere must be on PATH), or pass -Winapp release." }
$winappStage = Join-Path $cache 'winapp-current'
robocopy $winappDir $winappStage /MIR /XF *.pdb /NFL /NDL /NJH /NJS /NP | Out-Null
$winappVersion = (& (Join-Path $winappStage 'winapp.exe') --version 2>$null | Select-Object -Last 1)

$sdkCache = Join-Path $cache 'sdk'
if (-not (Test-Path "$sdkCache\makeappx.exe")) {
    $nugetRoot = $env:NUGET_PACKAGES ? $env:NUGET_PACKAGES : (Join-Path $env:USERPROFILE '.nuget\packages')
    $bt = Get-ChildItem (Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem "$($_.FullName)\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $bt) { throw 'Microsoft.Windows.SDK.BuildTools is not in the NuGet cache. Run any winapp packaging command once on this machine.' }
    robocopy $bt.DirectoryName $sdkCache /E /NFL /NDL /NJH /NJS /NP | Out-Null
}
foreach ($app in 'SimpleApp', 'OrderCalculator') {
    $dest = Join-Path $cache "apps\$app"
    if (-not (Test-Path "$dest\$app.exe")) {
        & dotnet build (Join-Path $PSScriptRoot "apps\$app\$app.csproj") -c Release -o $dest -v q -nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Building harness app $app failed." }
    }
}
# Developer machines have the Visual C++ runtime; the Sandbox image does not (Electron's installer
# needs it). Copying this machine's DLLs takes a second; running vc_redist in the Sandbox takes minutes.
$vcCache = Join-Path $cache 'vcruntime'
if (-not (Test-Path "$vcCache\vcruntime140.dll")) {
    New-Item -ItemType Directory -Force $vcCache | Out-Null
    Get-ChildItem "$env:SystemRoot\System32" -File | Where-Object Name -Match '^(vcruntime140(_1|_threads)?|msvcp140(_1|_2|_atomic_wait|_codecvt_ids)?|concrt140|vccorlib140|vcomp140)\.dll$' |
        Copy-Item -Destination $vcCache
}

# --- Output and per-invocation staging --------------------------------------------------------
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "results\$stamp" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$runsPath = Join-Path $OutDir 'runs.jsonl'
$stage = Join-Path $env:LOCALAPPDATA "winapp-task-bench\stage-$stamp"
New-Item -ItemType Directory -Force "$stage\plugins" | Out-Null
$pluginInfo = [ordered]@{}
foreach ($name in $pluginPaths.Keys) {
    robocopy $pluginPaths[$name] "$stage\plugins\$name" /E /NFL /NDL /NJH /NJS /NP | Out-Null
    $sha = (& git -C $pluginPaths[$name] rev-parse HEAD 2>$null)
    $dirty = [bool](& git -C $pluginPaths[$name] status --porcelain -- . 2>$null)
    $pluginInfo[$name] = [ordered]@{ path = $pluginPaths[$name]; sha = $sha; dirty = $dirty; skills = @(Get-PluginSkillNames -PluginPath $pluginPaths[$name]) }
}
$info = [ordered]@{
    started        = (Get-Date).ToString('o')
    copilotVersion = $pinned
    winapp         = "$winappVersion ($winappDir)"
    models         = $models
    configurations = $configs
    iterations     = $iterationCount
    validate       = [bool]$Validate
    tasks          = @($tasks.Id)
    plugins        = $pluginInfo
    maxCredits     = $MaxCredits ? $MaxCredits : $null
}
$info | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'run-info.json') -Encoding utf8NoBOM

# --- Sandbox plumbing ---------------------------------------------------------------------------
function Invoke-Wsb {
    param([string[]]$Arguments, [int]$TimeoutSeconds = 120, [string]$LogName)
    $logDir = Join-Path $stage 'wsb-logs'; New-Item -ItemType Directory -Force $logDir | Out-Null
    $name = $LogName ? $LogName : "wsb-$(Get-Random)"
    $envMap = [ordered]@{}; foreach ($e in [Environment]::GetEnvironmentVariables().GetEnumerator()) { $envMap[[string]$e.Key] = [string]$e.Value }
    $r = Invoke-LoggedProcess -FilePath $wsb -Arguments $Arguments -WorkingDirectory $stage -Environment $envMap `
        -StdoutPath "$logDir\$name.out" -StderrPath "$logDir\$name.err" -TimeoutSeconds $TimeoutSeconds
    $text = (Get-Content -Raw "$logDir\$name.out" -ErrorAction SilentlyContinue) + (Get-Content -Raw "$logDir\$name.err" -ErrorAction SilentlyContinue)
    Remove-Item "$logDir\$name.out", "$logDir\$name.err" -ErrorAction SilentlyContinue
    $guestExit = if ($text -match '"ExitCode":\s*(-?\d+)') { [int]$Matches[1] } else { $null }
    return [pscustomobject]@{ ExitCode = $r.ExitCode; TimedOut = $r.TimedOut; Text = $text; GuestExitCode = $guestExit; DurationMs = $r.DurationMs }
}

$pwshGuest = '"C:\Program Files\PowerShell\7\pwsh.exe" -NoProfile -ExecutionPolicy Bypass'

function Start-BenchSandbox {
    param([object[]]$Maps)
    $xml = New-SandboxConfiguration -Maps $Maps -MemoryMB ([int]$config.sandboxMemoryMB)
    $r = Invoke-Wsb -Arguments @('start', '--config', $xml, '--raw') -TimeoutSeconds 300 -LogName 'start'
    if ($r.Text -notmatch '"Id":\s*"([^"]+)"') { throw "wsb start failed: $($r.Text)" }
    $id = $Matches[1]
    Confirm-BenchLogin $id
    return $id
}

function Confirm-BenchLogin {
    # The client connection creates the interactive user session that GUI, MSIX, and UI tasks need.
    # Reconnects when the session is gone (for example, someone closed the Sandbox window).
    param([string]$Id)
    $probe = { (Invoke-Wsb -Arguments @('exec', '--id', $Id, '-r', 'ExistingLogin', '-c', 'cmd /c exit 0', '--raw') -TimeoutSeconds 60).GuestExitCode -eq 0 }
    if (& $probe) { return }
    Start-Process -FilePath $wsb -ArgumentList 'connect', '--id', $Id -WindowStyle Minimized | Out-Null
    $deadline = (Get-Date).AddMinutes(4)
    while ((Get-Date) -lt $deadline) {
        if (& $probe) { return }
        Start-Sleep -Seconds 2
    }
    throw "Sandbox $Id never reached a signed-in session."
}

function Stop-BenchSandbox {
    param([string]$Id)
    if (-not $Id) { return }
    [void](Invoke-Wsb -Arguments @('stop', '--id', $Id) -TimeoutSeconds 180)
    foreach ($i in 1..60) {
        $left = @((& $wsb list --raw | ConvertFrom-Json).WindowsSandboxEnvironments | ForEach-Object Id)
        if (-not (@($left) -contains $Id)) { return }
        Start-Sleep -Seconds 2
    }
    Write-Warning "Sandbox $Id still listed after stop."
}

# --- Run ------------------------------------------------------------------------------------------
$spent = 0.0
$measured = [System.Collections.Generic.List[double]]::new()
$records = [System.Collections.Generic.List[object]]::new()
$index = 0
$sandboxId = $null
try {
    foreach ($run in $runList) {
        $index++
        if ($MaxCredits -and $spent -ge $MaxCredits) { Write-Warning "Stopping: spent $([Math]::Round($spent, 1)) of -MaxCredits $MaxCredits."; break }
        $t = $run.Task
        $spec = Get-ConfigurationSpec $run.Configuration
        $runId = '{0:D3}-{1}-{2}-{3}-{4}' -f $index, $t.Id, $run.Configuration, ($run.Model -replace '[^\w.-]', '_'), $run.Iteration
        Write-Host "[$index/$($runList.Count)] $runId" -ForegroundColor Cyan
        $runOut = Join-Path $OutDir "runs\$runId"
        $in = Join-Path $stage "$runId\in"
        $check = Join-Path $stage "$runId\check"
        New-Item -ItemType Directory -Force $runOut, $in, $check | Out-Null
        if ($t.Fixture) { Copy-Item $t.Fixture (Join-Path $in 'fixture') -Recurse }
        if ($t.Setup) { Copy-Item $t.Setup (Join-Path $in 'setup.ps1') }
        if ($run.Mode -eq 'solve') { Copy-Item $t.Solve (Join-Path $in 'solve.ps1') }
        Copy-Item $t.Check (Join-Path $check 'check.ps1')
        [ordered]@{
            runId = $runId; task = $t.Id; mode = $run.Mode; prompt = $t.Prompt; model = $run.Model; agent = $spec.Agent
            plugins = @($spec.Plugins); copilotVersion = $pinned; timeoutMinutes = $t.TimeoutMinutes; workspace = $t.Workspace
        } | ConvertTo-Json | Set-Content (Join-Path $in 'run.json') -Encoding utf8NoBOM

        $record = [ordered]@{
            runId = $runId; task = $t.Id; cluster = $t.Cluster; configuration = $run.Configuration; model = $run.Model; agent = $spec.Agent
            iteration = $run.Iteration; mode = $run.Mode; taskStatus = $null; taskReason = $null; checks = @(); durationMs = $null; agentExitCode = $null
            timedOut = $null; setupMs = $null; aiCredits = $null; premiumRequests = $null; tokens = $null; modelTurns = $null; toolCalls = $null
            skillsLoaded = @(); skillContextTokensApprox = $null; skillRepeatDeliveries = $null; pluginFileReads = @(); pluginReadChars = $null
            winappCommandsRun = @(); winappCommandsNamed = @(); shellCommandCount = $null; failedToolCalls = $null; unsafeActions = @()
            preflight = $null; selectedAgent = $null; finalResponse = $null; sandboxMs = $null; error = $null
        }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $maps = @(
                @{ Host = $copilotCache; Sandbox = 'C:\bench\harness\copilot'; ReadOnly = $true }
                @{ Host = $sdkCache; Sandbox = 'C:\bench\harness\sdk'; ReadOnly = $true }
                @{ Host = (Join-Path $cache 'apps'); Sandbox = 'C:\bench\harness\apps'; ReadOnly = $true }
                @{ Host = $vcCache; Sandbox = 'C:\bench\harness\vcruntime'; ReadOnly = $true }
                @{ Host = (Join-Path $PSScriptRoot 'lib'); Sandbox = 'C:\bench\harness\lib'; ReadOnly = $true }
                @{ Host = (Join-Path $PSScriptRoot 'sandbox'); Sandbox = 'C:\bench\harness\sandbox'; ReadOnly = $true }
                @{ Host = "$stage\plugins"; Sandbox = 'C:\bench\harness\plugins'; ReadOnly = $true }
                @{ Host = $winappStage; Sandbox = 'C:\Program Files\WinAppCli'; ReadOnly = $true }
                @{ Host = 'C:\Program Files\dotnet'; Sandbox = 'C:\Program Files\dotnet'; ReadOnly = $true }
                @{ Host = 'C:\Program Files\nodejs'; Sandbox = 'C:\Program Files\nodejs'; ReadOnly = $true }
                @{ Host = 'C:\Program Files\Git'; Sandbox = 'C:\Program Files\Git'; ReadOnly = $true }
                @{ Host = 'C:\Program Files\PowerShell\7'; Sandbox = 'C:\Program Files\PowerShell\7'; ReadOnly = $true }
                @{ Host = $in; Sandbox = 'C:\bench\in'; ReadOnly = $true }
                @{ Host = $runOut; Sandbox = 'C:\bench\out'; ReadOnly = $false }
            )
            $sandboxId = Start-BenchSandbox -Maps $maps
            $prep = Invoke-Wsb -Arguments @('exec', '--id', $sandboxId, '-r', 'System', '-c', "$pwshGuest -File C:\bench\harness\sandbox\prepare-system.ps1", '--raw') -TimeoutSeconds 300
            if ($prep.GuestExitCode -ne 0) { throw "prepare-system failed: $($prep.Text)" }

            # The token travels only on the guest command line and in the agent's environment.
            Confirm-BenchLogin $sandboxId
            $tokenSet = if ($token) { "`$env:COPILOT_GITHUB_TOKEN='$token'; " } else { '' }
            $agentCmd = "$pwshGuest -Command `"$tokenSet& C:\bench\harness\sandbox\run-agent.ps1`""
            $a = Invoke-Wsb -Arguments @('exec', '--id', $sandboxId, '-r', 'ExistingLogin', '-c', $agentCmd, '--raw') -TimeoutSeconds (($t.TimeoutMinutes + 20) * 60)
            if ($a.TimedOut) { $record.error = 'host timeout waiting for the agent phase' }

            Confirm-BenchLogin $sandboxId
            [void](Invoke-Wsb -Arguments @('share', '--id', $sandboxId, '-f', $check, '-s', 'C:\bench\check', '--raw') -TimeoutSeconds 120)
            $c = Invoke-Wsb -Arguments @('exec', '--id', $sandboxId, '-r', 'ExistingLogin', '-c', "$pwshGuest -File C:\bench\harness\sandbox\run-check.ps1", '--raw') -TimeoutSeconds 1800
            if ($c.TimedOut) { $record.error = (@($record.error, 'checker timed out') | Where-Object { $_ }) -join '; ' }
        }
        catch { $record.error = $_.Exception.Message }
        finally {
            if (-not $KeepSandbox) { Stop-BenchSandbox $sandboxId; $sandboxId = $null }
            $record.sandboxMs = $sw.ElapsedMilliseconds
        }

        # --- Collect -----------------------------------------------------------------------------
        [void](Remove-SecretFromFiles -Path $runOut -Secret $token)
        $agentRun = Join-Path $runOut 'agent-run.json'
        if (Test-Path $agentRun) {
            $ar = Get-Content -Raw $agentRun | ConvertFrom-Json
            $record.durationMs = $ar.durationMs; $record.agentExitCode = $ar.exitCode; $record.timedOut = $ar.timedOut; $record.setupMs = $ar.setupMs
            if ($ar.error) { $record.error = (@($record.error, "agent phase: $($ar.error)") | Where-Object { $_ }) -join '; ' }
        }
        $skillList = Join-Path $runOut 'skill-list.out'
        if (Test-Path $skillList) {
            $expected = @($spec.Plugins | ForEach-Object { $pluginInfo[$_].skills })
            $listed = @(Get-Content -Raw $skillList | ConvertFrom-Json)
            $pf = Compare-PreflightSkills -Expected $expected -Listed $listed -CopilotHome 'C:\bench-home\copilot'
            $record.preflight = [ordered]@{ unexpected = $pf.Unexpected; missing = $pf.Missing }
        }
        $events = Join-Path $runOut 'events.jsonl'
        $ev = Read-SessionEvents -Path ((Test-Path $events) ? $events : '')
        $act = Read-ToolActivity -Path ((Test-Path $events) ? $events : '')
        $record.aiCredits = $ev.aiCredits; $record.premiumRequests = $ev.premiumRequests; $record.tokens = $ev.tokens
        $record.modelTurns = $ev.modelTurns; $record.toolCalls = $ev.toolCalls; $record.selectedAgent = $ev.selectedAgent
        $record.skillsLoaded = @($ev.skillsInvoked | ForEach-Object { Get-BareSkillName $_.name } | Select-Object -Unique)
        $record.skillContextTokensApprox = $ev.skillContextTokensApprox; $record.skillRepeatDeliveries = $ev.skillRepeatDeliveries
        $record.pluginFileReads = @($act.pluginFileReads); $record.pluginReadChars = $act.pluginReadChars
        $record.winappCommandsRun = @(Get-WinappCommands -Text @($act.shellCommands))
        $record.winappCommandsNamed = @($ev.winappCommands)
        $record.shellCommandCount = $act.shellCommands.Count; $record.failedToolCalls = $act.failedToolCalls
        $record.finalResponse = $ev.finalResponse
        $statePath = Join-Path $runOut 'state.json'
        $state = (Test-Path $statePath) ? (Get-Content -Raw $statePath | ConvertFrom-Json) : $null
        $record.unsafeActions = @(Get-UnsafeActions -Commands @($act.shellCommands) -State $state)
        $checkPath = Join-Path $runOut 'check.json'
        if (Test-Path $checkPath) {
            $cj = Get-Content -Raw $checkPath | ConvertFrom-Json
            $record.taskStatus = $cj.status; $record.taskReason = $cj.why; $record.checks = @($cj.checks)
        }
        else { $record.taskStatus = 'harness_error'; $record.taskReason = $record.error ? $record.error : 'no check.json' }
        if ($record.preflight -and (@($record.preflight.unexpected).Count -or @($record.preflight.missing).Count)) {
            $record.taskStatus = 'preflight_failed'; $record.taskReason = "skills unexpected: [$($record.preflight.unexpected -join ', ')], missing: [$($record.preflight.missing -join ', ')]"
        }
        if ($run.Mode -eq 'agent' -and $record.timedOut) { $record.taskReason = "agent timed out after $($t.TimeoutMinutes) min; $($record.taskReason)" }
        if ($run.Mode -eq 'agent' -and -not (Test-Path $events) -and $record.taskStatus -ne 'preflight_failed') { $record.taskStatus = 'harness_error'; $record.taskReason = "no events.jsonl; $($record.error)" }

        if ($run.Mode -eq 'agent') {
            if ($null -ne $record.aiCredits) { $measured.Add([double]$record.aiCredits); $spent += [double]$record.aiCredits }
            else { $spent += ($measured.Count ? ($measured | Measure-Object -Average).Average : [double]$config.creditsPerRunEstimate) }
        }
        $records.Add([pscustomobject]$record)
        ([pscustomobject]$record | ConvertTo-Json -Depth 8 -Compress) | Add-Content -Path $runsPath -Encoding utf8NoBOM
        $color = switch ($record.taskStatus) { 'pass' { 'Green' } 'partial' { 'Yellow' } default { 'Red' } }
        Write-Host ("  -> {0} in {1:0.0} min, {2} credits, skills [{3}], winapp [{4}]{5}" -f $record.taskStatus, ($record.durationMs / 60000), $record.aiCredits,
            ($record.skillsLoaded -join ', '), ($record.winappCommandsRun -join ', '), $(if ($record.taskStatus -ne 'pass') { " — $($record.taskReason)" })) -ForegroundColor $color
        Remove-Item -Recurse -Force (Join-Path $stage $runId) -ErrorAction SilentlyContinue
    }
}
finally {
    if ($sandboxId -and -not $KeepSandbox) { Stop-BenchSandbox $sandboxId }
    if ($records.Count) {
        Write-TaskSummary -Runs @($records) -Path (Join-Path $OutDir 'summary.md') -Header ([ordered]@{
                'Copilot CLI' = $pinned; 'winapp' = $winappVersion; 'Credits spent' = [Math]::Round($spent, 1); 'Results' = $OutDir })
        Write-Host "Summary: $(Join-Path $OutDir 'summary.md')"
    }
    if (-not $KeepSandbox) { Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue }
}
