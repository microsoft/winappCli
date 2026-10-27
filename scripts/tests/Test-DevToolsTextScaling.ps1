#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\validate-devtools-text-scaling.ps1"
$root = Join-Path ([IO.Path]::GetTempPath()) "winapp-scaling-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$cases = @(
    @{ Source = '<Button Height="32" Content="Comment"/>'; Rule = 'fixed-content-height' }
    @{ Source = '<Border Height="16"><TextBlock Text="12"/></Border>'; Rule = 'fixed-content-height' }
    @{ Source = '<Button><Grid Height="28"><FontIcon IsTextScaleFactorEnabled="True"/></Grid></Button>'; Rule = 'fixed-content-height' }
    @{ Source = '<Button Width="28"><FontIcon IsTextScaleFactorEnabled="True"/></Button>'; Rule = 'fixed-icon-width' }
    @{ Source = '<TextBlock IsTextScaleFactorEnabled="False"/>'; Rule = 'disabled-text-scaling' }
    @{ Source = '<Button><FontIcon/></Button>'; Rule = 'command-icon-scaling' }
    @{ Source = '<Grid x:Name="Host"><Grid.RowDefinitions><RowDefinition Height="14"/></Grid.RowDefinitions><TextBlock Text="Height"/></Grid>'; Rule = 'fixed-text-row' }
    @{ Source = '<Grid RowDefinitions="14,*"><TextBlock Text="Height"/></Grid>'; Rule = 'fixed-text-row' }
    @{ Source = '<x:Double x:Key="ExpanderChevronGlyphSize">8</x:Double>'; Rule = 'chevron-size-override' }
    @{ Source = '<Style><Setter Property="Height" Value="32"/></Style>'; Rule = 'scaling-style-setter' }
    @{ Source = '<Style><Setter Property="IsTextScaleFactorEnabled" Value="False"/></Style>'; Rule = 'scaling-style-setter' }
    @{ Source = '<Style><Setter Property="MinHeight" Value="32"/></Style>'; Rule = '' }
    @{ Source = '<Button MinHeight="32" MinWidth="32"><FontIcon IsTextScaleFactorEnabled="True"/></Button>'; Rule = '' }
    @{ Source = '<Border Width="30" Height="30"/>'; Rule = '' }
    @{ Source = '<TextBox MaxHeight="120"/>'; Rule = '' }
    @{ Source = '<TextBlock FontSize="12" Text="Comment"/>'; Rule = '' }
    @{ Source = '<Grid><Grid.RowDefinitions><RowDefinition Height="Auto"/></Grid.RowDefinitions><TextBlock/></Grid>'; Rule = '' }
    @{ Source = '<Grid><Grid.RowDefinitions><RowDefinition Height="1"/><RowDefinition Height="Auto"/></Grid.RowDefinitions><Border Height="1"/><TextBlock Grid.Row="1"/></Grid>'; Rule = '' }
    @{ Source = '<!-- <Button Height="32"/> --><TextBlock/>'; Rule = '' }
    @{ Source = 'auto x = L"<Button Height=\"32\"" L" Content=\"Comment\"/>";'; Rule = 'fixed-content-height'; Cpp = $true }
    @{ Source = 'auto x = L"<TextBlock IsTextScaleFactorEnabled=\"False\"/>";'; Rule = 'disabled-text-scaling'; Cpp = $true }
    @{ Source = 'auto x = L"<x:Double x:Key=\"ExpanderChevronButtonSize\">18</x:Double>";'; Rule = 'chevron-size-override'; Cpp = $true }
    @{ Source = 'DevToolsPutWidth(editor, 64.0);'; Rule = 'constant-size-setter'; Cpp = $true }
    @{ Source = 'element->put_Height(32);'; Rule = 'constant-size-setter'; Cpp = $true }
    @{ Source = 'if (c == L''"'') {}' + "`n" + 'DevToolsPutHeight(editor, 32);' + "`n" + 'auto s = L"x";'; Rule = 'constant-size-setter'; Line = 2; Cpp = $true }
    @{ Source = 'if (c == ''"'') {}' + "`n" + 'auto x = L"<Button Height=\"32\"" L" Content=\"Comment\"/>";'; Rule = 'fixed-content-height'; Line = 2; Cpp = $true }
    @{ Source = 'auto quote = L''\''''; DevToolsPutHeight(editor, 32); auto s = L"x";'; Rule = 'constant-size-setter'; Cpp = $true }
    @{ Source = 'auto slash = ''\\''; auto x = L"<TextBlock IsTextScaleFactorEnabled=\"False\"/>";'; Rule = 'disabled-text-scaling'; Cpp = $true }
    @{ Source = 'auto number = 1''000; DevToolsPutHeight(editor, 32); auto quote = ''"'';'; Rule = 'constant-size-setter'; Cpp = $true }
    @{ Source = 'auto quote = u8''"''; DevToolsPutHeight(editor, 32); auto s = L"x";'; Rule = 'constant-size-setter'; Cpp = $true }
    @{ Source = 'auto quote = U''"''; auto x = L"<Button Height=\"32\"/>";'; Rule = 'fixed-content-height'; Cpp = $true }
    @{ Source = 'auto quote = u''\"''; DevToolsPutHeight(editor, 32); auto s = L"x";'; Rule = 'constant-size-setter'; Cpp = $true }
    @{ Source = 'auto quote = L''"''; auto s = L"DevToolsPutHeight(editor, 32);";'; Rule = ''; Cpp = $true }
    @{ Source = '// DevToolsPutHeight(editor, 32);'; Rule = ''; Cpp = $true }
    @{ Source = '// L"<TextBlock Height=\"12\"/>"'; Rule = ''; Cpp = $true }
    @{ Source = 'DevToolsPutHeight(canvas, surface.contentH);'; Rule = ''; Cpp = $true }
)
try {
    foreach ($case in $cases) {
        $extension = if ($case.Cpp) { 'inc' } else { 'xaml' }
        $path = Join-Path $root "fixture.$extension"
        Set-Content -LiteralPath $path -Value $case.Source
        $findings = @(Get-DevToolsScalingFindings $root)
        if ($case.Rule) {
            if ($case.Rule -notin $findings.Rule) { throw "Missed $($case.Rule): $($case.Source)" }
            if (@($findings | Where-Object Line -lt 1).Count) { throw 'Invalid source line.' }
            if ($case.Line -and @($findings | Where-Object { $_.Rule -eq $case.Rule -and $_.Line -eq $case.Line }).Count -ne 1) {
                throw "Incorrect source line for $($case.Rule): $($case.Source)"
            }
        } elseif ($findings.Count) {
            throw "False positive: $($case.Source): $($findings | ConvertTo-Json -Compress)"
        }
        Remove-Item -LiteralPath $path
    }
    $path = Join-Path $root 'fixture.xaml'
    Set-Content -LiteralPath $path -Value '<Button x:Name="OldButton" Height="32"/>'
    $exceptions = Join-Path $root 'exceptions.json'
    Set-Content -LiteralPath $exceptions -Value '[{"file":"fixture.xaml","rule":"fixed-content-height","target":"OldButton","count":1,"reason":"Test fixture"}]'
    Invoke-DevToolsScalingCheck $root $exceptions
    Add-Content -LiteralPath $path -Value '<Button x:Name="NewButton" Height="32"/>'
    $rejected = $false
    try { Invoke-DevToolsScalingCheck $root $exceptions } catch { $rejected = $true }
    if (-not $rejected) { throw 'An existing exception suppressed a new control.' }
    Set-Content -LiteralPath $path -Value '<Button x:Name="OldButton" MinHeight="32"/>'
    $rejected = $false
    try { Invoke-DevToolsScalingCheck $root $exceptions } catch { $rejected = $_.Exception.Message -like 'Stale scaling exception:*' }
    if (-not $rejected) { throw 'An unused exception did not fail.' }
    Set-Content -LiteralPath $path -Value '<Button'
    $rejected = $false
    try { Get-DevToolsScalingFindings $root } catch { $rejected = $true }
    if (-not $rejected) { throw 'Malformed XAML did not fail.' }
    Write-Host "Passed $($cases.Count) styling cases and 4 validation/exception checks."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
