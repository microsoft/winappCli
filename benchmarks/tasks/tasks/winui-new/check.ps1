param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'winui-new'
$projects = @(Find-WorkspaceFile -Root $Workspace -Include '*.csproj' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

function Get-EvaluatedWinUI([string]$Project) {
    # The evaluated project, not its text: UseWinUI and a real Microsoft.WindowsAppSDK PackageReference
    # (including ones from Directory.*.props and central package management).
    $json = & dotnet msbuild $Project -nologo -getProperty:UseWinUI -getItem:PackageReference 2>$null | Out-String
    try { $e = $json | ConvertFrom-Json } catch { return [pscustomobject]@{ UseWinUI = $false; HasWasdk = $false; Note = "evaluation failed: $($json.Trim())" } }
    $refs = @($e.Items.PackageReference | ForEach-Object Identity)
    return [pscustomobject]@{
        UseWinUI = [string]$e.Properties.UseWinUI -eq 'true'
        HasWasdk = 'Microsoft.WindowsAppSDK' -in $refs
        Note     = "UseWinUI=$($e.Properties.UseWinUI); PackageReference=[$($refs -join ', ')]"
    }
}

$evaluated = foreach ($p in $projects) { $ev = Get-EvaluatedWinUI $p.FullName; [pscustomobject]@{ File = $p; Eval = $ev } }
$winui = @($evaluated | Where-Object { $_.Eval.UseWinUI -and $_.Eval.HasWasdk }) | Select-Object -First 1
$detail = if ($winui) { "$($winui.File.FullName): $($winui.Eval.Note)" } elseif ($evaluated) { (@($evaluated | ForEach-Object { "$($_.File.Name): $($_.Eval.Note)" }) -join '; ') } else { 'no .csproj' }
[void](Add-Check $r 'winui-project' ([bool]$winui) $detail -Required)
if ($winui) {
    $winui = $winui.File
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
