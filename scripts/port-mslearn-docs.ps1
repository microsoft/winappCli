#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Port winapp CLI docs to MS Learn format
.DESCRIPTION
    Transforms documentation from this repo into MS Learn-ready format:
    1. Copies mapped doc files to an output directory mirroring the docs repo structure
    2. Adds YAML front matter (title, description, ms.date, ms.topic)
    3. Rewrites links using a canonical source→destination map
    4. Flattens electron subfolder guides
    5. Copies referenced images
    6. Generates guides/index.md
    7. Generates toc.yml (the MS Learn left-nav for the winapp-cli node)

    The output directory can be directly committed to MicrosoftDocs/windows-dev-docs-pr
    under hub/apps/dev-tools/winapp-cli/.
.PARAMETER OutputPath
    Output directory for ported docs (default: artifacts/mslearn-docs)
.PARAMETER Version
    Version label for metadata (default: from version.json)
.EXAMPLE
    .\scripts\port-mslearn-docs.ps1
.EXAMPLE
    .\scripts\port-mslearn-docs.ps1 -OutputPath "./my-output"
#>

param(
    [string]$OutputPath = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
$DocsRoot = Join-Path $ProjectRoot "docs"

# Shared MS Learn front-matter rules (title/description/topic + YAML quoting),
# kept in one place so this generator and validate-mslearn-docs.ps1 can't drift.
. (Join-Path $PSScriptRoot 'mslearn-doc-lib.ps1')

# ─── Helpers ────────────────────────────────────────────────────────────────────

function Write-Step  { param([string]$msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Info  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Gray }
function Write-Ok    { param([string]$msg) Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Yellow }

# ─── Configuration ──────────────────────────────────────────────────────────────

$GitHubRepoBase = "https://github.com/microsoft/WinAppCli"

# Per-file metadata is read from HTML-comment markers in source docs:
#   <!-- mslearn: true -->         (required opt-in marker)
#   <!-- ms.topic: overview -->    (optional, defaults to "how-to")
#   <!-- description: ... -->      (optional, defaults to page title)
# Repo-only link targets (anything outside docs/ or not opted-in) are
# auto-rewritten to GitHub URLs by Resolve-DocLink — no list to maintain.

# ─── Step 0: Resolve parameters ────────────────────────────────────────────────

if (-not $OutputPath) {
    $OutputPath = Join-Path $ProjectRoot "artifacts\mslearn-docs"
}

if (-not $Version) {
    $versionJson = Get-Content (Join-Path $ProjectRoot "version.json") -Raw | ConvertFrom-Json
    $Version = $versionJson.version
}

$msDate = Get-Date -Format "MM/dd/yyyy"

Write-Step "Porting docs for v$Version"
Write-Info "Output: $OutputPath"

# ─── Step 0.5: Validate source docs against MS Learn publishing rules ────────────
# Fail fast (and identically in CI) if any opted-in doc violates the docs-repo
# conventions the reviewer enforces, so issues are fixed at the source of truth
# rather than in generated output. See scripts/validate-mslearn-docs.ps1.

Write-Step "Validating source docs"
$validator = Join-Path $PSScriptRoot "validate-mslearn-docs.ps1"
# Run in a separate process so the validator's `exit 1` can't terminate this
# script before we inspect the exit code and emit our own error (matches how the
# release stage and the Pester tests invoke it).
& pwsh -NoProfile -File $validator -DocsRoot $DocsRoot
if ($LASTEXITCODE -ne 0) {
    Write-Error "MS Learn doc validation failed. Fix the reported issues before porting."
    exit $LASTEXITCODE
}

# ─── Step 1: Auto-discover files to port ────────────────────────────────────────

Write-Step "Discovering docs to port"

# Find all .md files under docs/
$allDocFiles = Get-ChildItem (Join-Path $ProjectRoot "docs") -Recurse -File |
    Where-Object { $_.Extension -eq ".md" -or $_.Extension -eq ".png" -or $_.Extension -eq ".jpg" -or $_.Extension -eq ".gif" }

# Build file mapping by auto-discovery
$FileMapping = [ordered]@{}
$ImageMapping = [ordered]@{}

foreach ($file in $allDocFiles) {
    $repoRelPath = $file.FullName.Substring($ProjectRoot.Length + 1) -replace '\\', '/'
    $docsRelPath = $repoRelPath.Substring("docs/".Length)  # path relative to docs/

    # Handle images first — they're always included if referenced by ported docs
    if ($file.Extension -in @(".png", ".jpg", ".gif")) {
        $parentDir = ($docsRelPath -replace '[^/]+$', '').TrimEnd('/')
        if ($parentDir -eq "images") {
            $destRel = "media/$($file.Name)"
        } elseif ($parentDir -match '^(.*)/images$') {
            $destRel = "$($Matches[1])/media/$($file.Name)"
        } else {
            $destRel = "$parentDir/media/$($file.Name)"
        }
        $ImageMapping[$repoRelPath] = $destRel
        continue
    }

    # Check for <!-- mslearn: true --> marker (opt-in model)
    $fileHead = Get-Content $file.FullName -TotalCount 10 -ErrorAction SilentlyContinue | Out-String
    if ($fileHead -notmatch '<!--\s*mslearn:\s*true\s*-->') {
        continue  # not marked for MS Learn
    }

    # Compute output path
    # Special case: README.md → index.md
    if ($docsRelPath -eq "README.md") {
        $destRel = "index.md"
    }
    # Flatten electron subfolder: guides/electron/foo.md → guides/electron-foo.md
    elseif ($docsRelPath -match '^guides/electron/(.+)$') {
        $destRel = "guides/electron-$($Matches[1])"
    }
    else {
        $destRel = $docsRelPath
    }

    $FileMapping[$repoRelPath] = $destRel
    Write-Info "  $repoRelPath -> $destRel"
}

Write-Info "Discovered $($FileMapping.Count) doc files + $($ImageMapping.Count) images"

# ─── Step 2: Prepare output directory ───────────────────────────────────────────

if (Test-Path $OutputPath) {
    Remove-Item $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputPath "guides") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputPath "guides\media") -Force | Out-Null

# ─── Step 3: Build the canonical link resolution map ────────────────────────────

Write-Step "Building link resolution map"

# Map from repo-relative source path → output-relative path (for files we port)
# Map from repo-relative source path → GitHub URL (for files we don't port)
$LinkMap = @{}

# Add all ported files
foreach ($entry in $FileMapping.GetEnumerator()) {
    $LinkMap[$entry.Key] = @{ Type = "ported"; Path = $entry.Value }
}

# Add image files
foreach ($entry in $ImageMapping.GetEnumerator()) {
    $LinkMap[$entry.Key] = @{ Type = "ported"; Path = $entry.Value }
}

Write-Info "Map has $($LinkMap.Count) entries ($($FileMapping.Count) ported + $($ImageMapping.Count) images)"

# ─── Link resolution function ──────────────────────────────────────────────────

function Resolve-DocLink {
    param(
        [string]$Href,
        [string]$SourceRepoPath  # repo-relative path of the file containing the link
    )

    # Skip external URLs, anchors-only, mailto, and MS Learn absolute paths
    if ($Href -match '^https?://' -or $Href -match '^mailto:' -or $Href -match '^#') {
        return $null  # null = leave unchanged
    }

    # MS Learn cross-docs relative paths (start with /windows/, /dotnet/, etc.) — leave as-is
    if ($Href -match '^/') {
        return $null
    }

    # Separate anchor from path
    $anchor = ""
    if ($Href -match '^([^#]*)(.*)$') {
        $hrefPath = $Matches[1]
        $anchor = $Matches[2]
    }

    if (-not $hrefPath) {
        return $null  # anchor-only link
    }

    # Resolve relative path against source file's directory
    $sourceDir = ($SourceRepoPath -replace '[^/]+$', '').TrimEnd('/')
    $resolved = "$sourceDir/$hrefPath" -replace '\\', '/'

    # Normalize: resolve ../ segments
    $parts = $resolved -split '/'
    $normalized = [System.Collections.Generic.List[string]]::new()
    foreach ($part in $parts) {
        if ($part -eq '..') {
            if ($normalized.Count -gt 0) {
                $normalized.RemoveAt($normalized.Count - 1)
            }
        }
        elseif ($part -ne '.' -and $part -ne '') {
            $normalized.Add($part)
        }
    }
    $repoPath = ($normalized -join '/').TrimEnd('/')

    # Look up in the map
    if ($LinkMap.ContainsKey($repoPath)) {
        $target = $LinkMap[$repoPath]
        if ($target.Type -eq "ported") {
            # Compute relative path from source output file to target output file
            $sourceOutputPath = $FileMapping[$SourceRepoPath]
            $sourceOutputDir = ($sourceOutputPath -replace '[^/]+$', '').TrimEnd('/')
            $targetOutputPath = $target.Path

            # Compute relative path
            $sourceParts = @(($sourceOutputDir -split '/') | Where-Object { $_ })
            $targetParts = @(($targetOutputPath -split '/') | Where-Object { $_ })

            # Find common prefix length
            $commonLen = 0
            for ($i = 0; $i -lt [Math]::Min($sourceParts.Count, $targetParts.Count - 1); $i++) {
                if ($sourceParts[$i] -eq $targetParts[$i]) { $commonLen++ } else { break }
            }

            # Build relative path
            $upCount = $sourceParts.Count - $commonLen
            $relParts = @()
            for ($i = 0; $i -lt $upCount; $i++) { $relParts += ".." }
            for ($i = $commonLen; $i -lt $targetParts.Count; $i++) { $relParts += $targetParts[$i] }

            $relPath = ($relParts -join '/')
            if (-not $relPath) { $relPath = "." }
            return "$relPath$anchor"
        }
        elseif ($target.Type -eq "github") {
            return "$($target.Url)$anchor"
        }
    }

    # Not in map — if the path actually exists in the repo, rewrite to GitHub.
    # Warn only when the target doesn't exist (likely a broken link).
    if ($repoPath -and -not ($repoPath -match '^\w+://')) {
        $absPath = Join-Path $ProjectRoot ($repoPath -replace '/', '\')
        $exists = Test-Path $absPath
        $isFile = $repoPath -match '\.\w+$'
        if (-not $exists) {
            Write-Warn "  Unmapped link in $SourceRepoPath : $Href (resolved: $repoPath)"
        }
        $ghBase = if ($isFile) { "$GitHubRepoBase/blob/main" } else { "$GitHubRepoBase/tree/main" }
        return "$ghBase/$repoPath$anchor"
    }

    return $null
}

# ─── learn.microsoft.com URL rewriter ──────────────────────────────────────────

function Rewrite-LearnUrls {
    param([string]$Content)

    # Convert https://learn.microsoft.com[/en-us]/path/... → /path/...
    $Content = [regex]::Replace($Content, 'https://learn\.microsoft\.com(?:/en-us)?((?:/[\w-]+)+[^\s\)\]"]*)', '$1')

    return $Content
}

# ─── Front matter generator ────────────────────────────────────────────────────

function Get-FrontMatter {
    param(
        [string]$Content,
        [string]$DestRelPath
    )

    # Title = first H1 (fall back to a sane default for untitled docs).
    $title = Get-MsLearnTitle $Content
    if (-not $title) { $title = "winapp CLI" }

    # Description + ms.topic resolved via the shared rules (marker override,
    # else defaults). Quoting is applied through Format-MsLearnYamlValue so the
    # emitted values always match what the validator gates on.
    $description = (Resolve-MsLearnDescription -Content $Content -Title $title).Description
    $topic = Get-MsLearnTopic -Content $Content

    $safeTitle = Format-MsLearnYamlValue $title
    $safeDesc  = Format-MsLearnYamlValue $description

    $lines = @(
        "---"
        "title: $safeTitle"
        "description: $safeDesc"
        "ms.date: $msDate"
        "ms.topic: $topic"
        "---"
        ""
    )
    return ($lines -join "`r`n") + "`r`n"
}

# ─── Step 3: Process and copy files ─────────────────────────────────────────────

Write-Step "Processing files"

foreach ($entry in $FileMapping.GetEnumerator()) {
    $sourcePath = Join-Path $ProjectRoot ($entry.Key -replace '/', '\')
    $destRelPath = $entry.Value
    $destPath = Join-Path $OutputPath ($destRelPath -replace '/', '\')
    $destPath = [System.IO.Path]::GetFullPath($destPath)
    if (-not (Test-Path $sourcePath)) {
        Write-Warn "  MISSING: $($entry.Key) — skipping"
        continue
    }

    # Read content
    $content = Get-Content $sourcePath -Raw
    $rawContent = $content  # preserve metadata markers for front matter extraction

    # Strip the mslearn marker comments (mslearn: true, description, ms.topic)
    $content = $content -replace '(?m)^\s*<!--\s*mslearn:\s*true\s*-->\s*\r?\n?', ''
    $content = $content -replace '(?m)^\s*<!--\s*description:\s*.+?\s*-->\s*\r?\n?', ''
    $content = $content -replace '(?m)^\s*<!--\s*ms\.topic:\s*.+?\s*-->\s*\r?\n?', ''

    # Protect code blocks from link rewriting and URL rewriting by temporarily replacing them
    $codeBlocks = [System.Collections.Generic.List[string]]::new()
    $content = [regex]::Replace($content, '(?ms)(```[^\n]*\n.*?```)', {
        param($match)
        $idx = $codeBlocks.Count
        $codeBlocks.Add($match.Value)
        return "%%CODEBLOCK_${idx}%%"
    })

    # Protect inline code spans from placeholder escaping and URL rewriting
    $inlineCode = [System.Collections.Generic.List[string]]::new()
    $content = [regex]::Replace($content, '(`[^`]+`)', {
        param($match)
        $idx = $inlineCode.Count
        $inlineCode.Add($match.Value)
        return "%%INLINECODE_${idx}%%"
    })

    # Rewrite learn.microsoft.com URLs to relative paths (after code protection)
    $content = Rewrite-LearnUrls $content

    # Escape bare <placeholder> patterns that MS Learn treats as HTML tags.
    # Matches <word>, <word-word>, <word word> not already inside backticks or code blocks.
    # Skip legitimate HTML tags whose closing form (</tag>) wouldn't be escaped by this rule,
    # which would otherwise produce mismatched &lt;tag&gt; ... </tag> pairs in the output.
    # Compare only the tag name (first token), so allowed tags with attributes like
    # <details open> are preserved as HTML instead of being partially escaped.
    $htmlPassthroughTags = @('details', 'summary', 'br', 'hr', 'sub', 'sup', 'kbd', 'b')
    $content = [regex]::Replace($content, '<([\w][\w\s-]*)>', {
        param($match)
        $tag = $match.Groups[1].Value
        $tagName = ($tag -split '\s+', 2)[0]
        if ($htmlPassthroughTags -contains $tagName.ToLowerInvariant()) {
            return $match.Value
        }
        return "&lt;$tag&gt;"
    })

    # Rewrite image links first (before regular links, since ![...]() also matches [...]() regex)
    $content = [regex]::Replace($content, '!\[([^\]]*)\]\(([^)]+)\)', {
        param($match)
        $alt = $match.Groups[1].Value
        $href = $match.Groups[2].Value
        $newHref = Resolve-DocLink -Href $href -SourceRepoPath $entry.Key
        if ($null -ne $newHref) {
            return "![$alt]($newHref)"
        }
        return $match.Value
    })

    # Rewrite markdown links (excluding images which start with !)
    $content = [regex]::Replace($content, '(?<!!)(\[([^\]]*)\]\(([^)]+)\))', {
        param($match)
        $text = $match.Groups[2].Value
        $href = $match.Groups[3].Value
        $newHref = Resolve-DocLink -Href $href -SourceRepoPath $entry.Key
        if ($null -ne $newHref) {
            return "[$text]($newHref)"
        }
        return $match.Groups[1].Value
    })

    # Restore inline code spans
    for ($i = 0; $i -lt $inlineCode.Count; $i++) {
        $content = $content.Replace("%%INLINECODE_${i}%%", $inlineCode[$i])
    }

    # Restore code blocks
    for ($i = 0; $i -lt $codeBlocks.Count; $i++) {
        $content = $content.Replace("%%CODEBLOCK_${i}%%", $codeBlocks[$i])
    }

    # Add YAML front matter
    $frontMatter = Get-FrontMatter -Content $rawContent -DestRelPath $destRelPath
    $content = $frontMatter + $content

    # Write to output
    $destDir = Split-Path $destPath -Parent
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Set-Content -Path $destPath -Value $content -Encoding UTF8 -NoNewline

    Write-Info "  $($entry.Key) -> $destRelPath"
}

# ─── Step 4: Copy images (only those referenced by ported docs) ────────────────

Write-Step "Copying referenced images"

# Collect all written markdown content to check image references
$allPortedContent = Get-ChildItem $OutputPath -Recurse -File -Filter "*.md" | ForEach-Object {
    Get-Content $_.FullName -Raw
} | Out-String

foreach ($entry in $ImageMapping.GetEnumerator()) {
    $sourcePath = Join-Path $ProjectRoot ($entry.Key -replace '/', '\')
    $destPath = Join-Path $OutputPath ($entry.Value -replace '/', '\')
    $imageName = Split-Path $entry.Value -Leaf

    # Only copy if the image filename appears in any ported doc
    if ($allPortedContent -notmatch [regex]::Escape($imageName)) {
        Write-Info "  SKIPPED (unreferenced): $($entry.Key)"
        continue
    }

    if (-not (Test-Path $sourcePath)) {
        Write-Warn "  MISSING: $($entry.Key) — skipping"
        continue
    }

    $destDir = Split-Path $destPath -Parent
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    Copy-Item $sourcePath $destPath -Force
    Write-Info "  $($entry.Key) -> $($entry.Value)"
}

# ─── Step 5: Generate guides/index.md from ported index.md ─────────────────────

Write-Step "Generating guides/index.md"

# Extract the framework table and additional guides from the ported index.md
# (docs/README.md → index.md) so there's a single source of truth.
$portedIndexPath = Join-Path $OutputPath "index.md"
$portedIndexContent = Get-Content $portedIndexPath -Raw

# Extract the "Supported frameworks" section (table + additional guides)
$frameworkSection = ""
if ($portedIndexContent -match '(?ms)(## Supported frameworks\s*\n.+?)(?=\n## )') {
    $frameworkSection = $Matches[1]
    # The extracted links are index.md-relative (i.e. output-root-relative) but are
    # about to be written into guides/index.md, one directory down. Rebase them:
    #   guides/foo.md -> foo.md          (now a sibling)
    #   security.md   -> ../security.md  (still at the root; a bare name would 404)
    # Absolute URLs, mailto:, in-page anchors and already-relative paths are left alone.
    # Root-relative links must be rewritten first, while the guides/ prefix still
    # marks which targets are siblings.
    $frameworkSection = $frameworkSection -replace '\]\((?!https?:|mailto:|#|/|\.{1,2}/)(?!guides/)([^)]+)\)', '](../$1)'
    $frameworkSection = $frameworkSection -replace '\]\(guides/', ']('
}

# Build guides/index.md using the extracted content
$guidesIndex = @"
---
title: winapp CLI framework guides
description: Step-by-step guides for using the winapp CLI with .NET, C++, Electron, Rust, Tauri, Flutter, and other frameworks.
ms.date: $msDate
ms.topic: overview
---

# Framework guides

These guides walk you through using the winapp CLI with your app framework — from project setup to debugging with package identity to packaging as MSIX.

"@

if ($frameworkSection) {
    # Strip the "## Supported frameworks" heading and the intro line, keep just the table + additional guides
    $tableContent = $frameworkSection -replace '(?ms)^## Supported frameworks\s*\n+.*?app frameworks:\s*\n+', ''
} else {
    Write-Warn "  Could not extract framework section from index.md — using fallback"
    $tableContent = @"
| Framework | Guide |
|-----------|-------|
| .NET / WPF / WinForms | [Get started with .NET](dotnet.md) |
| C++ (CMake) | [Get started with C++](cpp.md) |
| Electron | [Get started with Electron](electron-index.md) |
| Rust | [Get started with Rust](rust.md) |
| Tauri | [Get started with Tauri](tauri.md) |
| Flutter | [Get started with Flutter](flutter.md) |
"@
}

# A here-string does not include the newline before its closing "@, so the blank
# line intended to separate the intro paragraph from the table collapses away and
# Markdown renders the table as part of that paragraph. Join explicitly rather
# than relying on the here-string to carry the separator.
$guidesIndex = $guidesIndex.TrimEnd() + "`r`n`r`n" + $tableContent.TrimStart()

# Add Electron deep-dive section (these only exist as guides, not in README)
# Normalize the boundary: the extracted framework/additional-guides block can
# carry a trailing bare CR from the source capture, which renders as a stray
# character before the next heading. Trim it and join with exactly one blank line.
$guidesIndex = $guidesIndex.TrimEnd() + "`r`n`r`n" + @"
## Electron deep-dive guides

After completing the [Electron setup guide](electron-setup.md):

| Guide | Description |
|-------|-------------|
| [Package for distribution](electron-packaging.md) | Create an MSIX package for your Electron app |
| [Phi Silica addon](electron-phi-silica-addon.md) | On-device AI with the Phi Silica model |
| [WinML addon](electron-winml-addon.md) | Machine learning inference with Windows ML |
| [C++ notification addon](electron-cpp-notification-addon.md) | Native Windows notifications from Electron |
"@

$guidesIndexPath = Join-Path $OutputPath "guides\index.md"
Set-Content -Path $guidesIndexPath -Value $guidesIndex -NoNewline -Encoding UTF8
Write-Info "  Generated guides/index.md"

# ─── Step 7: Generate toc.yml (MS Learn left-nav) ───────────────────────────────
#
# Without a toc.yml in the winapp-cli folder, MS Learn renders no nested navigation
# under the winapp-cli node — pages are only reachable via "Related topics". This
# step emits the child TOC so every ported page appears in the left-hand nav.
#
# NOTE: The *parent* dev-tools TOC in MicrosoftDocs/windows-dev-docs-pr must branch
# into this file, e.g.:
#     - name: winapp CLI
#       href: winapp-cli/toc.yml
# That one-time edit lives in the docs repo (outside the winapp-cli folder this
# script writes) and is not something the port script can generate.

Write-Step "Generating toc.yml"

# Curated left-nav labels keyed by output-relative path. Anything ported but not
# listed here falls back to its H1 title (and is flagged so a maintainer can add it).
$TocLabels = [ordered]@{
    "index.md"                                   = "winapp CLI overview"
    "usage.md"                                   = "Commands and usage"
    "debugging.md"                               = "Debugging with package identity"
    "ui-automation.md"                           = "UI Automation"
    "sandbox-execution.md"                       = "Windows Sandbox execution"
    "security.md"                                = "Security guidance"
    "guides/index.md"                            = "Framework guides"
    "guides/dotnet.md"                           = ".NET / WPF / WinForms"
    "guides/maui.md"                             = ".NET MAUI"
    "guides/cpp.md"                              = "C++ (CMake)"
    "guides/rust.md"                             = "Rust"
    "guides/tauri.md"                            = "Tauri"
    "guides/flutter.md"                          = "Flutter"
    "guides/packaging-cli.md"                    = "Package an EXE or CLI"
    "guides/sparse.md"                           = "Sparse packaging"
    "guides/shell-completion.md"                 = "Shell completion"
    "guides/electron-index.md"                   = "Electron"
    "guides/electron-setup.md"                   = "Set up the environment"
    "guides/electron-packaging.md"               = "Package for distribution"
    "guides/electron-js-file-picker.md"          = "File picker (JS bindings)"
    "guides/electron-js-notification.md"         = "Notifications (JS bindings)"
    "guides/electron-js-phi-silica.md"           = "Phi Silica (JS bindings)"
    "guides/electron-js-winml.md"                = "WinML (JS bindings)"
    "guides/electron-phi-silica-addon.md"        = "Phi Silica addon"
    "guides/electron-winml-addon.md"             = "WinML addon"
    "guides/electron-cpp-notification-addon.md"  = "C++ notification addon"
}

# Set of markdown files actually present in the output (drives inclusion + warnings)
$outFull = (Resolve-Path $OutputPath).Path
$portedDest = @{}
Get-ChildItem $outFull -Recurse -File -Filter "*.md" | ForEach-Object {
    $rel = [System.IO.Path]::GetRelativePath($outFull, $_.FullName) -replace '\\', '/'
    $portedDest[$rel] = $true
}


function Get-PortedTitle {
    param([string]$DestRel)
    $p = Join-Path $outFull ($DestRel -replace '/', '\')
    if (-not (Test-Path $p)) { return $DestRel }
    # Reuse the shared H1 parser so title extraction is identical to the
    # validator and front-matter generation.
    $title = Get-MsLearnTitle (Get-Content $p -Raw)
    if ($title) { return $title }
    return $DestRel
}

function New-TocNode {
    param([string]$Href, [object[]]$Items = @())
    $name = if ($TocLabels.Contains($Href)) { $TocLabels[$Href] } else { Get-PortedTitle $Href }
    return @{ Name = $name; Href = $Href; Items = $Items }
}

function ConvertTo-TocLines {
    param([object[]]$Nodes, [int]$Indent = 0)
    $lines = [System.Collections.Generic.List[string]]::new()
    $pad = ' ' * $Indent
    foreach ($node in $Nodes) {
        if (-not $portedDest.ContainsKey($node.Href)) {
            Write-Warn "  Skipping TOC entry for un-ported file: $($node.Href)"
            continue
        }
        $lines.Add("$pad- name: $(Format-MsLearnYamlValue $node.Name)")
        $lines.Add("$pad  href: $($node.Href)")
        if ($node.Items -and $node.Items.Count -gt 0) {
            $childLines = ConvertTo-TocLines -Nodes $node.Items -Indent ($Indent + 2)
            if ($childLines.Count -gt 0) {
                $lines.Add("$pad  items:")
                foreach ($cl in $childLines) { $lines.Add($cl) }
            }
        }
    }
    return , $lines.ToArray()
}

function Get-TocHrefs {
    param([object[]]$Nodes)
    $hrefs = [System.Collections.Generic.List[string]]::new()
    foreach ($node in $Nodes) {
        $hrefs.Add($node.Href)
        if ($node.Items -and $node.Items.Count -gt 0) {
            foreach ($h in (Get-TocHrefs -Nodes $node.Items)) { $hrefs.Add($h) }
        }
    }
    return , $hrefs.ToArray()
}

$tocTree = @(
    (New-TocNode "index.md")
    (New-TocNode "usage.md")
    (New-TocNode "debugging.md")
    (New-TocNode "ui-automation.md")
    (New-TocNode "sandbox-execution.md")
    (New-TocNode "security.md")
    (New-TocNode "guides/index.md" @(
        (New-TocNode "guides/dotnet.md")
        (New-TocNode "guides/maui.md")
        (New-TocNode "guides/cpp.md")
        (New-TocNode "guides/electron-index.md" @(
            (New-TocNode "guides/electron-setup.md")
            (New-TocNode "guides/electron-packaging.md")
            (New-TocNode "guides/electron-js-file-picker.md")
            (New-TocNode "guides/electron-js-notification.md")
            (New-TocNode "guides/electron-js-phi-silica.md")
            (New-TocNode "guides/electron-js-winml.md")
            (New-TocNode "guides/electron-phi-silica-addon.md")
            (New-TocNode "guides/electron-winml-addon.md")
            (New-TocNode "guides/electron-cpp-notification-addon.md")
        ))
        (New-TocNode "guides/rust.md")
        (New-TocNode "guides/tauri.md")
        (New-TocNode "guides/flutter.md")
        (New-TocNode "guides/packaging-cli.md")
        (New-TocNode "guides/sparse.md")
        (New-TocNode "guides/shell-completion.md")
    ))
)

# Warn about any ported page missing from the TOC so nothing silently drops out of nav
$tocHrefs = @{}
foreach ($h in (Get-TocHrefs -Nodes $tocTree)) { $tocHrefs[$h] = $true }
foreach ($dest in ($portedDest.Keys | Sort-Object)) {
    if (-not $tocHrefs.ContainsKey($dest)) {
        Write-Warn "  Ported page not in toc.yml: $dest — add it to the TOC tree in port-mslearn-docs.ps1"
    }
}

$tocLines = ConvertTo-TocLines -Nodes $tocTree -Indent 0
$tocContent = ($tocLines -join "`r`n") + "`r`n"
$tocPath = Join-Path $OutputPath "toc.yml"
Set-Content -Path $tocPath -Value $tocContent -Encoding UTF8 -NoNewline
Write-Info "  Generated toc.yml ($($tocLines.Count) lines)"

# ─── Step 8: Summary ───────────────────────────────────────────────────────────

Write-Step "Done"

$fileCount = (Get-ChildItem $OutputPath -Recurse -File | Where-Object { $_.Extension -eq ".md" }).Count
$imageCount = (Get-ChildItem $OutputPath -Recurse -File | Where-Object { $_.Extension -in @(".png", ".jpg", ".gif") }).Count

Write-Ok "Ported $fileCount markdown files + $imageCount images to: $OutputPath"
Write-Info "Ready to commit to MicrosoftDocs/windows-dev-docs-pr under hub/apps/dev-tools/winapp-cli/"
