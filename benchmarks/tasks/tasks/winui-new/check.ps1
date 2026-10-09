param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'winui-new'
$projects = @(Find-WorkspaceFile -Root $Workspace -Include '*.csproj' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$winui = @($projects | Where-Object {
        $text = Get-Content -Raw $_.FullName
        $props = Get-ChildItem -Path $Workspace -Recurse -Include 'Directory.Packages.props', 'Directory.Build.props' -File -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -Raw $_.FullName }
        $text -match '<UseWinUI>\s*true\s*</UseWinUI>' -and (($text + ($props -join '')) -match 'Microsoft\.WindowsAppSDK')
    }) | Select-Object -First 1
[void](Add-Check $r 'winui-project' ([bool]$winui) ($(if ($winui) { $winui.FullName } else { "projects: [$($projects.Name -join ', ')]" })) -Required)
if ($winui) {
    [void](Add-Check $r 'named-notetaker' ($winui.BaseName -eq 'NoteTaker') $winui.Name)
    Push-Location $winui.DirectoryName
    $out = & dotnet build $winui.FullName -nologo -v q 2>&1 | Out-String
    $code = $LASTEXITCODE
    Pop-Location
    $tail = ($out.Trim() -split "`r?`n" | Select-Object -Last 6) -join ' | '
    [void](Add-Check $r 'builds' ($code -eq 0) "dotnet build exit $code; $tail" -Required)
}
else { [void](Add-Check $r 'builds' $false 'no WinUI project to build' -Required) }
Complete-CheckResult -Result $r -OutFile $OutFile
