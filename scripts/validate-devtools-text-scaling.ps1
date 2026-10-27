#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot '..\src\winapp-devtools\native\WinApp.DevTools.Native'),
    [string]$ExceptionsPath = (Join-Path $PSScriptRoot 'devtools-text-scaling-exceptions.json')
)
$ErrorActionPreference = 'Stop'

function Get-DevToolsScalingFindings {
    param([string]$Root)
    $Root = (Resolve-Path -LiteralPath $Root).Path
    $findings = [Collections.Generic.List[object]]::new()
    function Add-Finding($File, $Line, $Rule, $Target, $Message) {
        $findings.Add([pscustomobject]@{
            File = [IO.Path]::GetRelativePath($Root, $File).Replace('\', '/')
            Line = $Line; Rule = $Rule; Target = $Target; Message = $Message
        })
    }
    function Test-Element($Element, $File, $Line, [bool]$HasTree) {
        $kind = $Element.Name.LocalName
        $id = $Element.Attributes() | Where-Object { $_.Name.LocalName -eq 'Name' } | Select-Object -First 1
        $attributes = @($Element.Attributes() | Where-Object { -not $_.IsNamespaceDeclaration } |
            Sort-Object { $_.Name.ToString() } | ForEach-Object { "$($_.Name.LocalName)=$($_.Value)" })
        $target = if ($id) { $id.Value } elseif ($attributes.Count) { "$kind[$($attributes -join ';')]" } else { $kind }
        if ($kind -eq 'Setter') {
            $property = $Element.Attribute('Property')?.Value
            $value = $Element.Attribute('Value')?.Value
            if (($property -in @('Height', 'Width') -and $value -match '^\d+(\.\d+)?$') -or
                ($property -eq 'IsTextScaleFactorEnabled' -and $value -eq 'False')) {
                Add-Finding $File $Line 'scaling-style-setter' "$target/$property" 'Review the style constraint for text scaling or document its non-text purpose.'
            }
        }
        $textKinds = @('TextBlock', 'TextBox', 'RichEditBox', 'Button', 'ToggleButton', 'CheckBox', 'RadioButton', 'ComboBox', 'FontIcon')
        $descendants = if ($HasTree) { @($Element.Descendants()) } else { @() }
        $hasText = $kind -in $textKinds -or @($descendants | Where-Object { $_.Name.LocalName -in $textKinds }).Count -gt 0
        $height = $Element.Attribute('Height')
        if ($height -and $height.Value -ne 'Auto' -and $hasText) {
            Add-Finding $File $Line 'fixed-content-height' $target 'Use MinHeight instead of a fixed height around text or icons, or document the exception.'
        }
        if ($Element.Attribute('Width') -and ($kind -eq 'FontIcon' -or
            @($descendants | Where-Object { $_.Name.LocalName -eq 'FontIcon' }).Count -gt 0)) {
            Add-Finding $File $Line 'fixed-icon-width' $target 'Use MinWidth instead of a fixed width around scaling icons.'
        }
        if ($Element.Attribute('IsTextScaleFactorEnabled')?.Value -eq 'False') {
            Add-Finding $File $Line 'disabled-text-scaling' $target 'Text scaling is disabled. Remove the override or document why this is decorative.'
        }
        if ($HasTree -and $kind -eq 'FontIcon' -and
            @($Element.Ancestors() | Where-Object { $_.Name.LocalName -in @('Button', 'ToggleButton') }).Count -gt 0 -and
            $Element.Attribute('IsTextScaleFactorEnabled')?.Value -ne 'True') {
            Add-Finding $File $Line 'command-icon-scaling' $target 'Set IsTextScaleFactorEnabled="True" on command icons.'
        }
        $key = $Element.Attributes() | Where-Object { $_.Name.LocalName -eq 'Key' } | Select-Object -First 1
        if ($key -and $key.Value -in @('ExpanderChevronButtonSize', 'ExpanderChevronGlyphSize')) {
            Add-Finding $File $Line 'chevron-size-override' $key.Value 'Keep the platform expander chevron sizes.'
        }
        if (-not $HasTree -or $kind -ne 'Grid') { return }
        $rows = @($Element.Elements() | Where-Object { $_.Name.LocalName -eq 'Grid.RowDefinitions' } |
            ForEach-Object { $_.Elements() })
        $heights = @($rows | ForEach-Object { $_.Attribute('Height')?.Value })
        if ($Element.Attribute('RowDefinitions')) {
            $heights = @($Element.Attribute('RowDefinitions').Value.Split(',') | ForEach-Object { $_.Trim() })
        }
        for ($index = 0; $index -lt $heights.Count; $index++) {
            if ($heights[$index] -notmatch '^\d+(\.\d+)?$') { continue }
            $occupants = @($Element.Elements() | Where-Object {
                $row = if ($_.Attribute('Grid.Row')) { $_.Attribute('Grid.Row').Value } else { '0' }
                $_.Name.LocalName -notlike '*.*' -and $row -match '^\d+$' -and [int]$row -eq $index
            })
            if (@($occupants | Where-Object {
                $_.Name.LocalName -in $textKinds -or
                @($_.Descendants() | Where-Object { $_.Name.LocalName -in $textKinds }).Count -gt 0
            }).Count -gt 0) {
                $rowLine = if ($Element.Attribute('RowDefinitions')) { $Line } else { $rows[$index].LineNumber - 1 }
                Add-Finding $File $rowLine 'fixed-text-row' "$target/row[$index]" 'Use an Auto row for text-bearing content.'
            }
        }
    }
    $files = Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        $_.Extension -in @('.xaml', '.cpp', '.inc') -and $_.FullName -notmatch '[\\/]generated[\\/]'
    }
    if (-not $files) { throw "No DevTools XAML or C++ source found under $Root." }
    foreach ($file in $files) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($file.Extension -eq '.xaml') {
            $markup = [regex]::Replace($text, '(?s)<!--.*?-->', { param($match) [regex]::Replace($match.Value, '[^\r\n]', ' ') })
            # Templates contain multiple roots and runtime placeholders, but are valid XML inside this wrapper.
            $wrapped = '<ScalingCheck xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + "`n$markup`n</ScalingCheck>"
            $document = [Xml.Linq.XDocument]::Parse($wrapped, [Xml.Linq.LoadOptions]::SetLineInfo)
            foreach ($element in $document.Root.Descendants()) {
                Test-Element $element $file.FullName ($element.LineNumber - 1) $true
            }
        } else {
            # A digit separator must not start a character literal.
            $commentsAndCharacters = '(?s)//[^\r\n]*|/\*.*?\*/|(?<![\w''])(?:u8|[LuU])?''(?:\\.|[^''\\\r\n])+'''
            # Only adjacent C++ string literals are reconstructed. Runtime expressions are not evaluated.
            $tokens = [regex]::Matches($text, $commentsAndCharacters + '|(?:L?"(?:\\.|[^"\\])*"\s*)+')
            $literals = @($tokens | Where-Object { $_.Value -match '^(L)?"' })
            foreach ($literal in $literals) {
                $decoded = ([regex]::Matches($literal.Value, 'L?"((?:\\.|[^"\\])*)"') |
                    ForEach-Object { $_.Groups[1].Value.Replace('\"', '"').Replace('\\', '\') }) -join ''
                foreach ($tag in [regex]::Matches($decoded, '<(?:Grid|Border|TextBlock|TextBox|RichEditBox|FontIcon|Button|ToggleButton|CheckBox|RadioButton|ComboBox|Setter|x:Double)\b(?:\s+[\w.:]+\s*=\s*"[^"]*")*\s*/?>')) {
                    $xml = $tag.Value.TrimEnd('>', '/', ' ') + ' />'
                    try {
                        $document = [Xml.Linq.XDocument]::Parse('<ScalingCheck xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + $xml + '</ScalingCheck>')
                    } catch {
                        throw "Cannot parse literal tag in $($file.FullName): $xml. $($_.Exception.Message)"
                    }
                    $line = 1 + [regex]::Matches($text.Substring(0, $literal.Index), '\n').Count
                    Test-Element @($document.Root.Elements())[0] $file.FullName $line $false
                }
            }
            $code = [regex]::Replace($text, $commentsAndCharacters + '|L?"(?:\\.|[^"\\])*"', { param($match) [regex]::Replace($match.Value, '[^\r\n]', ' ') })
            foreach ($setter in [regex]::Matches($code, '(?:DevToolsPut(?:Height|Width)\([^,\r\n]+,\s*\d+(?:\.\d+)?\s*\)|->put_(?:Height|Width)\(\s*\d+(?:\.\d+)?\s*\))')) {
                $line = 1 + [regex]::Matches($text.Substring(0, $setter.Index), '\n').Count
                Add-Finding $file.FullName $line 'constant-size-setter' $setter.Value 'Review this constant runtime size for text clipping; use a minimum or document its purpose.'
            }
        }
    }
    return $findings
}

function Invoke-DevToolsScalingCheck {
    param([string]$Root, [string]$Exceptions)
    $findings = @(Get-DevToolsScalingFindings $Root)
    $entries = @(Get-Content -LiteralPath $Exceptions -Raw | ConvertFrom-Json)
    foreach ($entry in $entries) {
        if (-not $entry.reason -or $entry.count -lt 1) { throw 'Each scaling exception needs a reason and a positive match count.' }
        $matches = @($findings | Where-Object { $_.File -ceq $entry.file -and $_.Rule -ceq $entry.rule -and $_.Target -ceq $entry.target })
        if ($matches.Count -ne $entry.count) { throw "Stale scaling exception: $($entry.file) [$($entry.rule)] $($entry.target). Expected $($entry.count), found $($matches.Count)." }
        $findings = @($findings | Where-Object { $_ -notin $matches })
    }
    foreach ($finding in $findings) {
        Write-Host "$($finding.File):$($finding.Line) [$($finding.Rule)] $($finding.Target): $($finding.Message)"
    }
    if ($findings.Count) { throw "DevTools text scaling: $($findings.Count) source findings." }
    Write-Host 'DevTools text scaling source check passed.'
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-DevToolsScalingCheck $SourceRoot $ExceptionsPath
}
