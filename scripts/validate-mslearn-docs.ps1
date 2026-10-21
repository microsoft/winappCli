#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate winapp CLI source docs against MS Learn publishing rules.
.DESCRIPTION
    Enforces the docs-repo conventions that the MicrosoftDocs/windows-dev-docs-pr
    review (including the Copilot reviewer) checks, so that ported docs are clean
    BEFORE a PR is opened. Runs against the hand-authored source under docs/ — the
    same files port-mslearn-docs.ps1 consumes — so failures are fixed at the source
    of truth rather than in the generated output.

    HARD-FAIL checks (exit code 1):
      * Every doc opted into MS Learn (<!-- mslearn: true -->) must resolve a
        front-matter description that is:
          - present via <!-- description: ... --> (not defaulted to the title)
          - not equal to the page title
          - 115–145 characters
          - free of YAML-special characters that would force the value to be quoted
      * No banned marketing words anywhere in the doc (title, prose, or sample text).
      * No blockquotes used as callouts (a block that opens with "> **Bold lead-in**")
        without MS Learn alert syntax (> [!NOTE] / [!IMPORTANT] / [!TIP] / [!WARNING]
        / [!CAUTION]). Learn renders those as plain quotes, and the port script
        would overwrite any fix made by hand in the docs repo.

    Front-matter resolution + quoting rules live in mslearn-doc-lib.ps1, which
    both this validator and port-mslearn-docs.ps1 dot-source, so the gate and the
    generator cannot drift.
.PARAMETER DocsRoot
    Root of the docs tree to validate (default: <repo>/docs).
.EXAMPLE
    ./scripts/validate-mslearn-docs.ps1
#>

param(
    [string]$DocsRoot = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
if (-not $DocsRoot) { $DocsRoot = Join-Path $ProjectRoot "docs" }

# Shared MS Learn front-matter rules (title/description/topic resolution + the
# YAML-quoting rule). Sourcing them here — instead of re-implementing — is what
# guarantees this gate and port-mslearn-docs.ps1 stay in lock-step.
. (Join-Path $PSScriptRoot 'mslearn-doc-lib.ps1')

# Display paths relative to the docs root's parent so they read like "docs/..."
# regardless of whether DocsRoot is inside this repo or an external/temp path.
$DocsRootFull = [System.IO.Path]::GetFullPath($DocsRoot)
$DisplayBase = Split-Path $DocsRootFull -Parent

# ─── Rules (front-matter quoting/resolution live in mslearn-doc-lib.ps1) ─────────

$DescriptionMin = 115
$DescriptionMax = 145

# Marketing / promotional words disallowed by the docs style guidance. Scanned
# across the whole file (including sample strings, which is where they hide).
$BannedWords = @(
    'powerful', 'seamless', 'seamlessly', 'cutting-edge', 'world-class',
    'revolutionary', 'effortless', 'effortlessly', 'blazing', 'game-changing',
    'best-in-class', 'state-of-the-art', 'unleash', 'supercharge', 'robust'
)

$AlertPattern = '^\s*>\s*\[!(NOTE|IMPORTANT|TIP|WARNING|CAUTION)\]'
$CalloutPattern = '^\s*>\s*\*\*'   # blockquote whose first line starts with bold text

# ─── Helpers ────────────────────────────────────────────────────────────────────

$errors = [System.Collections.Generic.List[string]]::new()

function Add-Error { param([string]$File, [string]$Msg) $errors.Add("$File`: $Msg") }

# Blank out fenced code blocks (preserving line count so reported line numbers
# stay aligned with the source) so structural checks ignore code. Marketing
# scanning intentionally uses the raw content instead.
function Remove-CodeFences {
    param([string]$Content)
    return [regex]::Replace($Content, '(?ms)```.*?```', {
        param($m)
        # Keep one blank line per source line the block spanned.
        "`n" * ([regex]::Matches($m.Value, "`n").Count)
    })
}

# ─── Validate ───────────────────────────────────────────────────────────────────

$docFiles = Get-ChildItem $DocsRoot -Recurse -File -Filter *.md
$checked = 0

foreach ($file in $docFiles) {
    $relPath = [System.IO.Path]::GetRelativePath($DisplayBase, $file.FullName) -replace '\\', '/'
    $raw = Get-Content $file.FullName -Raw

    # Opt-in marker (match the first-10-lines window port-mslearn-docs.ps1 uses)
    $head = ($raw -split "`n" | Select-Object -First 10) -join "`n"
    if ($head -notmatch '<!--\s*mslearn:\s*true\s*-->') { continue }
    $checked++

    # Title = first H1 (shared resolver)
    $title = Get-MsLearnTitle $raw
    if (-not $title) {
        Add-Error $relPath "no H1 title found (required to derive front matter)."
        continue
    }

    # Resolve description exactly like Get-FrontMatter: marker override, else title.
    $resolved = Resolve-MsLearnDescription -Content $raw -Title $title
    $description = $resolved.Description
    $hasMarker = $resolved.HasMarker

    if (-not $hasMarker -or $description -eq $title) {
        Add-Error $relPath "description is missing or duplicates the title; add a <!-- description: ... --> marker distinct from the H1."
    }
    else {
        $len = $description.Length
        if ($len -lt $DescriptionMin -or $len -gt $DescriptionMax) {
            Add-Error $relPath "description length $len is outside $DescriptionMin-$DescriptionMax characters."
        }
        if (Test-MsLearnYamlUnsafe $description) {
            Add-Error $relPath "description would be emitted as a quoted YAML value (contains a special character or a leading/trailing indicator); reword so it is a plain scalar."
        }
    }

    # Banned marketing words (whole-file, case-insensitive, word-boundary).
    # Report the offending line number(s) to speed up CI fixes.
    $rawLines = $raw -split "`r?`n"
    foreach ($word in $BannedWords) {
        $wordRe = "(?i)\b$([regex]::Escape($word))\b"
        $hitLines = for ($li = 0; $li -lt $rawLines.Count; $li++) {
            if ($rawLines[$li] -match $wordRe) { $li + 1 }
        }
        if ($hitLines) {
            Add-Error $relPath "contains banned marketing word '$word' on line(s) $($hitLines -join ', '); use neutral, instructional language."
        }
    }

    # Blockquote-as-callout. Only a block's opening line is checked: bold text
    # later in the block (a "> **Details**" paragraph inside an alert, or a bold
    # word that wraps to the start of a line) is not a callout lead-in.
    $body = Remove-CodeFences $raw
    $lines = $body -split "`r?`n"
    $inBlockquote = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $isQuote = $lines[$i] -match '^\s*>'
        if ($isQuote -and -not $inBlockquote) {
            if (($lines[$i] -match $CalloutPattern) -and ($lines[$i] -notmatch $AlertPattern)) {
                Add-Error $relPath "blockquote callout on line $($i + 1) does not use MS Learn alert syntax; put '> [!NOTE]', '> [!TIP]', '> [!IMPORTANT]', '> [!WARNING]', or '> [!CAUTION]' on the line above it."
            }
        }
        # Any non-blockquote line (including a blank line) ends the block.
        $inBlockquote = $isQuote
    }
}

# ─── Report ─────────────────────────────────────────────────────────────────────

Write-Host "`nValidated $checked MS Learn doc(s) under $DocsRoot" -ForegroundColor Cyan

if ($errors.Count -gt 0) {
    Write-Host "`nErrors ($($errors.Count)):" -ForegroundColor Red
    foreach ($e in $errors) { Write-Host "  [error] $e" -ForegroundColor Red }
    Write-Host "`nMS Learn doc validation FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "MS Learn doc validation passed." -ForegroundColor Green
exit 0
