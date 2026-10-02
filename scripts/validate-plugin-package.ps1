#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate that plugins/winapp conforms to the Agent Plugins 1.0 specification
.DESCRIPTION
    Checks the portable manifest against the closed Agent Plugins 1.0 schema, verifies
    that portable components sit in their fixed locations, and guards the host-specific
    compatibility files that the spec does not cover.

    The Agent Plugins specification requires clients to validate without retrieving the
    schema, so this script encodes the schema rules rather than fetching them. That also
    keeps CI deterministic and offline-safe.

    It also checks the skills of every plugin under -PluginsRoot: SKILL.md frontmatter,
    description length, relative links staying inside the plugin, and `winapp` command
    examples matching docs/cli-schema.json. It then prints an approximate size report.

    Requires no build output and can be run standalone:
        .\scripts\validate-plugin-package.ps1
.PARAMETER PluginsRoot
    Folder holding one subfolder per plugin; skill checks run on each (default: plugins)
.PARAMETER PluginRoot
    Path to the winapp plugin package root (default: <PluginsRoot>/winapp)
.PARAMETER FailOnError
    Exit with code 1 when a conformance error is found (default: true)
#>

param(
    [string]$PluginsRoot = "",
    [string]$PluginRoot = "",
    [switch]$FailOnError = $true
)

$ProjectRoot = $PSScriptRoot | Split-Path -Parent
if (-not $PluginsRoot) {
    $PluginsRoot = Join-Path $ProjectRoot "plugins"
}
$PluginsRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PluginsRoot)
if (-not $PluginRoot) {
    $PluginRoot = Join-Path $PluginsRoot "winapp"
}

# Agent Skills hard limit is 1024; above the warning budget, descriptions cost context in
# every session without failing the build.
$DescriptionMaxChars = 1024
$DescriptionWarnChars = 300

$SchemaId = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json"
$McpSchemaId = "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json"
$AllowedFields = @(
    '$schema', 'name', 'version', 'description', 'author',
    'homepage', 'repository', 'license', 'keywords', 'extensions'
)
$CopilotAgent = "com.github.copilot/agents/winapp.agent.md"

$Errors = [System.Collections.Generic.List[string]]::new()
$Warnings = [System.Collections.Generic.List[string]]::new()
function Add-Failure([string]$Message) { $script:Errors.Add($Message) }
function Add-Warning([string]$Message) { $script:Warnings.Add($Message) }

function Read-JsonFile([string]$Path, [string]$Label) {
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
    try {
        return $text | ConvertFrom-Json -Depth 100
    }
    catch {
        Add-Failure "$Label is not valid JSON: $($_.Exception.Message)"
        return $null
    }
}

Write-Host "[VALIDATE] Checking Agent Plugins 1.0 conformance..." -ForegroundColor Blue
Write-Host "Plugin root: $PluginRoot" -ForegroundColor Gray

# --- Portable manifest (spec section 5) -------------------------------------------------
$ManifestPath = Join-Path $PluginRoot "plugin.json"
if (-not (Test-Path $ManifestPath -PathType Leaf)) {
    Add-Failure "portable manifest not found at $ManifestPath"
}
else {
    $manifest = Read-JsonFile $ManifestPath "plugins/winapp/plugin.json"
    if ($manifest) {
        if ($manifest.'$schema' -ne $SchemaId) {
            Add-Failure "plugins/winapp/plugin.json must declare `$schema as $SchemaId"
        }

        $name = $manifest.name
        if ($name -isnot [string] -or [string]::IsNullOrEmpty($name)) {
            Add-Failure "plugins/winapp/plugin.json requires a non-empty string 'name'"
        }
        elseif ($name.Length -gt 64 -or
                $name -cnotmatch '^[a-z0-9]([a-z0-9.\-]*[a-z0-9])?$' -or
                $name.Contains('--') -or $name.Contains('..')) {
            Add-Failure "plugins/winapp/plugin.json name '$name' violates Agent Plugins 1.0 name constraints (1-64 chars, lowercase a-z 0-9 . -, alphanumeric start/end, no '--' or '..')"
        }

        # The schema is closed: any other top-level field is a conformance violation.
        $unknown = @($manifest.PSObject.Properties.Name | Where-Object { $AllowedFields -cnotcontains $_ })
        if ($unknown.Count -gt 0) {
            Add-Failure "plugins/winapp/plugin.json has non-portable top-level field(s): $($unknown -join ', '). Component paths are auto-discovered; host-specific data belongs under 'extensions'."
        }

        foreach ($field in @('version', 'description', 'homepage', 'repository', 'license')) {
            $value = $manifest.PSObject.Properties[$field]
            if ($value -and $value.Value -isnot [string]) {
                Add-Failure "plugins/winapp/plugin.json '$field' must be a string"
            }
        }

        $author = $manifest.PSObject.Properties['author']
        if ($author) {
            if ($author.Value -isnot [System.Management.Automation.PSCustomObject]) {
                Add-Failure "plugins/winapp/plugin.json 'author' must be an object"
            }
            else {
                $badAuthor = @($author.Value.PSObject.Properties |
                    Where-Object { @('name', 'email', 'url') -cnotcontains $_.Name -or $_.Value -isnot [string] })
                if ($badAuthor.Count -gt 0) {
                    Add-Failure "plugins/winapp/plugin.json 'author' allows only string 'name', 'email', and 'url'"
                }
            }
        }

        $keywords = $manifest.PSObject.Properties['keywords']
        if ($keywords -and ($keywords.Value -isnot [array] -or
                @($keywords.Value | Where-Object { $_ -isnot [string] }).Count -gt 0)) {
            Add-Failure "plugins/winapp/plugin.json 'keywords' must be an array of strings"
        }

        $extensions = $manifest.PSObject.Properties['extensions']
        if ($extensions) {
            if ($extensions.Value -isnot [System.Management.Automation.PSCustomObject]) {
                Add-Failure "plugins/winapp/plugin.json 'extensions' must be an object"
            }
            elseif (@($extensions.Value.PSObject.Properties |
                    Where-Object { $_.Value -isnot [System.Management.Automation.PSCustomObject] }).Count -gt 0) {
                Add-Failure "plugins/winapp/plugin.json 'extensions' must map each namespace to an object"
            }
        }
    }
}

# --- Portable skills, fixed location (spec sections 6.1 and 7.1) ------------------------
$SkillsRoot = Join-Path $PluginRoot "skills"
if (-not (Test-Path $SkillsRoot -PathType Container)) {
    Add-Failure "portable skills directory not found at $SkillsRoot"
}
else {
    $skillDirs = @(Get-ChildItem $SkillsRoot -Directory)
    if ($skillDirs.Count -eq 0) {
        Add-Failure "no skill directories found under $SkillsRoot"
    }

    # Clients do not recurse past immediate children, so a deeper SKILL.md never loads.
    foreach ($stray in Get-ChildItem $SkillsRoot -Recurse -File -Filter "SKILL.md") {
        if ($stray.Directory.Parent.FullName -ne (Resolve-Path $SkillsRoot).Path) {
            $relative = $stray.FullName.Substring($PluginRoot.Length).TrimStart('\', '/')
            Add-Failure "SKILL.md at $relative is nested too deep to be discovered; skills must be immediate children of skills/"
        }
    }
}

# --- MCP configuration, only validated when present (spec section 7.2) ------------------
$McpPath = Join-Path $PluginRoot "mcp.json"
if (Test-Path $McpPath -PathType Leaf) {
    $mcp = Read-JsonFile $McpPath "plugins/winapp/mcp.json"
    if ($mcp) {
        if ($mcp.'$schema' -ne $McpSchemaId) {
            Add-Failure "plugins/winapp/mcp.json must declare `$schema as $McpSchemaId"
        }
        if (-not $mcp.PSObject.Properties['mcpServers']) {
            Add-Failure "plugins/winapp/mcp.json must contain 'mcpServers'"
        }
        $extraMcp = @($mcp.PSObject.Properties.Name | Where-Object { @('$schema', 'mcpServers') -cnotcontains $_ })
        if ($extraMcp.Count -gt 0) {
            Add-Failure "plugins/winapp/mcp.json has unsupported top-level field(s): $($extraMcp -join ', ')"
        }
    }
}

# --- Copilot extension namespace (spec section 8.2) -------------------------------------
if (-not (Test-Path (Join-Path $PluginRoot $CopilotAgent) -PathType Leaf)) {
    Add-Failure "Copilot agent not found at plugins/winapp/$CopilotAgent. Copilot-specific components must live under the com.github.copilot/ namespace."
}

# --- Claude Code compatibility ----------------------------------------------------------
# Claude is not an Agent Plugins client. It keeps its own manifest, and its 'agents' field
# points at the Copilot-namespaced file so the agent is not duplicated. A rename that
# breaks that pointer would otherwise fail silently for Claude users only.
$ClaudeManifestPath = Join-Path $PluginRoot ".claude-plugin/plugin.json"
if (-not (Test-Path $ClaudeManifestPath -PathType Leaf)) {
    Add-Failure "Claude Code manifest not found at $ClaudeManifestPath"
}
else {
    $claude = Read-JsonFile $ClaudeManifestPath "plugins/winapp/.claude-plugin/plugin.json"
    if ($claude) {
        $claudeAgents = @($claude.agents)
        if ($claudeAgents.Count -eq 0) {
            Add-Failure "plugins/winapp/.claude-plugin/plugin.json must declare 'agents' pointing at the Copilot-namespaced agent (Claude does not read com.github.copilot/ by default)"
        }
        foreach ($agentRef in $claudeAgents) {
            $resolved = Join-Path $PluginRoot ($agentRef -replace '^\./', '')
            if (-not (Test-Path $resolved -PathType Leaf)) {
                Add-Failure "plugins/winapp/.claude-plugin/plugin.json 'agents' entry '$agentRef' does not resolve to a file"
            }
        }
    }
}

# --- Repo-root legacy shim --------------------------------------------------------------
# The root manifest intentionally stays in the legacy Copilot format. Declaring $schema
# there would opt it into the closed schema, demoting its nested skills/agents paths to
# ignored unknown fields and breaking `copilot plugin install microsoft/WinAppCli`.
$RootManifestPath = Join-Path $ProjectRoot "plugin.json"
if (-not (Test-Path $RootManifestPath -PathType Leaf)) {
    Add-Failure "repo-root plugin.json shim not found"
}
else {
    $root = Read-JsonFile $RootManifestPath "plugin.json"
    if ($root) {
        if ($root.PSObject.Properties['$schema']) {
            Add-Failure "repo-root plugin.json must NOT declare `$schema. It is the legacy Copilot shim; adding `$schema turns its nested 'skills'/'agents' paths into ignored unknown fields and breaks 'copilot plugin install microsoft/WinAppCli'."
        }
        foreach ($field in @('agents', 'skills')) {
            foreach ($pathRef in @($root.$field)) {
                if (-not $pathRef) { continue }
                $resolved = Join-Path $ProjectRoot $pathRef
                if (-not (Test-Path $resolved)) {
                    Add-Failure "repo-root plugin.json '$field' path '$pathRef' does not exist"
                }
            }
        }
    }
}

# --- Skill content, for every plugin under $PluginsRoot that has skills ------------------
$Utf8 = [System.Text.UTF8Encoding]::new($false)
$Invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Get-DisplayPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.StartsWith($ProjectRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($ProjectRoot.Length + 1).Replace('\', '/')
    }
    return $full
}

function Get-ApproxTokens([long]$Bytes) { [long][math]::Ceiling($Bytes / 4) }

# Reads top-level YAML frontmatter keys: plain or quoted scalars and folded (>) or literal (|)
# block scalars. Returns an error string instead when the block is missing or unterminated.
function Read-Frontmatter([string[]]$Lines) {
    if ($Lines.Count -eq 0 -or $Lines[0].Trim() -ne '---') {
        return @{ Error = "is missing YAML frontmatter (first line must be '---')" }
    }
    $closing = -1
    for ($i = 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Trim() -eq '---') { $closing = $i; break }
    }
    if ($closing -lt 0) {
        return @{ Error = "has an unterminated YAML frontmatter block" }
    }

    $raw = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new()
    $key = $null
    for ($i = 1; $i -lt $closing; $i++) {
        if ($Lines[$i] -cmatch '^([A-Za-z0-9_-]+)\s*:(.*)$') {
            $key = $Matches[1]
            $raw[$key] = [System.Collections.Generic.List[string]]::new()
            $raw[$key].Add($Matches[2].Trim())
        }
        elseif ($key) {
            $raw[$key].Add($Lines[$i].Trim())
        }
    }

    $values = [System.Collections.Generic.Dictionary[string, string]]::new()
    foreach ($entry in $raw.GetEnumerator()) {
        $first = $entry.Value[0]
        $rest = @($entry.Value | Select-Object -Skip 1 | Where-Object { $_ })
        if ($first -match '^[>|][+-]?$') {
            $value = $rest -join $(if ($first[0] -eq '>') { ' ' } else { "`n" })
        }
        else {
            $value = (@($first) + $rest | Where-Object { $_ }) -join ' '
            if ($value -match '^"(.*)"$') { $value = $Matches[1] }
            elseif ($value -match "^'(.*)'$") { $value = $Matches[1] -replace "''", "'" }
        }
        $values[$entry.Key] = $value
    }
    return @{ Values = $values }
}

# Command paths come from the generated CLI schema plus the npm wrapper's own commands.
function New-CommandNode($Schema) {
    $node = [pscustomobject]@{
        Subcommands    = [System.Collections.Generic.Dictionary[string, object]]::new()
        TakesArguments = $false
    }
    if ($Schema) {
        $argProp = $Schema.PSObject.Properties['arguments']
        $node.TakesArguments = [bool]($argProp -and @($argProp.Value.PSObject.Properties).Count -gt 0)
        $subProp = $Schema.PSObject.Properties['subcommands']
        if ($subProp) {
            foreach ($sub in $subProp.Value.PSObject.Properties) {
                $child = New-CommandNode $sub.Value
                $node.Subcommands[$sub.Name] = $child
                foreach ($alias in @($sub.Value.aliases)) {
                    if ($alias) { $node.Subcommands[$alias] = $child }
                }
            }
        }
    }
    return $node
}

$CommandTree = $null
$CliSchemaPath = Join-Path $ProjectRoot "docs/cli-schema.json"
$NpmCliPath = Join-Path $ProjectRoot "src/winapp-npm/src/cli.ts"
if (-not (Test-Path $CliSchemaPath -PathType Leaf)) {
    Add-Failure "docs/cli-schema.json not found; it is needed to check winapp command examples in skills. Run scripts/build-cli.ps1 to regenerate it."
}
else {
    $cliSchema = Read-JsonFile $CliSchemaPath "docs/cli-schema.json"
    if ($cliSchema) {
        $CommandTree = New-CommandNode $cliSchema
        $npmCli = if (Test-Path $NpmCliPath -PathType Leaf) { [System.IO.File]::ReadAllText($NpmCliPath, $Utf8) } else { "" }
        if ($npmCli -match 'const NODE_SUBCOMMANDS = \[([^\]]*)\]') {
            $nodeCommand = New-CommandNode $null
            foreach ($m in [regex]::Matches($Matches[1], "'([^']+)'")) {
                $nodeCommand.Subcommands[$m.Groups[1].Value] = New-CommandNode $null
            }
            $CommandTree.Subcommands['node'] = $nodeCommand
        }
        else {
            Add-Failure "NODE_SUBCOMMANDS not found in src/winapp-npm/src/cli.ts; update validate-plugin-package.ps1 to read the npm wrapper-only commands from their new location."
        }
    }
}

# Tokens that may precede `winapp` on a command line (e.g. `npx winapp ...`, `& winapp ...`).
$LaunchTokens = @('npx', 'npm', 'pnpm', 'yarn', 'bunx', 'exec', 'dlx', '&', 'call')

function Test-CommandExample([string]$Line, [string]$Where) {
    if (-not $CommandTree) { return }
    $text = $Line.Trim() -replace '^(PS\b[^>]*>|[$>])\s+', ''
    if ($text -match '^(#|//|::|rem\s)') { return }
    $text = $text -replace '\s+#(\s.*)?$', ''

    foreach ($segment in $text -split '&&|\|\||[;|]') {
        $tokens = @($segment.Trim() -split '\s+' | ForEach-Object { $_ -replace '^\$?\(', '' } | Where-Object { $_ })
        $start = -1
        for ($i = 0; $i -lt $tokens.Count; $i++) {
            if ($tokens[$i] -ceq 'winapp') { $start = $i; break }
            if ($LaunchTokens -notcontains $tokens[$i] -and -not $tokens[$i].StartsWith('-')) { break }
        }
        if ($start -lt 0) { continue }

        $node = $CommandTree
        $path = 'winapp'
        for ($i = $start + 1; $i -lt $tokens.Count; $i++) {
            $token = $tokens[$i]
            # Options, paths, placeholders, variables and quoted values end the command path.
            if ($token -notmatch '^[A-Za-z][A-Za-z0-9-]*$') { break }
            if ($node.Subcommands.ContainsKey($token)) {
                $node = $node.Subcommands[$token]
                $path += " $token"
                continue
            }
            # Only a command that has subcommands and no positional arguments makes this an error.
            if ($node.Subcommands.Count -gt 0 -and -not $node.TakesArguments) {
                Add-Failure "$Where uses unknown command '$path $token'. Use a command listed in docs/cli-schema.json (or an npm wrapper command from src/winapp-npm/src/cli.ts)."
            }
            break
        }
    }
}

function Test-RelativeReference([string]$Target, [string[]]$BaseDirs, [string]$PluginDir, [string]$Where) {
    # Skip URLs (any scheme) and in-page anchors.
    if ($Target -match '^[A-Za-z][A-Za-z0-9+.-]*:' -or $Target.StartsWith('#')) { return }
    $pathPart = ($Target -split '[#?]', 2)[0]
    if (-not $pathPart) { return }
    $pathPart = [Uri]::UnescapeDataString($pathPart)

    $root = $PluginDir.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $insidePlugin = $false
    foreach ($base in $BaseDirs) {
        $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($base, $pathPart))
        if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            $insidePlugin = $true
            if (Test-Path -LiteralPath $full) { return }
        }
    }
    if ($insidePlugin) {
        Add-Failure "$Where references '$Target', which does not exist. Fix the path or add the file."
    }
    else {
        Add-Failure "$Where references '$Target', which is outside the plugin and will not be installed with it. Move the file into the plugin or use a full https:// URL (e.g. https://github.com/microsoft/winappCli/blob/main/docs/...)."
    }
}

# Checks relative links and `winapp` command examples in one Markdown file.
function Test-MarkdownFile([System.IO.FileInfo]$File, [string[]]$CodeRefBaseDirs, [string]$PluginDir) {
    $lines = [System.IO.File]::ReadAllLines($File.FullName, $Utf8)
    $display = Get-DisplayPath $File.FullName
    $fence = $null
    for ($n = 0; $n -lt $lines.Count; $n++) {
        $line = $lines[$n]
        $where = "${display}:$($n + 1)"
        if ($line -match '^\s{0,3}(`{3,}|~{3,})') {
            $marker = $Matches[1]
            if (-not $fence) { $fence = $marker; continue }
            if ($marker[0] -eq $fence[0] -and $marker.Length -ge $fence.Length -and $line.Trim() -eq $marker) {
                $fence = $null
                continue
            }
        }
        if ($fence) {
            Test-CommandExample $line $where
            continue
        }

        $withoutCode = $line -replace '`[^`]*`', ''
        foreach ($m in [regex]::Matches($withoutCode, '\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)')) {
            Test-RelativeReference $m.Groups[1].Value @($File.DirectoryName) $PluginDir $where
        }
        if ($line -match '^\s{0,3}\[[^\]]+\]:\s*<?([^\s>]+)') {
            Test-RelativeReference $Matches[1] @($File.DirectoryName) $PluginDir $where
        }
        # Inline-code paths into the Agent Skills resource folders are references too; other
        # inline paths (./dist, ./<name>) usually describe the user's project, not the plugin.
        if ($CodeRefBaseDirs.Count -gt 0) {
            foreach ($m in [regex]::Matches($line, '`((?:references|scripts|assets)/[^`\s<>{}*$]+)`')) {
                Test-RelativeReference $m.Groups[1].Value $CodeRefBaseDirs $PluginDir $where
            }
        }
    }
}

$SizeReport = [System.Text.StringBuilder]::new()
function Format-Count([long]$Value) { $Value.ToString('N0', $Invariant) }

$pluginDirs = @(Get-ChildItem $PluginsRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName "skills") -PathType Container })
foreach ($pluginDir in $pluginDirs) {
    $pluginPath = $pluginDir.FullName
    $rows = [System.Collections.Generic.List[string]]::new()
    $totals = @{ Desc = 0L; Meta = 0L; Body = 0L; Refs = 0L }

    foreach ($skillDir in Get-ChildItem (Join-Path $pluginPath "skills") -Directory) {
        $skillFile = Join-Path $skillDir.FullName "SKILL.md"
        $skillDisplay = Get-DisplayPath $skillFile
        if (-not (Test-Path $skillFile -PathType Leaf)) {
            Add-Failure "skill directory lacks SKILL.md: $(Get-DisplayPath $skillDir.FullName)"
            continue
        }

        $front = Read-Frontmatter ([System.IO.File]::ReadAllLines($skillFile, $Utf8))
        $name = ""
        $description = ""
        if ($front.Error) {
            Add-Failure "$skillDisplay $($front.Error)"
        }
        else {
            foreach ($key in @('name', 'description')) {
                if (-not $front.Values.ContainsKey($key) -or -not $front.Values[$key].Trim()) {
                    Add-Failure "$skillDisplay frontmatter is missing required '$key'"
                }
            }
            if ($front.Values.ContainsKey('name')) { $name = $front.Values['name'] }
            if ($front.Values.ContainsKey('description')) { $description = $front.Values['description'] }

            if ($name -and $name -cne $skillDir.Name) {
                Add-Failure "$skillDisplay name '$name' must equal its folder name '$($skillDir.Name)'"
            }
            if ($name -and ($name.Length -gt 64 -or $name -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$')) {
                Add-Failure "$skillDisplay name '$name' violates Agent Skills naming rules (1-64 chars, lowercase a-z 0-9 and single hyphens, no leading or trailing hyphen)"
            }
            if ($description.Length -gt $DescriptionMaxChars) {
                Add-Failure "$skillDisplay description is $($description.Length) characters; Agent Skills allows at most $DescriptionMaxChars. Shorten it."
            }
            elseif ($description.Length -gt $DescriptionWarnChars) {
                Add-Warning "$skillDisplay description is $($description.Length) characters (budget $DescriptionWarnChars). It is loaded into every session; consider trimming it."
            }
        }

        $skillMarkdown = @(Get-ChildItem $skillDir.FullName -Recurse -File -Filter "*.md")
        foreach ($md in $skillMarkdown) {
            Test-MarkdownFile $md @($md.DirectoryName, $skillDir.FullName) $pluginPath
        }

        $metaTokens = Get-ApproxTokens $Utf8.GetByteCount("${name}: $description")
        $bodyTokens = Get-ApproxTokens (Get-Item $skillFile).Length
        $refBytes = (@(Get-ChildItem $skillDir.FullName -Recurse -File | Where-Object { $_.FullName -ne (Get-Item $skillFile).FullName }) |
            Measure-Object -Property Length -Sum).Sum
        $refTokens = Get-ApproxTokens ([long]$refBytes)
        $rows.Add("| $($skillDir.Name) | $(Format-Count $description.Length) | $(Format-Count $metaTokens) | $(Format-Count $bodyTokens) | $(Format-Count $refTokens) |")
        $totals.Desc += $description.Length; $totals.Meta += $metaTokens; $totals.Body += $bodyTokens; $totals.Refs += $refTokens
    }

    $agentFiles = @(Get-ChildItem $pluginPath -Recurse -File -Filter "*.md" | Where-Object { $_.Directory.Name -eq 'agents' })
    foreach ($agentFile in $agentFiles) {
        Test-MarkdownFile $agentFile @() $pluginPath
        $front = Read-Frontmatter ([System.IO.File]::ReadAllLines($agentFile.FullName, $Utf8))
        $agentName = if ($front.Values -and $front.Values.ContainsKey('name')) { $front.Values['name'] } else { $agentFile.BaseName }
        $agentDescription = if ($front.Values -and $front.Values.ContainsKey('description')) { $front.Values['description'] } else { "" }
        $metaTokens = Get-ApproxTokens $Utf8.GetByteCount("${agentName}: $agentDescription")
        $bodyTokens = Get-ApproxTokens $agentFile.Length
        $rows.Add("| agent: $($agentFile.Name) | $(Format-Count $agentDescription.Length) | $(Format-Count $metaTokens) | $(Format-Count $bodyTokens) | |")
        $totals.Desc += $agentDescription.Length; $totals.Meta += $metaTokens; $totals.Body += $bodyTokens
    }

    [void]$SizeReport.AppendLine("### Plugin size: $($pluginDir.Name)")
    [void]$SizeReport.AppendLine("")
    [void]$SizeReport.AppendLine("| Component | Description chars | Metadata ~tokens | Body ~tokens | References ~tokens |")
    [void]$SizeReport.AppendLine("|---|--:|--:|--:|--:|")
    foreach ($row in $rows) { [void]$SizeReport.AppendLine($row) }
    [void]$SizeReport.AppendLine("| **Total** | $(Format-Count $totals.Desc) | **$(Format-Count $totals.Meta)** | $(Format-Count $totals.Body) | $(Format-Count $totals.Refs) |")
    [void]$SizeReport.AppendLine("")
    [void]$SizeReport.AppendLine("Approximate: ~tokens = UTF-8 bytes / 4. Metadata (``name: description``) is loaded in every session; the metadata total is the plugin's always-loaded cost. Body loads when a skill or agent activates; references load on demand.")
    [void]$SizeReport.AppendLine("")
}

if ($SizeReport.Length -gt 0) {
    Write-Host ""
    Write-Host $SizeReport.ToString()
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $SizeReport.ToString() -Encoding utf8
    }
}

# --- Report ------------------------------------------------------------------------------
foreach ($warning in $Warnings) {
    Write-Host "::warning::$warning" -ForegroundColor Yellow
}

if ($Errors.Count -gt 0) {
    foreach ($failure in $Errors) {
        Write-Host "::error::$failure" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "See https://agent-plugins.org/specification and https://agentskills.io/specification for the rules." -ForegroundColor Yellow
    if ($FailOnError) {
        exit 1
    }
    exit 0
}

Write-Host "[VALIDATE] plugins/winapp conforms to Agent Plugins 1.0 and all plugin skills passed checks" -ForegroundColor Green
exit 0
