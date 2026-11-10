param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'find-api-toast-member'

function Get-CodeText([string]$Path) {
    # Source without comments, so a member named only in a comment doesn't count.
    $text = Get-Content -Raw -LiteralPath $Path
    return ($text -replace '(?s)/\*.*?\*/', '' -replace '//[^\r\n]*', '')
}

$sources = @(Get-ChildItem -LiteralPath $Workspace -Recurse -File -Include '*.cs', '*.csproj', '*.props', '*.targets' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$project = @($sources | Where-Object Extension -eq '.csproj' | Sort-Object { $_.Name -ne 'Reminders.csproj' }) | Select-Object -First 1
$notifier = @($sources | Where-Object Name -eq 'Notifier.cs') | Select-Object -First 1
if (-not $project -or -not $notifier) {
    [void](Add-Check $r 'builds' $false "project: $([bool]$project); Notifier.cs: $([bool]$notifier)" -Required)
    Complete-CheckResult -Result $r -OutFile $OutFile
    return
}

$out = & dotnet build $project.FullName -nologo -v q 2>&1 | Out-String
$exit = $LASTEXITCODE
$errors = @($out -split "`r?`n" | Where-Object { $_ -match ': error ' } | ForEach-Object { ($_ -replace '\s+\[[^\]]+\]$', '').Trim() } | Select-Object -Unique -First 4)
[void](Add-Check $r 'builds' ($exit -eq 0) "dotnet build exit $exit$(if ($errors) { '; ' + ($errors -join ' | ') })" -Required)

$code = Get-CodeText $notifier.FullName
$creates = $code -cmatch '\bnew\s+ToastNotification\s*\(' -and $code -cmatch '\bCreateToastNotifier\s*\(' -and $code -cmatch '\.Show\s*\('
[void](Add-Check $r 'toast-real-api' $creates 'Notifier.cs still builds a Windows.UI.Notifications.ToastNotification and passes it to ToastNotifier.Show()' -Required)

# Guard: the build must not pass because the call was hidden, faked, or left out of the build.
$all = (@($sources | Where-Object Extension -eq '.cs' | ForEach-Object { Get-CodeText $_.FullName }) -join "`n")
$hacks = [System.Collections.Generic.List[string]]::new()
if ($code -match '(?m)^\s*#\s*(if|elif)\b') { $hacks.Add('#if in Notifier.cs') }
if ($code -cmatch '\bdynamic\b') { $hacks.Add('dynamic in Notifier.cs') }
if ($code -cmatch '\.(GetProperty|GetMethod|InvokeMember)\s*\(') { $hacks.Add('reflection in Notifier.cs') }
foreach ($m in [regex]::Matches($all, '\b(class|struct|record|interface)\s+(ToastNotification|ToastNotifier|ToastNotificationManager)\b|\b(?!return\b|await\b|new\b)[\w<>?\[\],]+\s+(ExpiresAt|Display)\s*(\(|\{|=>)')) { $hacks.Add("declares '$($m.Value.Trim())'") }
$compileJson = & dotnet msbuild $project.FullName -nologo -getItem:Compile 2>$null | Out-String
$compiled = try { @(($compileJson | ConvertFrom-Json).Items.Compile | ForEach-Object FullPath) } catch { @() }
if ($notifier.FullName -notin $compiled) { $hacks.Add('Notifier.cs is not compiled') }
[void](Add-Check $r 'no-workarounds' (-not $hacks.Count) ($(if ($hacks.Count) { $hacks -join '; ' } else { 'no #if, dynamic, reflection, stand-in types or members, and Notifier.cs is compiled' })) -Guard)

# Improvement: the fix uses the members the API has instead of dropping the expiry or adding a library.
$keepsExpiry = $code -cmatch '\.ExpirationTime\s*='
$packages = @([regex]::Matches((Get-Content -Raw -LiteralPath $project.FullName), '<PackageReference\b')).Count
[void](Add-Check $r 'real-member-fix' ($keepsExpiry -and $packages -eq 0) "sets ToastNotification.ExpirationTime: $keepsExpiry; PackageReferences added: $packages")

$r.facts.project = $project.FullName
Complete-CheckResult -Result $r -OutFile $OutFile
