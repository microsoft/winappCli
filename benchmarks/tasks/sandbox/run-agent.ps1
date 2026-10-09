# Runs inside Windows Sandbox as the signed-in user: prepares the task, records the baseline,
# installs the configuration's plugins into an isolated Copilot home, and runs the agent.
# Input: C:\bench\in\run.json. Output: C:\bench\out. The token arrives only in the environment.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$in = 'C:\bench\in'
$out = 'C:\bench\out'
$harness = 'C:\bench\harness'
$run = Get-Content -Raw "$in\run.json" | ConvertFrom-Json
Import-Module "$harness\lib\TaskCheck.psm1" -Force

$log = "$out\agent-run.log"
function Write-Log([string]$Message) { "$(Get-Date -Format 'HH:mm:ss.fff') $Message" | Add-Content -LiteralPath $log -Encoding utf8NoBOM }

$result = [ordered]@{ phase = 'start'; setupMs = $null; preflight = $null; exitCode = $null; timedOut = $false; durationMs = $null; error = $null }
function Save-Result { $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$out\agent-run.json" -Encoding utf8NoBOM }

function Invoke-Timed {
    # Starts a process with output streamed to files; kills the whole tree on timeout.
    param([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [string]$Name, [int]$TimeoutSeconds)
    $psi = [System.Diagnostics.ProcessStartInfo]::new($File)
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $o = [System.IO.File]::Create("$out\$Name.out")
    $e = [System.IO.File]::Create("$out\$Name.err")
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $p = [System.Diagnostics.Process]::Start($psi)
        $p.StandardInput.Close()
        $c1 = $p.StandardOutput.BaseStream.CopyToAsync($o)
        $c2 = $p.StandardError.BaseStream.CopyToAsync($e)
        $timedOut = -not $p.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { try { $p.Kill($true) } catch { }; [void]$p.WaitForExit(30000) } else { $p.WaitForExit() }
        [void][System.Threading.Tasks.Task]::WaitAll(@($c1, $c2), 30000)
        return [pscustomobject]@{ ExitCode = if ($p.HasExited) { $p.ExitCode } else { $null }; TimedOut = $timedOut; DurationMs = $sw.ElapsedMilliseconds }
    }
    finally { $o.Dispose(); $e.Dispose() }
}

try {
    # --- Environment: a developer machine with .NET, Node, Git, PowerShell 7, and winapp installed.
    $env:PATH = "C:\Program Files\WinAppCli;C:\Program Files\dotnet;C:\Program Files\nodejs;C:\Program Files\Git\cmd;C:\Program Files\PowerShell\7;$env:APPDATA\npm;$env:LOCALAPPDATA\Microsoft\WindowsApps;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
    $env:WINAPP_CLI_UPDATE_CHECK = '0'
    $env:NO_COLOR = '1'

    # --- Copilot CLI: the launcher plus the pinned build, so --prefer-version resolves offline.
    $pkg = "$env:LOCALAPPDATA\copilot\pkg\win32-x64"
    New-Item -ItemType Directory -Force $pkg | Out-Null
    robocopy "$harness\copilot\pkg\win32-x64" $pkg /E /NFL /NDL /NJH /NJS /NP | Out-Null
    $copilotHome = 'C:\bench-home\copilot'
    New-Item -ItemType Directory -Force $copilotHome | Out-Null
    Set-Content "$copilotHome\config.json" '{"autoUpdate": false}'
    $env:COPILOT_HOME = $copilotHome
    $env:COPILOT_AUTO_UPDATE = 'false'
    $copilot = "$harness\copilot\copilot.exe"
    $prefix = @('--prefer-version', $run.copilotVersion)

    # --- Workspace and task setup.
    $result.phase = 'setup'
    $ws = $run.workspace
    New-Item -ItemType Directory -Force $ws | Out-Null
    if (Test-Path "$in\fixture") { Copy-Item -Path "$in\fixture\*" -Destination $ws -Recurse -Force }
    & winapp --version *> $null   # first-run notice now, not in the agent's first winapp call
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if (Test-Path "$in\setup.ps1") {
        Write-Log 'setup.ps1'
        & "$in\setup.ps1" -Workspace $ws *>&1 | Out-File -Append "$out\setup.log" -Encoding utf8NoBOM
    }
    $result.setupMs = $sw.ElapsedMilliseconds

    # --- Baseline state, compared after the run to find unsafe or destructive changes.
    New-Item -ItemType Directory -Force 'C:\bench-state' | Out-Null
    $baseline = [ordered]@{
        packages     = @(Get-InstalledPackages | ForEach-Object PackageFullName)
        certificates = @(Get-CertificateSnapshot)
        security     = Get-SecuritySettings
        files        = @(Get-ChildItem -LiteralPath $ws -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($ws.Length + 1) })
    }
    $baseline | ConvertTo-Json -Depth 5 | Set-Content 'C:\bench-state\baseline.json' -Encoding utf8NoBOM
    Copy-Item 'C:\bench-state\baseline.json' "$out\baseline.json"

    # --- Plugins and preflight.
    $result.phase = 'preflight'
    foreach ($p in @($run.plugins)) {
        $r = Invoke-Timed -File $copilot -Arguments ($prefix + @('plugin', 'install', "$harness\plugins\$p")) -WorkingDirectory $ws -Name "install-$p" -TimeoutSeconds 180
        if ($r.ExitCode -ne 0) { throw "plugin install '$p' exited $($r.ExitCode): $(Get-Content -Raw "$out\install-$p.err")" }
    }
    $r = Invoke-Timed -File $copilot -Arguments ($prefix + @('skill', 'list', '--json')) -WorkingDirectory $ws -Name 'skill-list' -TimeoutSeconds 180
    if ($r.ExitCode -ne 0) { throw "skill list exited $($r.ExitCode)" }
    $result.preflight = 'listed'

    # --- The agent (or, for checker validation, the reference solution or nothing).
    $result.phase = 'agent'
    Save-Result
    switch ($run.mode) {
        'agent' {
            $agentArgs = @('-p', $run.prompt, '--model', $run.model, '--output-format', 'json', '--stream', 'off',
                '--allow-all', '--no-ask-user', '--disable-builtin-mcps', '--no-custom-instructions', '--no-remote')
            if ($run.agent) { $agentArgs += @('--agent', $run.agent) }
            if ($run.reasoningEffort) { $agentArgs += @('--reasoning-effort', $run.reasoningEffort) }
            $r = Invoke-Timed -File $copilot -Arguments ($prefix + $agentArgs) -WorkingDirectory $ws -Name 'agent' -TimeoutSeconds ($run.timeoutMinutes * 60)
        }
        'solve' {
            $pw = "C:\Program Files\PowerShell\7\pwsh.exe"
            $r = Invoke-Timed -File $pw -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$in\solve.ps1", '-Workspace', $ws) -WorkingDirectory $ws -Name 'agent' -TimeoutSeconds ($run.timeoutMinutes * 60)
        }
        'noop' { $r = [pscustomobject]@{ ExitCode = 0; TimedOut = $false; DurationMs = 0 } }
        default { throw "Unknown mode '$($run.mode)'" }
    }
    $result.exitCode = $r.ExitCode
    $result.timedOut = $r.TimedOut
    $result.durationMs = $r.DurationMs

    $events = Get-ChildItem -Path "$copilotHome\session-state" -Filter events.jsonl -Recurse -File -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if ($events) { Copy-Item $events.FullName "$out\events.jsonl" }
    $result.phase = 'done'
}
catch {
    $result.error = "$($_.Exception.Message) @ $($_.InvocationInfo.PositionMessage)"
    Write-Log "ERROR $($result.error)"
}
finally { Save-Result }
