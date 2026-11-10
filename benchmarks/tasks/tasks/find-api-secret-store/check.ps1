param([string]$Workspace, $Baseline, [string]$OutFile, [string]$StatePath = 'C:\bench-state\find-api-secret-store.json')
$r = New-CheckResult -Task 'find-api-secret-store'
$s = Read-TaskState $StatePath

$sources = @(Get-ChildItem -LiteralPath $Workspace -Recurse -File -Include '*.cs', '*.csproj', '*.props', '*.targets', '*.json' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$project = @($sources | Where-Object Extension -eq '.csproj' | Sort-Object { $_.Name -ne 'ReportExporter.csproj' }) | Select-Object -First 1
$database = @($sources | Where-Object Name -eq 'Database.cs') | Select-Object -First 1
if (-not $project -or -not $database) {
    [void](Add-Check $r 'builds' $false "project: $([bool]$project); Database.cs: $([bool]$database)" -Required)
    Complete-CheckResult -Result $r -OutFile $OutFile
    return
}

$build = Invoke-DotnetBuild $project.FullName
# The fixture already builds, so building is a guard: it must still hold but isn't progress.
[void](Add-Check $r 'builds' $build.Ok $build.Detail -Guard)

# A probe app calls Database.ConnectionString() (sync or async) and prints the result. It lives under
# obj\ so the workspace's nuget.config applies and the workspace project doesn't compile it.
$connection = $null
$probeDetail = 'not run: the build failed'
if ($build.Ok) {
    $probeDir = Join-Path $project.DirectoryName 'obj\bench-probe'
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
    try {
        Set-Content -LiteralPath (Join-Path $probeDir 'BenchProbe.csproj') -Encoding utf8NoBOM -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable><NoWarn>CS0184;CS8600</NoWarn></PropertyGroup>
  <ItemGroup><ProjectReference Include="$([System.Security.SecurityElement]::Escape($project.FullName))" /></ItemGroup>
</Project>
"@
        Set-Content -LiteralPath (Join-Path $probeDir 'Probe.cs') -Encoding utf8NoBOM -Value @'
object result = ReportExporter.Database.ConnectionString();
if (result is System.Threading.Tasks.Task<string> pending) result = await pending;
System.Console.WriteLine("BENCH-CS=" + result);
'@
        $probeBuild = Invoke-DotnetBuild (Join-Path $probeDir 'BenchProbe.csproj')
        if ($probeBuild.Ok) {
            $out = & dotnet run --project (Join-Path $probeDir 'BenchProbe.csproj') --no-build 2>&1 | Out-String
            $line = @($out -split "`r?`n" | Where-Object { $_ -like 'BENCH-CS=*' }) | Select-Object -First 1
            $connection = if ($line) { $line.Substring(9) } else { $null }
            $probeDetail = if ($line) { 'ConnectionString() returned a value' } else { "ConnectionString() threw: $(($out -split "`r?`n" | Where-Object { $_ -match 'Exception' } | Select-Object -First 1))" }
        }
        else { $probeDetail = "probe calling ReportExporter.Database.ConnectionString() did not build: $($probeBuild.Detail)" }
    }
    finally { Remove-Item -LiteralPath $probeDir -Recurse -Force -ErrorAction SilentlyContinue }
}
$hasSecret = $connection -and $connection.Contains("Password=$($s.secret);")
$masked = $connection -and $connection -match 'Password=\*+;'
[void](Add-Check $r 'password-from-store' ([bool]$hasSecret) "Database.ConnectionString() contains the reports-db secret from the store: $hasSecret ($probeDetail$(if ($masked) { '; the password is the masked ToString() placeholder' }))" -Required)

# Guard: the stored secret was not replaced (rotating it would break every other service using it).
$current = $null
try {
    $store = Get-Content -Raw -LiteralPath $s.storeFile | ConvertFrom-Json
    $key = [Convert]::FromBase64String($s.key)
    $enc = [Convert]::FromBase64String($store.'reports-db'.Data)
    $dec = [byte[]]::new($enc.Length)
    for ($i = 0; $i -lt $enc.Length; $i++) { $dec[$i] = $enc[$i] -bxor $key[$i % $key.Length] }
    $current = [System.Text.Encoding]::UTF8.GetString($dec)
}
catch { }
[void](Add-Check $r 'secret-unchanged' ($current -ceq $s.secret) "the stored reports-db secret is the one setup created: $($current -ceq $s.secret)" -Guard)

# Guard: the value comes through the library, not from a copy of the secret or the store file.
$all = (@($sources | Where-Object Extension -eq '.cs' | ForEach-Object { Get-CSharpCode $_.FullName }) -join "`n")
$raw = (@($sources | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join "`n")
$hacks = [System.Collections.Generic.List[string]]::new()
if ($raw.Contains($s.secret)) { $hacks.Add('the secret is copied into a workspace file') }
if ($all -match 'vault\.dat|Contoso\\\\Secrets|"Secrets"') { $hacks.Add('a workspace source reads the store file directly') }
if ($all -cmatch '\bnamespace\s+Contoso\.Secrets\b') { $hacks.Add('a workspace source declares Contoso.Secrets types') }
if ($all -cmatch '\.(GetMethod|GetProperty|GetField|InvokeMember)\s*\(|\bdynamic\b') { $hacks.Add('reflection or dynamic') }
if ((Get-Content -Raw -LiteralPath $project.FullName) -notmatch '<PackageReference\s+Include="Contoso\.Secrets"') { $hacks.Add('Contoso.Secrets PackageReference removed') }
[void](Add-Check $r 'no-workarounds' (-not $hacks.Count) ($(if ($hacks.Count) { $hacks -join '; ' } else { 'the secret is read through Contoso.Secrets, not copied or read from the store file' })) -Guard)

# Improvement: the development password is gone from the source.
$devGone = -not $raw.Contains('Dev-Only-P@ss1')
[void](Add-Check $r 'dev-password-removed' $devGone "the hard-coded development password is no longer in the workspace source: $devGone")

$r.facts.project = $project.FullName
$r.facts.generatedNames = "$($s.store).$($s.open)() -> $($s.reveal)(name).$($s.plain)"
Complete-CheckResult -Result $r -OutFile $OutFile
