#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Shared MS Learn doc-metadata helpers.
.DESCRIPTION
    Single source of truth for the front-matter rules that both
    port-mslearn-docs.ps1 (the generator) and validate-mslearn-docs.ps1 (the
    gate) depend on: title extraction, description resolution, ms.topic
    resolution, and — most importantly — the YAML-quoting rule. Previously each
    script carried its own copy of these, and the file headers literally said
    "keep in sync"; centralizing them here guarantees the gate can never approve
    a value the generator would silently quote (or vice versa).

    Dot-source this file:  . (Join-Path $PSScriptRoot 'mslearn-doc-lib.ps1')
#>

# Characters that make a plain YAML scalar unsafe *anywhere* in the value.
$script:MsLearnYamlSpecialAnywhere = '[:\[\]{}#&*!|>''"%@`]'

function Test-MsLearnYamlUnsafe {
    <#
        Returns $true when $Value could not be emitted as a bare YAML scalar and
        would have to be quoted. Covers both characters that are special anywhere
        in a plain scalar and the indicators that are only special at the very
        start (or end) of one — e.g. a description beginning with "- " or "? "
        would otherwise be emitted as `description: - ...` and parsed by YAML as
        collection syntax instead of a string.
    #>
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $false }
    # Special anywhere in the scalar.
    if ($Value -match $script:MsLearnYamlSpecialAnywhere) { return $true }
    # Block indicators special only when they *begin* a plain scalar: "- " / "? "
    # open a sequence entry / complex key (a bare "-" or "?" behaves the same).
    if ($Value -match '^[-?](\s|$)') { return $true }
    # Leading '~' is the YAML null indicator; a leading ',' is the flow separator;
    # leading or trailing whitespace also forces quoting.
    if ($Value -match '^[\s~,]' -or $Value -match '\s$') { return $true }
    return $false
}

function Format-MsLearnYamlValue {
    # Quote $Value if (and only if) it could not be emitted as a bare scalar.
    # When quoting, escape backslashes *before* double quotes so values like a
    # Windows path (C:\Windows) don't produce an invalid double-quoted escape.
    param([string]$Value)
    if (Test-MsLearnYamlUnsafe $Value) {
        $escaped = $Value -replace '\\', '\\' -replace '"', '\"'
        return '"' + $escaped + '"'
    }
    return $Value
}

function Get-MsLearnTitle {
    # First H1 heading, or $null when the doc has none.
    param([string]$Content)
    if ($Content -match '(?m)^#\s+(.+?)\s*$') { return $Matches[1].Trim() }
    return $null
}

function Resolve-MsLearnDescription {
    # An explicit <!-- description: ... --> marker wins; otherwise the
    # description defaults to the title (matching the generator's behaviour).
    param([string]$Content, [string]$Title)
    if ($Content -match '<!--\s*description:\s*(.+?)\s*-->') {
        return [pscustomobject]@{ Description = $Matches[1].Trim(); HasMarker = $true }
    }
    return [pscustomobject]@{ Description = $Title; HasMarker = $false }
}

function Get-MsLearnTopic {
    # <!-- ms.topic: ... --> override, else the supplied default.
    param([string]$Content, [string]$Default = 'how-to')
    if ($Content -match '<!--\s*ms\.topic:\s*(.+?)\s*-->') { return $Matches[1].Trim() }
    return $Default
}

# Matches one Markdown inline code span. A span opens with a run of N backticks
# and closes with the next run of exactly N, so a span that must contain a
# literal backtick is written with a longer run: `` IAsyncOperation`1 ``.
# Matching only single-backtick spans pairs that opening run's two backticks
# with unrelated backticks further on, and every backtick after it then pairs
# one position out — which silently swallows whole paragraphs, and any links
# inside them, into a "code span" the link rewriter never sees.
$script:MsLearnInlineCodePattern = '(?<!`)(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)'

function Get-MsLearnInlineCodePattern {
    # The inline-code span pattern, exposed so the generator and its tests share one rule.
    return $script:MsLearnInlineCodePattern
}
