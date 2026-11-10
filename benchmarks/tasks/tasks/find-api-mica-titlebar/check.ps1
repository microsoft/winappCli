param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'find-api-mica-titlebar'

function Get-CodeText([System.IO.FileInfo]$File) {
    # Source without comments, so a type named only in a comment doesn't count.
    $text = Get-Content -Raw -LiteralPath $File.FullName
    if ($File.Extension -eq '.xaml') { return ($text -replace '(?s)<!--.*?-->', '') }
    return ($text -replace '(?s)/\*.*?\*/', '' -replace '//[^\r\n]*', '')
}

$files = @(Get-ChildItem -LiteralPath $Workspace -Recurse -File -Include '*.cs', '*.xaml', '*.csproj' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$project = @($files | Where-Object Extension -eq '.csproj' | Sort-Object { $_.Name -ne 'PhotoNotes.csproj' }) | Select-Object -First 1
if (-not $project) {
    [void](Add-Check $r 'builds' $false 'no .csproj' -Required)
    Complete-CheckResult -Result $r -OutFile $OutFile
    return
}

# One build with IDE0005 (unnecessary using) reported, so the improvement check needs no second build.
# The SDK reads a .globalconfig next to the project; the checker adds one only if the agent didn't.
$globalConfig = Join-Path $project.DirectoryName '.globalconfig'
$ownConfig = -not (Test-Path -LiteralPath $globalConfig)
if ($ownConfig) { Set-Content -LiteralPath $globalConfig -Encoding utf8NoBOM -Value "is_global = true`ndotnet_diagnostic.IDE0005.severity = warning`ndotnet_diagnostic.CS1591.severity = none" }
try {
    $out = & dotnet build $project.FullName -nologo -v q --no-incremental -p:EnforceCodeStyleInBuild=true -p:GenerateDocumentationFile=true 2>&1 | Out-String
    $exit = $LASTEXITCODE
}
finally { if ($ownConfig) { Remove-Item -LiteralPath $globalConfig -ErrorAction SilentlyContinue } }
$lines = @($out -split "`r?`n" | ForEach-Object { ($_ -replace '\s+\[[^\]]+\]$', '').Trim() } | Select-Object -Unique)
$errors = @($lines | Where-Object { $_ -match ': error ' } | Select-Object -First 4)
# The fixture already builds, so building is a guard: it must still hold but isn't progress.
[void](Add-Check $r 'builds' ($exit -eq 0) "dotnet build exit $exit$(if ($errors) { '; ' + ($errors -join ' | ') })" -Guard)

$code = (@($files | Where-Object Extension -in '.cs', '.xaml' | ForEach-Object { Get-CodeText $_ }) -join "`n")
$cs = (@($files | Where-Object Extension -eq '.cs' | ForEach-Object { Get-CodeText $_ }) -join "`n")

$micaBackdrop = $code -cmatch '\bMicaBackdrop\b' -and $code -cmatch '\bSystemBackdrop\b'
$micaController = $cs -cmatch '\bMicaController\b' -and $cs -cmatch '\bAddSystemBackdropTarget\s*\('
[void](Add-Check $r 'mica-backdrop' ($micaBackdrop -or $micaController) "MicaBackdrop set as SystemBackdrop: $micaBackdrop; MicaController with a backdrop target: $micaController" -Required)

$titleCs = $cs -cmatch '\bExtendsContentIntoTitleBar\s*=\s*true\b'
$titleXaml = $code -match 'ExtendsContentIntoTitleBar\s*=\s*"true"'
[void](Add-Check $r 'content-in-title-bar' ($titleCs -or $titleXaml) "ExtendsContentIntoTitleBar set to true (Window or AppWindow.TitleBar): $($titleCs -or $titleXaml)" -Required)

# Guard: no hand-written type or member that stands in for the real API and makes the code compile.
$stubs = @([regex]::Matches($cs, '\b(class|struct|record|interface)\s+(MicaBackdrop|MicaController)\b|\b(?!return\b|await\b|new\b|override\b)[\w<>?\[\],]+\s+(ExtendsContentIntoTitleBar|SystemBackdrop)\s*(\{|=>)') | ForEach-Object { $_.Value.Trim() })
[void](Add-Check $r 'no-stand-ins' (-not $stubs.Count) ($(if ($stubs.Count) { "declares: $($stubs -join ', ')" } else { 'no workspace type or member imitates the backdrop or title bar API' })) -Guard)

# Improvement: no unnecessary using directives (the fixture has none).
$unused = @($lines | Where-Object { $_ -match 'warning IDE0005' } | ForEach-Object { ($_ -split ': warning')[0] -replace [regex]::Escape("$Workspace\"), '' })
[void](Add-Check $r 'no-unused-usings' (($exit -eq 0) -and -not $unused.Count) ($(if ($exit -ne 0) { 'not checked: the build failed' } elseif ($unused.Count) { "IDE0005 at $($unused -join ', ')" } else { 'no IDE0005' })))

$r.facts.project = $project.FullName
Complete-CheckResult -Result $r -OutFile $OutFile
