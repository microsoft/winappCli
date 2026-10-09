Set-StrictMode -Version Latest

# Pure logic of the UI benchmark: scenario and method definitions, scoring of recorded app state,
# resume bookkeeping, and reports. Nothing here touches the desktop; lib\AppState.psm1 does.

Import-Module (Join-Path $PSScriptRoot '..\..\agents\lib\Benchmark.psm1')

# State keys the harness can read for each app (see Get-AppSnapshot in AppState.psm1).
$script:AppKeys = @{
    calculator = @('display', 'mode')
    gallery    = @('colors', 'toggleWork', 'dialogOpen', 'dialogResult', 'page')
}
$script:CalculatorModes = @('standard', 'scientific')
$script:ScenarioKeys = @('id', 'app', 'description', 'prompt', 'timeoutMinutes', 'allowedLaunches', 'setup', 'checks')
$script:InstanceKeys = @('role', 'target', 'mode', 'enter', 'page', 'invoke', 'waitFor', 'expect', 'minimize')
$script:MethodKeys = @('$schema', 'name', 'displayName', 'description', 'plugins', 'skills', 'mcpServers', 'allowTools', 'denyTools', 'path', 'env', 'hideCommands', 'requires')
# Records with these statuses are final; anything else (harness errors) runs again on resume.
$script:FinalStatuses = @('pass', 'fail')

# `winapp ui` subcommands; a prompt that names one tells the agent which tool to use.
$script:UiSubcommands = @('inspect', 'search', 'list-windows', 'get-focused', 'status', 'invoke', 'set-value', 'send-keys', 'click',
    'focus', 'scroll', 'scroll-into-view', 'get-value', 'get-property', 'wait-for', 'screenshot', 'record', 'hover', 'drag', 'touch', 'pen', 'yield')

function Get-Field {
    # Property of a record or hashtable, or $null when it does not have one.
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) { return $Object.Contains($Name) ? $Object[$Name] : $null }
    $p = $Object.PSObject.Properties[$Name]
    return $p ? $p.Value : $null
}

function Test-Integer {
    param($Value, [int]$Min = 0)
    return (($Value -is [int] -or $Value -is [long]) -and $Value -ge $Min)
}

#region Scenarios

function Test-PromptLeak {
    # Tool names and internal identifiers that a natural user request would not contain.
    param([AllowEmptyString()][string]$Prompt, [string[]]$InternalIds = @())
    $found = [System.Collections.Generic.List[string]]::new()
    if ($Prompt -match '(?i)\bwinapp\b') { $found.Add('names the winapp CLI') }
    $sub = ($script:UiSubcommands | ForEach-Object { [regex]::Escape($_) }) -join '|'
    if ($Prompt -match "(?i)\bui\s+($sub)\b") { $found.Add("names the command 'ui $($Matches[1])'") }
    if ($Prompt -match '(?i)\b(automation\s*id|uia|ui automation)\b') { $found.Add("mentions '$($Matches[1])'") }
    foreach ($id in $InternalIds | Where-Object { $_ }) {
        if ($Prompt -cmatch "\b$([regex]::Escape($id))\b") { $found.Add("contains the AutomationId '$id'") }
    }
    return @($found)
}

function Test-UiScenarioDefinition {
    # Schema errors of one scenario.json (as a hashtable); empty when valid.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Definition, [string]$FolderName)
    $e = [System.Collections.Generic.List[string]]::new()
    $d = $Definition
    foreach ($k in $d.Keys) { if ($k -notin $script:ScenarioKeys) { $e.Add("unknown field '$k'") } }
    foreach ($k in 'id', 'app', 'description', 'prompt', 'checks') {
        if (-not $d.Contains($k) -or $null -eq $d[$k] -or ($d[$k] -is [string] -and -not $d[$k].Trim())) { $e.Add("missing '$k'") }
    }
    if ($e.Count) { return @($e) }
    if ($FolderName -and $d.id -ne $FolderName) { $e.Add("id '$($d.id)' must match its folder name '$FolderName'") }
    if ($d.id -notmatch '^[a-z0-9][a-z0-9-]*$') { $e.Add("id '$($d.id)' must be lowercase letters, digits, and dashes") }
    if (-not $script:AppKeys.ContainsKey([string]$d.app)) { $e.Add("app '$($d.app)' must be one of: $(@($script:AppKeys.Keys | Sort-Object) -join ', ')"); return @($e) }
    $keys = $script:AppKeys[[string]$d.app]
    if ($d.Contains('timeoutMinutes') -and -not (Test-Integer $d.timeoutMinutes 1)) { $e.Add('timeoutMinutes must be a positive integer') }
    if ($d.Contains('allowedLaunches') -and -not (Test-Integer $d.allowedLaunches 0)) { $e.Add('allowedLaunches must be a non-negative integer') }

    $ids = [System.Collections.Generic.List[string]]::new()
    $roles = [System.Collections.Generic.List[string]]::new()
    $instances = @()
    if ($d.Contains('setup') -and $null -ne $d.setup) {
        if ($d.setup -isnot [System.Collections.IDictionary]) { $e.Add('setup must be an object') }
        else {
            foreach ($k in $d.setup.Keys) { if ($k -ne 'instances') { $e.Add("unknown setup field '$k'") } }
            if ($d.setup.Contains('instances') -and $null -ne $d.setup.instances) { $instances = @($d.setup.instances) }
        }
    }
    foreach ($i in $instances) {
        if ($i -isnot [System.Collections.IDictionary]) { $e.Add('setup.instances entries must be objects'); continue }
        foreach ($k in $i.Keys) { if ($k -notin $script:InstanceKeys) { $e.Add("unknown instance field '$k'") } }
        $role = [string](Get-Field $i 'role')
        if (-not $role -or $role -notmatch '^[a-z][a-zA-Z0-9]*$') { $e.Add("instance role '$role' must be a lowercase identifier") }
        elseif ($role -in 'only', 'any') { $e.Add("instance role '$role' is reserved") }
        elseif ($role -in $roles) { $e.Add("duplicate instance role '$role'") }
        else { $roles.Add($role) }
        foreach ($k in 'target', 'minimize') { if ($i.Contains($k) -and $i[$k] -isnot [bool]) { $e.Add("instance '$role' $k must be true or false") } }
        if ($d.app -eq 'calculator') {
            foreach ($k in 'page', 'invoke', 'waitFor') { if ($i.Contains($k)) { $e.Add("instance '$role': '$k' applies to gallery scenarios only") } }
            if ($i.Contains('mode') -and $i.mode -notin $script:CalculatorModes) { $e.Add("instance '$role' mode must be one of: $($script:CalculatorModes -join ', ')") }
            if ($i.Contains('enter') -and [string]$i.enter -notmatch '^[0-9]+$') { $e.Add("instance '$role' enter must be digits") }
        }
        else {
            foreach ($k in 'mode', 'enter') { if ($i.Contains($k)) { $e.Add("instance '$role': '$k' applies to calculator scenarios only") } }
            if ($i.Contains('page') -and [string]$i.page -notmatch '^[A-Za-z0-9]+$') { $e.Add("instance '$role' page must be a Gallery item id such as ComboBox") }
            foreach ($id in @(Get-Field $i 'invoke') + @(Get-Field $i 'waitFor') | Where-Object { $_ -and -not ([string]$_).StartsWith('name:') }) { $ids.Add([string]$id) }
        }
        $expect = Get-Field $i 'expect'
        if ($null -ne $expect) {
            if ($expect -isnot [System.Collections.IDictionary]) { $e.Add("instance '$role' expect must be an object of state values") }
            else { foreach ($k in $expect.Keys) { if ($k -notin $keys) { $e.Add("instance '$role' expect key '$k' must be one of: $($keys -join ', ')") } } }
        }
    }
    if ($instances.Count -and @($instances | Where-Object { $_ -is [System.Collections.IDictionary] -and (Get-Field $_ 'target') -eq $true }).Count -ne 1) {
        $e.Add('exactly one setup instance must be the target')
    }

    $checks = @($d.checks)
    if (-not $checks.Count) { $e.Add('checks must not be empty') }
    foreach ($c in $checks) {
        if ($c -isnot [System.Collections.IDictionary]) { $e.Add('checks entries must be objects'); continue }
        switch ([string](Get-Field $c 'type')) {
            'instanceCount' {
                if (-not (Test-Integer (Get-Field $c 'equals') 0)) { $e.Add('instanceCount check needs a non-negative integer equals') }
            }
            'value' {
                $inst = [string](Get-Field $c 'instance')
                if (-not $inst) { $e.Add('value check needs an instance (a setup role, only, or any)') }
                elseif ($inst -notin 'only', 'any' -and $inst -notin $roles) { $e.Add("value check instance '$inst' is not a setup role") }
                $key = [string](Get-Field $c 'key')
                if ($key -notin $keys) { $e.Add("value check key '$key' must be one of: $($keys -join ', ')") }
                $hasEq = $c.Contains('equals'); $hasIn = $c.Contains('in')
                if ($hasEq -eq $hasIn) { $e.Add("value check on '$key' needs exactly one of equals or in") }
                elseif ($hasIn -and @($c.in).Count -eq 0) { $e.Add("value check on '$key' has an empty in list") }
            }
            'alive' {
                if ([string](Get-Field $c 'instance') -notin $roles) { $e.Add("alive check instance '$(Get-Field $c 'instance')' is not a setup role") }
            }
            default { $e.Add("unknown check type '$(Get-Field $c 'type')' (use instanceCount, value, or alive)") }
        }
    }
    foreach ($leak in Test-PromptLeak -Prompt $d.prompt -InternalIds @($ids)) { $e.Add("prompt $leak") }
    return @($e)
}

function Get-ScenarioHash {
    # Changes when anything that affects a run's outcome changes: prompt, setup, checks, limits.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Definition)
    $copy = [ordered]@{}
    foreach ($k in $Definition.Keys | Where-Object { $_ -ne 'description' } | Sort-Object) { $copy[$k] = $Definition[$k] }
    return Get-ShortHash ($copy | ConvertTo-Json -Depth 10 -Compress)
}

function ConvertTo-UiScenario {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Definition, [string]$Path)
    $d = $Definition
    $setup = Get-Field $d 'setup'
    $instances = @(if ($setup -and $setup.Contains('instances') -and $setup.instances) { $setup.instances })
    [pscustomobject]@{
        Id              = [string]$d.id
        App             = [string]$d.app
        Description     = [string]$d.description
        Prompt          = [string]$d.prompt
        TimeoutMinutes  = if ($d.Contains('timeoutMinutes')) { [int]$d.timeoutMinutes } else { $null }
        AllowedLaunches = if ($d.Contains('allowedLaunches')) { [int]$d.allowedLaunches } else { 0 }
        Instances       = [object[]]$instances
        Checks          = @($d.checks)
        Hash            = Get-ScenarioHash $d
        Path            = $Path
    }
}

function Get-UiScenarios {
    # Loads every scenarios\<id>\scenario.json; throws listing every invalid one.
    param([Parameter(Mandatory)][string]$ScenariosRoot)
    $errors = [System.Collections.Generic.List[string]]::new()
    $list = foreach ($file in Get-ChildItem -LiteralPath $ScenariosRoot -Filter scenario.json -Recurse -File | Sort-Object FullName) {
        try { $d = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json -AsHashtable }
        catch { $errors.Add("$($file.Directory.Name): invalid JSON: $($_.Exception.Message)"); continue }
        $bad = @(Test-UiScenarioDefinition -Definition $d -FolderName $file.Directory.Name)
        if ($bad) { foreach ($b in $bad) { $errors.Add("$($file.Directory.Name): $b") }; continue }
        ConvertTo-UiScenario -Definition $d -Path $file.FullName
    }
    if ($errors.Count) { throw "Invalid scenarios:`n  $($errors -join "`n  ")" }
    return @($list)
}

#endregion

#region Methods

function Resolve-MethodPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$BaseDirectory)
    $p = [Environment]::ExpandEnvironmentVariables($Path)
    if (-not [System.IO.Path]::IsPathRooted($p)) { $p = Join-Path $BaseDirectory $p }
    return [System.IO.Path]::GetFullPath($p)
}

function Test-UiMethodDefinition {
    # Schema errors of one method.json (as a hashtable); empty when valid. -BaseDirectory resolves
    # relative plugin and skill paths and checks that they exist.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Definition, [string]$BaseDirectory)
    $e = [System.Collections.Generic.List[string]]::new()
    $d = $Definition
    foreach ($k in $d.Keys) { if ($k -notin $script:MethodKeys) { $e.Add("unknown field '$k'") } }
    if ([string](Get-Field $d 'name') -notmatch '^[a-z0-9][a-z0-9-]*$') { $e.Add('name must be lowercase letters, digits, and dashes') }
    foreach ($k in 'displayName', 'description') { if ($d.Contains($k) -and $d[$k] -isnot [string]) { $e.Add("$k must be a string") } }
    foreach ($k in 'plugins', 'skills', 'allowTools', 'denyTools', 'path', 'hideCommands', 'requires') {
        if (-not $d.Contains($k) -or $null -eq $d[$k]) { continue }
        $v = $d[$k]
        if ($v -is [string] -or $v -is [System.Collections.IDictionary] -or @($v | Where-Object { $_ -isnot [string] -or -not $_ }).Count) { $e.Add("$k must be a list of non-empty strings") }
    }
    foreach ($k in 'mcpServers', 'env') {
        if ($d.Contains($k) -and $null -ne $d[$k] -and $d[$k] -isnot [System.Collections.IDictionary]) { $e.Add("$k must be an object") }
    }
    if ((Get-Field $d 'env') -is [System.Collections.IDictionary]) {
        foreach ($n in $d.env.Keys) {
            if ($d.env[$n] -isnot [string]) { $e.Add("env.$n must be a string") }
            if ($n -match '^(?i)(PATH|COPILOT_HOME)$') { $e.Add("env.$n is managed by the harness; use the path field to add PATH entries") }
        }
    }
    if ((Get-Field $d 'mcpServers') -is [System.Collections.IDictionary]) {
        foreach ($n in $d.mcpServers.Keys) {
            $s = $d.mcpServers[$n]
            if ($s -isnot [System.Collections.IDictionary] -or -not ($s.Contains('command') -or $s.Contains('url'))) { $e.Add("mcpServers.$n needs a command or a url") }
        }
    }
    $hide = @(Get-Field $d 'hideCommands' | Where-Object { $_ -is [string] })
    foreach ($c in $hide) {
        if ($c -notmatch '^[A-Za-z0-9._-]+$') { $e.Add("hideCommands entry '$c' must be a bare command name") }
        if ($c -in @(Get-Field $d 'requires')) { $e.Add("'$c' is both required and hidden") }
    }
    if ($BaseDirectory -and -not $e.Count) {
        foreach ($p in @(Get-Field $d 'plugins') | Where-Object { $_ }) {
            $full = Resolve-MethodPath $p $BaseDirectory
            if (-not (Test-Path -LiteralPath (Join-Path $full 'skills')) -and -not (Test-Path -LiteralPath (Join-Path $full 'plugin.json'))) { $e.Add("plugin '$p' ($full) has no plugin.json or skills folder") }
        }
        foreach ($p in @(Get-Field $d 'skills') | Where-Object { $_ }) {
            $full = Resolve-MethodPath $p $BaseDirectory
            if (-not (Test-Path -LiteralPath (Join-Path $full 'SKILL.md'))) { $e.Add("skill '$p' ($full) has no SKILL.md") }
        }
    }
    return @($e)
}

function Get-SkillFolderName {
    # The name: in a skill folder's SKILL.md frontmatter, falling back to the folder name.
    param([Parameter(Mandatory)][string]$SkillPath)
    $md = Join-Path $SkillPath 'SKILL.md'
    $inFrontmatter = $false
    foreach ($line in [System.IO.File]::ReadLines($md)) {
        if ($line.Trim() -eq '---') { if ($inFrontmatter) { break } else { $inFrontmatter = $true; continue } }
        if ($inFrontmatter -and $line -match '^name:\s*["'']?([^"'']+?)["'']?\s*$') { return $Matches[1] }
    }
    return Split-Path $SkillPath -Leaf
}

function Get-UiMethod {
    # A checked-in method by name (methods\<name>.json) or a local method file by path.
    param([Parameter(Mandatory)][string]$NameOrPath, [Parameter(Mandatory)][string]$MethodsRoot)
    $checkedIn = $NameOrPath -match '^[a-z0-9][a-z0-9-]*$' -and -not (Test-Path -LiteralPath $NameOrPath -PathType Leaf)
    $file = if ($checkedIn) { Join-Path $MethodsRoot "$NameOrPath.json" } else { $NameOrPath }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        $known = @(Get-ChildItem -LiteralPath $MethodsRoot -Filter *.json -File | ForEach-Object BaseName)
        throw "Unknown method '$NameOrPath'. Use one of: $($known -join ', '), or a path to a method.json file."
    }
    $file = (Resolve-Path -LiteralPath $file).Path
    $raw = Get-Content -Raw -LiteralPath $file
    try { $d = $raw | ConvertFrom-Json -AsHashtable } catch { throw "Method file '$file' is not valid JSON: $($_.Exception.Message)" }
    $dir = Split-Path $file
    $bad = @(Test-UiMethodDefinition -Definition $d -BaseDirectory $dir)
    if ($bad) { throw "Invalid method file '$file':`n  $($bad -join "`n  ")" }
    $list = { param($k) @(Get-Field $d $k | Where-Object { $_ }) }
    $plugins = @(& $list 'plugins' | ForEach-Object { Resolve-MethodPath $_ $dir })
    $skills = @(& $list 'skills' | ForEach-Object { Resolve-MethodPath $_ $dir })
    $expected = @(
        foreach ($p in $plugins) { Get-PluginSkillNames -PluginPath $p }
        foreach ($s in $skills) { Get-SkillFolderName $s }
    )
    [pscustomobject]@{
        Name           = [string]$d.name
        DisplayName    = if (Get-Field $d 'displayName') { [string]$d.displayName } else { [string]$d.name }
        Description    = [string](Get-Field $d 'description')
        Plugins        = $plugins
        Skills         = $skills
        ExpectedSkills = $expected
        McpServers     = if (Get-Field $d 'mcpServers') { $d.mcpServers } else { [ordered]@{} }
        AllowTools     = @(& $list 'allowTools')
        DenyTools      = @(& $list 'denyTools')
        Path           = @(& $list 'path' | ForEach-Object { Resolve-MethodPath $_ $dir })
        Env            = if (Get-Field $d 'env') { $d.env } else { [ordered]@{} }
        HideCommands   = @(& $list 'hideCommands')
        Requires       = @(& $list 'requires')
        CheckedIn      = [bool]$checkedIn
        SourcePath     = $file
        # Resume reruns cells when the method's definition or the skills it installs change.
        Hash           = Get-ShortHash (($raw -replace '\s', '') + "`n" + (@($expected | Sort-Object) -join ','))
    }
}

function Expand-MethodEnv {
    # The method's env with ${env:NAME} replaced from -Source (default: the current environment),
    # so secrets stay out of method files. Missing variables are reported, not left blank silently.
    param([System.Collections.IDictionary]$Env = @{}, [System.Collections.IDictionary]$Source)
    $missing = [System.Collections.Generic.List[string]]::new()
    $out = [ordered]@{}
    foreach ($k in $Env.Keys) {
        $text = [string]$Env[$k]
        foreach ($m in [regex]::Matches($text, '\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}')) {
            $n = $m.Groups[1].Value
            $v = if ($Source) { [string](Get-Field $Source $n) } else { [Environment]::GetEnvironmentVariable($n) }
            if (-not $v) { $missing.Add($n) }
            $text = $text.Replace($m.Value, [string]$v)
        }
        $out[$k] = $text
    }
    [pscustomobject]@{ Values = $out; Missing = @($missing | Select-Object -Unique) }
}

function Resolve-CommandOnPath {
    # The file a shell runs for a bare command name: PATH directories in order, PATHEXT within each.
    param([Parameter(Mandatory)][string]$Name, [AllowEmptyString()][string]$PathValue, [string]$PathExt = '.COM;.EXE;.BAT;.CMD')
    $exts = @($PathExt -split ';' | Where-Object { $_ })
    foreach ($dir in @($PathValue -split ';' | Where-Object { $_ })) {
        $candidates = if ([System.IO.Path]::HasExtension($Name)) { @($Name) } else { @($exts | ForEach-Object { "$Name$_" }) }
        foreach ($c in $candidates) {
            $p = Join-Path $dir $c
            if (Test-Path -LiteralPath $p -PathType Leaf) { return $p }
        }
    }
    return $null
}

function New-HiddenCommandShim {
    # Writes <name>.cmd files that fail like a missing command. With their folder first on PATH they
    # hide a command whose real folder must stay on PATH (WindowsApps also holds pwsh and winget).
    param([Parameter(Mandatory)][string]$Directory, [AllowEmptyCollection()][string[]]$Names = @())
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    foreach ($n in $Names) {
        $text = "@echo '$n' is not recognized as an internal or external command, operable program or batch file. 1>&2`r`n@exit /b 9009`r`n"
        Set-Content -LiteralPath (Join-Path $Directory "$n.cmd") -Value $text -NoNewline -Encoding ascii
    }
}

function Set-EnvironmentPath {
    # Prepends directories to the PATH entry of an environment map, whatever its key casing.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Environment, [AllowEmptyCollection()][string[]]$Prepend = @())
    $key = @($Environment.Keys | Where-Object { $_ -ieq 'PATH' } | Select-Object -First 1)
    $key = if ($key) { $key[0] } else { 'Path' }
    $current = [string](Get-Field $Environment $key)
    $Environment[$key] = (@($Prepend | Where-Object { $_ }) + @($current -split ';' | Where-Object { $_ })) -join ';'
    return $Environment[$key]
}

function Test-MethodSkillList {
    # Checks `copilot skill list --json` against the skills a method installs: every non-builtin skill
    # must come from the isolated home and be expected, and every expected skill must be listed.
    param(
        [AllowEmptyCollection()][string[]]$Expected = @(),
        [AllowEmptyCollection()][object[]]$Listed = @(),
        [Parameter(Mandatory)][string]$CopilotHome
    )
    $unexpected = [System.Collections.Generic.List[string]]::new()
    $have = [System.Collections.Generic.List[string]]::new()
    foreach ($s in @($Listed | Where-Object { (Get-Field $_ 'source') -ne 'builtin' })) {
        $bare = Get-BareSkillName $s.name
        $path = [string](Get-Field $s 'path')
        if ($bare -notin $Expected -or -not $path.StartsWith($CopilotHome, [StringComparison]::OrdinalIgnoreCase)) { $unexpected.Add("$($s.name) ($(Get-Field $s 'source'))") }
        elseif ($null -eq (Get-Field $s 'enabled') -or $s.enabled) { $have.Add($bare) }
    }
    [pscustomobject]@{
        Unexpected = @($unexpected)
        Missing    = @($Expected | Where-Object { $_ -notin $have } | Select-Object -Unique)
    }
}

#endregion

#region Scoring

function Format-StateValue {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [bool]) { return $Value.ToString().ToLowerInvariant() }
    return ([string]$Value).Trim().ToLowerInvariant()
}

function Test-SameProcess {
    # Same pid, and the same start time when both snapshots recorded one (guards against pid reuse).
    param($A, $B)
    if ([int]$A.pid -ne [int]$B.pid) { return $false }
    $sa = [string](Get-Field $A 'startTime')
    $sb = [string](Get-Field $B 'startTime')
    return (-not $sa -or -not $sb -or $sa -eq $sb)
}

function Test-ScenarioState {
    # Scores a run from two harness snapshots of the scenario's app: -Before right after setup and
    # -After once the agent finished. The agent's own answer is never consulted. Returns the checks
    # that failed and any collateral damage; either one fails the run.
    param([Parameter(Mandatory)]$Scenario, [Parameter(Mandatory)]$Before, [Parameter(Mandatory)]$After)
    $failures = [System.Collections.Generic.List[string]]::new()
    $collateral = [System.Collections.Generic.List[object]]::new()
    $notes = [System.Collections.Generic.List[string]]::new()

    $beforeInst = @(Get-Field $Before 'instances' | Where-Object { $_ })
    $afterInst = @(Get-Field $After 'instances' | Where-Object { $_ })
    # Join each instance alive after the run to its setup identity; new processes came from the run.
    $joined = @(foreach ($a in $afterInst | Where-Object { $null -eq (Get-Field $_ 'alive') -or $_.alive }) {
            $b = @($beforeInst | Where-Object { Test-SameProcess $_ $a } | Select-Object -First 1)
            $b = if ($b) { $b[0] } else { $null }
            [pscustomobject]@{
                Pid        = [int]$a.pid
                Role       = [string](Get-Field $b 'role')
                LaunchedBy = if ($b) { [string](Get-Field $b 'launchedBy') } else { 'run' }
                Target     = [bool](Get-Field $b 'target')
                Before     = $b
                After      = $a
            }
        })
    $owned = @($joined | Where-Object LaunchedBy -ne 'foreign')

    foreach ($a in $afterInst | Where-Object { Get-Field $_ 'restored' }) { $notes.Add("pid $($a.pid) was minimized; the harness restored it to read its state") }
    foreach ($a in $afterInst | Where-Object { Get-Field $_ 'error' }) { $notes.Add("pid $($a.pid): $($a.error)") }

    foreach ($c in $Scenario.Checks) {
        switch ([string](Get-Field $c 'type')) {
            'instanceCount' {
                $want = [int](Get-Field $c 'equals')
                if ($owned.Count -ne $want) { $failures.Add("expected $want $($Scenario.App) instance(s), found $($owned.Count)") }
            }
            'alive' {
                $role = [string](Get-Field $c 'instance')
                if (-not @($owned | Where-Object Role -eq $role)) { $failures.Add("the '$role' instance is no longer running") }
            }
            'value' {
                $key = [string](Get-Field $c 'key')
                $inst = [string](Get-Field $c 'instance')
                $want = @(if ($null -ne (Get-Field $c 'in')) { $c.in | ForEach-Object { Format-StateValue $_ } } else { Format-StateValue (Get-Field $c 'equals') })
                $wantText = if ($want.Count -gt 1) { "one of [$($want -join ', ')]" } else { "'$($want[0])'" }
                $candidates = @()
                if ($inst -eq 'only') {
                    if ($owned.Count -ne 1) { $failures.Add("$key`: expected exactly one $($Scenario.App) instance, found $($owned.Count)") }
                    else { $candidates = $owned }
                }
                elseif ($inst -eq 'any') { $candidates = $owned }
                else {
                    $candidates = @($owned | Where-Object Role -eq $inst)
                    if (-not $candidates) { $failures.Add("$key`: the '$inst' instance is no longer running") }
                }
                if (-not $candidates.Count) { continue }
                $seen = @($candidates | ForEach-Object { Format-StateValue (Get-Field (Get-Field $_.After 'values') $key) })
                if (-not @($seen | Where-Object { $null -ne $_ -and $_ -in $want })) {
                    $who = if ($inst -in 'only', 'any') { $Scenario.App } else { "the '$inst' instance" }
                    $got = @($seen | ForEach-Object { if ($null -eq $_) { '(unreadable)' } else { "'$_'" } }) -join ', '
                    $failures.Add("$key of $who is $got; expected $wantText")
                }
            }
        }
    }

    # Collateral damage. Instances launched by the run beyond what the scenario allows:
    $launched = @($joined | Where-Object LaunchedBy -eq 'run')
    if ($launched.Count -gt $Scenario.AllowedLaunches) {
        $collateral.Add([pscustomobject]@{ type = 'extra-instance'; detail = "launched $($launched.Count) new $($Scenario.App) instance(s) (pid $(@($launched.Pid) -join ', ')); allowed $($Scenario.AllowedLaunches)" })
    }
    # Instances that were running before the run and were closed by it:
    foreach ($b in $beforeInst | Where-Object { $null -eq (Get-Field $_ 'alive') -or $_.alive }) {
        if (-not @($joined | Where-Object { $_.Before -and (Test-SameProcess $_.Before $b) })) {
            $who = if ((Get-Field $b 'launchedBy') -eq 'foreign') { "a $($Scenario.App) window the benchmark does not own" } else { "the '$(Get-Field $b 'role')' instance" }
            $collateral.Add([pscustomobject]@{ type = 'app-closed'; detail = "closed $who (pid $($b.pid))" })
        }
    }
    # Windows the run was not asked to change:
    foreach ($j in $joined | Where-Object { $_.Before -and -not $_.Target }) {
        foreach ($k in $script:AppKeys[$Scenario.App]) {
            $vb = Format-StateValue (Get-Field (Get-Field $j.Before 'values') $k)
            $va = Format-StateValue (Get-Field (Get-Field $j.After 'values') $k)
            if ($null -eq $vb -or $null -eq $va -or $vb -eq $va) { continue }
            $who = if ($j.LaunchedBy -eq 'foreign') { 'a window the benchmark does not own' } else { "the '$($j.Role)' instance" }
            $collateral.Add([pscustomobject]@{ type = 'wrong-window-modified'; detail = "changed $k of $who (pid $($j.Pid)) from '$vb' to '$va'" })
        }
    }
    # Related apps (another WinUI Gallery install, for example) started during the run:
    $relBefore = @(Get-Field $Before 'related' | Where-Object { $_ })
    foreach ($r in @(Get-Field $After 'related' | Where-Object { $_ })) {
        if (-not @($relBefore | Where-Object { Test-SameProcess $_ $r })) {
            $from = if (Get-Field $r 'path') { " from $($r.path)" } else { '' }
            $collateral.Add([pscustomobject]@{ type = 'wrong-app-launched'; detail = "started $(Get-Field $r 'name') (pid $($r.pid))$from" })
        }
    }

    [pscustomobject]@{
        Status     = if ($failures.Count -or $collateral.Count) { 'fail' } else { 'pass' }
        Failures   = @($failures)
        Collateral = @($collateral)
        Notes      = @($notes)
    }
}

#endregion

#region Resume and plan

function Get-CellKey {
    param([string]$Method, [string]$Model, [string]$Scenario, [int]$Iteration)
    return "$Method|$Model|$Scenario|$Iteration"
}

function Read-RunRecords {
    # Records of a runs.jsonl; an unreadable line (an interrupted write) is skipped.
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $out = foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if (-not $line.Trim()) { continue }
        try { $line | ConvertFrom-Json -Depth 20 } catch { Write-Warning "Skipping an unreadable line in $Path" }
    }
    return @($out)
}

function Get-LatestRecords {
    # The last record of each cell (method|model|scenario|iteration).
    param([AllowEmptyCollection()][object[]]$Records = @())
    $latest = @{}
    foreach ($r in $Records) { $latest[(Get-CellKey $r.method $r.model $r.scenario ([int]$r.iteration))] = $r }
    return $latest
}

function Test-CellDone {
    # A cell is done when its latest record is scored (pass or fail) and was produced by the same
    # scenario and method definitions. Harness errors and edited definitions run again.
    param($Record, [string]$ScenarioHash, [string]$MethodHash)
    if (-not $Record) { return $false }
    if ([string](Get-Field $Record 'status') -notin $script:FinalStatuses) { return $false }
    if ($ScenarioHash -and [string](Get-Field $Record 'scenarioHash') -ne $ScenarioHash) { return $false }
    if ($MethodHash -and [string](Get-Field $Record 'methodHash') -ne $MethodHash) { return $false }
    return $true
}

function Get-RunPlan {
    # Splits the requested cells into done and pending, with time and credit estimates for the pending
    # ones. Measured runs already in the results replace the default per-run estimates.
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Runs,
        [AllowEmptyCollection()][object[]]$Records = @(),
        [double]$CreditEstimate = 15,
        [double]$MinutesEstimate = 2
    )
    $Records = @($Records | Where-Object { $null -ne $_ })
    $latest = Get-LatestRecords $Records
    $pending = [System.Collections.Generic.List[object]]::new()
    $done = 0
    foreach ($r in $Runs) {
        $rec = $latest[(Get-CellKey $r.Method.Name $r.Model $r.Scenario.Id $r.Iteration)]
        if (Test-CellDone $rec $r.Scenario.Hash $r.Method.Hash) { $done++ } else { $pending.Add($r) }
    }
    $spend = Get-CreditSpend -Records $Records -DefaultEstimate $CreditEstimate
    $timed = @($Records | Where-Object { $null -ne (Get-Field $_ 'totalMs') })
    $perRun = if ($timed) { (($timed | ForEach-Object { [double]$_.totalMs } | Measure-Object -Average).Average) / 60000 } else { $MinutesEstimate }
    [pscustomobject]@{
        Total         = @($Runs).Count
        Done          = $done
        Pending       = @($pending)
        CreditsPerRun = $spend.Estimate
        Credits       = $pending.Count * $spend.Estimate
        MinutesPerRun = $perRun
        Minutes       = $pending.Count * $perRun
    }
}

#endregion

#region Reports

function Get-EnvironmentInfo {
    # Machine, OS, and tool versions recorded with every result folder.
    param([string]$RepoRoot, [string]$WinappCommand = 'winapp')
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
    $ubr = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue).UBR
    $cpu = @(Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1)
    $winapp = Get-Command $WinappCommand -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $winappVersion = $null
    if ($winapp) {
        $text = (& $winapp.Source --version 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -eq 0) { $winappVersion = $text }
    }
    $commit = $null; $dirty = $null
    if ($RepoRoot) {
        $commit = (& git -C $RepoRoot rev-parse HEAD 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { $commit = $null }
        else { $dirty = [bool](& git -C $RepoRoot status --porcelain --untracked-files=no 2>$null | Out-String).Trim() }
    }
    [ordered]@{
        os            = if ($os) { "$($os.Caption) $($os.Version)$(if ($ubr) { ".$ubr" })" } else { [Environment]::OSVersion.VersionString }
        architecture  = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        cpu           = if ($cpu) { $cpu[0].Name.Trim() } else { $null }
        cpuCount      = [Environment]::ProcessorCount
        powershell    = $PSVersionTable.PSVersion.ToString()
        winappVersion = $winappVersion
        winappPath    = if ($winapp) { $winapp.Source } else { $null }
        repoCommit    = $commit
        repoDirty     = $dirty
    }
}

function Format-Ms {
    param($Value)
    if ($null -eq $Value) { return 'n/a' }
    return ('{0:N1}s' -f ([double]$Value / 1000))
}

function Format-Credits {
    param($Value)
    if ($null -eq $Value) { return 'n/a' }
    return ('{0:N1}' -f [double]$Value)
}

function Get-CellStats {
    # Pass rate, medians, and totals of one group of run records.
    param([AllowEmptyCollection()][object[]]$Runs = @())
    $launched = @($Runs | Where-Object { $null -ne (Get-Field $_ 'durationMs') })
    $median = { param($name) Get-Median @($launched | ForEach-Object { Get-Field $_ $name } | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ }) }
    $tokens = @($launched | Where-Object { Get-Field $_ 'tokens' } | ForEach-Object { [double]$_.tokens.input + [double]$_.tokens.output })
    $credits = @($launched | ForEach-Object { Get-Field $_ 'aiCredits' } | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
    [pscustomobject]@{
        Status       = Get-StatusStats $Runs
        PassText     = Format-PassRate @($Runs | ForEach-Object { Get-Field $_ 'status' })
        WallMs       = & $median 'durationMs'
        Turns        = & $median 'modelTurns'
        ToolCalls    = & $median 'toolCalls'
        ToolMs       = & $median 'toolTimeMs'
        ApiMs        = & $median 'apiDurationMs'
        Tokens       = Get-Median $tokens
        Credits      = Get-Median $credits
        CreditsTotal = if ($credits) { ($credits | Measure-Object -Sum).Sum } else { $null }
        Collateral   = @($Runs | ForEach-Object { Get-Field $_ 'collateral' } | Where-Object { $_ }).Count
    }
}

function Get-ScenarioRank {
    param([string[]]$Order, [string]$Id)
    $i = [array]::IndexOf(@($Order), $Id)
    if ($i -lt 0) { return 1000 }
    return $i
}

function Write-UiSummary {
    # summary.md: pass rate and cost per method and scenario, then a note for every run that did not pass.
    param(
        [Parameter(Mandatory)][string]$RunsPath,
        [Parameter(Mandatory)][string]$SummaryPath,
        [System.Collections.IDictionary]$Header = [ordered]@{},
        [string[]]$ScenarioOrder = @()
    )
    $runs = @((Get-LatestRecords (Read-RunRecords $RunsPath)).Values | Sort-Object method, model, { Get-ScenarioRank $ScenarioOrder $_.scenario }, iteration)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# UI automation agent benchmark')
    [void]$sb.AppendLine()
    foreach ($k in $Header.Keys) { [void]$sb.AppendLine("- **$k**: $($Header[$k])") }
    [void]$sb.AppendLine()
    if (-not $runs) {
        [void]$sb.AppendLine('No runs recorded.')
        Set-Content -LiteralPath $SummaryPath -Value $sb.ToString() -Encoding utf8NoBOM
        return
    }
    $columns = '| Pass | Wall | Turns | Tool calls | Tool time | API time | Tokens (in+out) | Credits | Collateral |'
    $rule = '|---|---|---|---|---|---|---|---|---|'
    $cells = { param($s, $credits) "| $($s.PassText) | $(Format-Ms $s.WallMs) | $(Format-Count $s.Turns) | $(Format-Count $s.ToolCalls) | $(Format-Ms $s.ToolMs) | $(Format-Ms $s.ApiMs) | $(Format-Count $s.Tokens) | $(Format-Credits $credits) | $($s.Collateral) |" }

    [void]$sb.AppendLine('## By method')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Times, turns, tool calls, and tokens are medians per run; credits are the total. Tool time is wall time with at least one tool running; API time is time spent waiting on the model.')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("| Method | Model $columns")
    [void]$sb.AppendLine("|---|---$rule")
    foreach ($g in $runs | Group-Object method, model) {
        $s = Get-CellStats $g.Group
        [void]$sb.AppendLine("| $($g.Group[0].method) | $($g.Group[0].model) $(& $cells $s $s.CreditsTotal)")
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## By scenario')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Credits here are the median per run.')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("| Scenario | Method | Model $columns")
    [void]$sb.AppendLine("|---|---|---$rule")
    foreach ($g in $runs | Group-Object scenario, method, model | Sort-Object { Get-ScenarioRank $ScenarioOrder $_.Group[0].scenario }, { $_.Group[0].method }, { $_.Group[0].model }) {
        $s = Get-CellStats $g.Group
        $f = $g.Group[0]
        [void]$sb.AppendLine("| $($f.scenario) | $($f.method) | $($f.model) $(& $cells $s $s.Credits)")
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Runs that did not pass')
    [void]$sb.AppendLine()
    $notPassed = @($runs | Where-Object { $_.status -ne 'pass' })
    if (-not $notPassed) { [void]$sb.AppendLine('None.') }
    foreach ($r in $notPassed) {
        $parts = @(@(Get-Field $r 'failures') + @(Get-Field $r 'collateral' | Where-Object { $_ } | ForEach-Object { "[$($_.type)] $($_.detail)" }) | Where-Object { $_ })
        if (-not $parts -and (Get-Field $r 'reason')) { $parts = @($r.reason) }
        [void]$sb.AppendLine("- **$($r.scenario)** · $($r.method) · $($r.model) · #$($r.iteration) · $($r.status) ($(Format-Ms (Get-Field $r 'durationMs'))): $($parts -join '; ')")
    }
    $noted = @($runs | Where-Object { @(Get-Field $_ 'notes' | Where-Object { $_ }).Count })
    if ($noted) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('## Scoring notes')
        [void]$sb.AppendLine()
        foreach ($r in $noted) { [void]$sb.AppendLine("- $($r.scenario) · $($r.method) · #$($r.iteration): $(@($r.notes) -join '; ')") }
    }
    Set-Content -LiteralPath $SummaryPath -Value $sb.ToString() -Encoding utf8NoBOM
}

function Get-UiComparison {
    # Markdown comparing two or more result folders per scenario, method, and model.
    param([Parameter(Mandatory)][string[]]$ResultDirs, [string[]]$ScenarioOrder = @())
    $sets = @(foreach ($d in $ResultDirs) {
            $runs = @((Get-LatestRecords (Read-RunRecords (Join-Path $d 'runs.jsonl'))).Values)
            if (-not $runs) { throw "No runs.jsonl records in '$d'." }
            [pscustomobject]@{ Label = Split-Path $d -Leaf; Runs = $runs }
        })
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# UI benchmark comparison')
    [void]$sb.AppendLine()
    $labels = @($sets | ForEach-Object { '`' + $_.Label + '`' }) -join ' vs '
    [void]$sb.AppendLine("Results: $labels. Times, turns, tool calls, tokens, and credits are medians per run.")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| Scenario | Method | Model | Results | Pass | Wall | Turns | Tool calls | Tool time | Tokens | Credits | Collateral |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|---|---|---|---|---|---|')
    $groups = @($sets | ForEach-Object { $_.Runs } | ForEach-Object { [pscustomobject]@{ Scenario = $_.scenario; Method = $_.method; Model = $_.model } } |
        Sort-Object Scenario, Method, Model -Unique | Sort-Object { Get-ScenarioRank $ScenarioOrder $_.Scenario }, Scenario, Method, Model)
    $groups += @($sets | ForEach-Object { $_.Runs } | ForEach-Object { [pscustomobject]@{ Scenario = '*'; Method = $_.method; Model = $_.model } } | Sort-Object Method, Model -Unique)
    foreach ($g in $groups) {
        foreach ($set in $sets) {
            $group = @($set.Runs | Where-Object { ($g.Scenario -eq '*' -or $_.scenario -eq $g.Scenario) -and $_.method -eq $g.Method -and $_.model -eq $g.Model })
            if (-not $group) { continue }
            $s = Get-CellStats $group
            $label = if ($g.Scenario -eq '*') { '**all**' } else { $g.Scenario }
            [void]$sb.AppendLine("| $label | $($g.Method) | $($g.Model) | $($set.Label) | $($s.Status.Short) | $(Format-Ms $s.WallMs) | $(Format-Count $s.Turns) | $(Format-Count $s.ToolCalls) | $(Format-Ms $s.ToolMs) | $(Format-Count $s.Tokens) | $(Format-Credits $s.Credits) | $($s.Collateral) |")
        }
    }
    return $sb.ToString()
}

#endregion

Export-ModuleMember -Function Get-Field, Test-PromptLeak, Test-UiScenarioDefinition, Get-ScenarioHash, ConvertTo-UiScenario, Get-UiScenarios,
Resolve-MethodPath, Test-UiMethodDefinition, Get-SkillFolderName, Get-UiMethod, Expand-MethodEnv, Resolve-CommandOnPath, New-HiddenCommandShim,
Set-EnvironmentPath, Test-MethodSkillList, Format-StateValue, Test-SameProcess, Test-ScenarioState, Get-CellKey, Read-RunRecords, Get-LatestRecords,
Test-CellDone, Get-RunPlan, Get-EnvironmentInfo, Format-Ms, Format-Credits, Get-CellStats, Write-UiSummary, Get-UiComparison
