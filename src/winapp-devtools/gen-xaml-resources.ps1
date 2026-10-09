# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Generate a C++ header that embeds the overlay's .xaml resource fragments as wide string literals.

.DESCRIPTION
  The DevTools overlay (DevToolsOverlay.cpp) builds its UI by handing XAML fragments to the target app's own
  Microsoft.UI.Xaml.Markup.XamlReader (the version-independent "runtime XAML" method). To keep that markup
  diffable, reviewable and validatable instead of scattered across C++ string-concatenation, the fragments
  live as real .xaml files under native/WinApp.DevTools.Native/xaml/*.xaml and are compiled in via this generated header.

  Each <name>.xaml becomes `DevToolsXaml::<Name>` — an `inline constexpr const wchar_t*` raw wide string literal.
  Dynamic values are authored as `$TOKEN$` placeholders and substituted at load time (DevToolsXamlSubst), so the
  .xaml stays a static, reviewable file. This is generated build output; it is .gitignored and regenerated
  by build-devtools.ps1 on every build. Do not edit the header by hand or check it in.

.PARAMETER XamlDir
  Directory of .xaml fragments. Defaults to native/WinApp.DevTools.Native/xaml next to this script.

.PARAMETER OutFile
  Output header path. Defaults to native/WinApp.DevTools.Native/DevToolsOverlayXaml.g.h.

.PARAMETER SelfTest
  Prove the well-formedness gate can still go red (and does not go red on the shapes the real fragments
  use), then exit without generating anything. Run by build-devtools.ps1 on every build.
#>
[CmdletBinding()]
param(
    [string]$XamlDir = (Join-Path $PSScriptRoot 'native\WinApp.DevTools.Native\xaml'),
    [string]$OutFile = (Join-Path $PSScriptRoot 'native\WinApp.DevTools.Native\DevToolsOverlayXaml.g.h'),
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

if (-not $SelfTest -and -not (Test-Path $XamlDir)) { throw "XAML resource directory not found: $XamlDir" }

# Raw-string delimiter. A build must fail loudly (not silently corrupt the literal) if a fragment ever
# contains this sentinel, so assert it never appears in any source file.
$delim = 'DEVTOOLS_XAML'
if ($delim.Length -gt 16) { throw 'C++ raw-string delimiters cannot exceed 16 characters.' }

# Reject malformed XML at build time rather than losing a panel when XamlReader loads it in the app.
#
# Two shapes must both pass. Standalone fragments declare their own xmlns; selection-panel.xaml is
# substituted into selection.xaml's $PANEL$ slot and is deliberately TWO roots with no xmlns at all.
# Wrapping every fragment in a synthetic root that declares the standard XAML namespaces covers both.
#
# Validate what the READER will see, not the file: comments are stripped before the markup reaches
# XamlReader and legitimately contain sequences (a bare `--`) that XML forbids inside a comment. Each
# comment collapses to its own newline count, and the wrapper's open tag carries no trailing newline, so
# reported line numbers address the source file.
#
# This checks well-formedness only; type and property availability require the target app.
function Assert-WellFormedXaml([string]$content, [string]$fileName) {
    $body = [regex]::Replace($content, '(?s)<!--.*?-->', {
        param($m) "`n" * ([regex]::Matches($m.Value, "`n").Count)
    })
    $wrapped = '<DevToolsFragmentValidationRoot' +
               ' xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"' +
               ' xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' +
               $body + "`n</DevToolsFragmentValidationRoot>"
    $doc = New-Object System.Xml.XmlDocument
    try {
        $doc.LoadXml($wrapped)
    } catch [System.Xml.XmlException] {
        throw ("XAML fragment $fileName is not well-formed: $($_.Exception.Message)`n" +
               "  (line/position are relative to $fileName itself)")
    }
}

function ConvertTo-Identifier([string]$baseName) {
    # pick-catcher -> PickCatcher ; adorner -> Adorner
    ($baseName -split '[-_.]' | Where-Object { $_ } | ForEach-Object {
        $_.Substring(0,1).ToUpperInvariant() + $_.Substring(1)
    }) -join ''
}

if ($SelfTest) {
    # Cover supported fragment shapes and a mismatched closing-tag negative control.
    $accept = @{
        'two roots, no xmlns of its own (the selection-panel.xaml shape)' =
            "<Border x:Name=`"A`" />`n<Popup x:Name=`"B`"><Grid /></Popup>"
        'a comment containing a bare -- (stripped before the reader sees it)' =
            "<!-- prose -- with a double dash -->`n<Border />"
        '$TOKEN$ placeholders in attributes and text' =
            '<Border Width="$W$"><TextBlock Text="$TITLE$" /></Border>'
    }
    foreach ($case in $accept.GetEnumerator()) {
        Assert-WellFormedXaml $case.Value 'selftest.xaml'   # throws -> the build fails, which is the point
    }

    $rejected = "<Popup>`n  <Border>`n    <Grid />`n  </Popup>`n</Border>"
    $caught = $null
    try { Assert-WellFormedXaml $rejected 'selftest.xaml' } catch { $caught = $_.Exception.Message }
    if (-not $caught) {
        throw 'Well-formedness gate did NOT reject a </Popup> closing over an open Border.'
    }
    if ($caught -notmatch 'not well-formed') {
        throw "Well-formedness gate rejected mismatched closing tags with an unexpected message: $caught"
    }
    Write-Host "    XAML gate self-test passed ($($accept.Count) accepted, 1 rejected; C++ delimiter $($delim.Length)/16 characters)." -ForegroundColor Green
    exit 0
}

$files = @(Get-ChildItem -Path $XamlDir -Filter '*.xaml' | Sort-Object Name)
if (-not $files) { throw "No .xaml fragments found in $XamlDir" }

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('// <auto-generated>')
[void]$sb.AppendLine('// Generated from native/WinApp.DevTools.Native/xaml/*.xaml by gen-xaml-resources.ps1 (run by build-devtools.ps1).')
[void]$sb.AppendLine('// DO NOT EDIT and DO NOT check in — edit the .xaml source files instead. This is build output.')
[void]$sb.AppendLine('// </auto-generated>')
[void]$sb.AppendLine('#pragma once')
[void]$sb.AppendLine('namespace DevToolsXaml {')
foreach ($f in $files) {
    $id = ConvertTo-Identifier $f.BaseName
    # Read as text; normalize newlines to \n so the literal is stable across checkouts (autocrlf).
    $content = (Get-Content -Raw -LiteralPath $f.FullName) -replace "`r`n", "`n"
    # Validate before embedding markup for the target's reader.
    Assert-WellFormedXaml $content $f.Name
    # Strip XML comments and trim: Microsoft.UI.Xaml.Markup.XamlReader.Load requires the markup to START at
    # the root element — a leading <!-- comment --> (or leading whitespace) makes it fail/AV. Comments in the
    # .xaml are author documentation only and must not reach the reader. This also keeps the substituter from
    # rewriting $TOKEN$-looking text inside comments and shrinks the embedded literal.
    $content = ([regex]::Replace($content, '(?s)<!--.*?-->', '')).Trim()
    if ($content.Contains(")$delim`"")) {
        throw "Fragment $($f.Name) contains the raw-string delimiter sentinel ')$delim`"'; choose a different delimiter."
    }
    [void]$sb.AppendLine("// ---- $($f.Name) ----")
    [void]$sb.AppendLine("inline constexpr const wchar_t* $id = LR`"$delim($content)$delim`";")
}
[void]$sb.AppendLine('} // namespace DevToolsXaml')

$text = $sb.ToString()
# Only rewrite when changed, so an unchanged build doesn't churn timestamps / trigger needless recompiles.
$existing = if (Test-Path $OutFile) { Get-Content -Raw -LiteralPath $OutFile } else { $null }
if ($existing -ne $text) {
    Set-Content -LiteralPath $OutFile -Value $text -NoNewline -Encoding UTF8
    Write-Host "    generated: $OutFile ($($files.Count) fragment(s))" -ForegroundColor Green
} else {
    Write-Host "    up-to-date: $OutFile ($($files.Count) fragment(s))" -ForegroundColor DarkGray
}
