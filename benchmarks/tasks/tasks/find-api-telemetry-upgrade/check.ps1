param([string]$Workspace, $Baseline, [string]$OutFile, [string]$StatePath = 'C:\bench-state\find-api-telemetry-upgrade.json')
$r = New-CheckResult -Task 'find-api-telemetry-upgrade'
$s = Read-TaskState $StatePath

$sources = @(Get-ChildItem -LiteralPath $Workspace -Recurse -File -Include '*.cs', '*.csproj', '*.props', '*.targets' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$project = @($sources | Where-Object Extension -eq '.csproj' | Sort-Object { $_.Name -ne 'OrderSync.csproj' }) | Select-Object -First 1
$telemetry = @($sources | Where-Object Name -eq 'AppTelemetry.cs') | Select-Object -First 1
if (-not $project -or -not $telemetry) {
    [void](Add-Check $r 'builds' $false "project: $([bool]$project); AppTelemetry.cs: $([bool]$telemetry)" -Required)
    Complete-CheckResult -Result $r -OutFile $OutFile
    return
}

# The fixture fails with CS0246/CS1061 against Contoso.Telemetry 2.0, so building is progress.
$build = Invoke-DotnetBuild $project.FullName
[void](Add-Check $r 'builds' $build.Ok $build.Detail -Required)

# Run the app with the library's outbox redirected; only the real event method plus the real send
# method write an event line, signed with this run's value that only the generated library knows.
$outbox = Join-Path ([System.IO.Path]::GetTempPath()) "outbox-$([guid]::NewGuid().ToString('N')).jsonl"
$events = @()
$runDetail = 'not run: the build failed'
if ($build.Ok) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($a in @('run', '--project', $project.FullName, '--no-build')) { $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $project.DirectoryName
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Environment['CONTOSO_TELEMETRY_OUTBOX'] = $outbox
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(90000)) { try { $p.Kill($true) } catch { } }
    $lines = if (Test-Path -LiteralPath $outbox) { @(Get-Content -LiteralPath $outbox) } else { @() }
    $items = @($lines | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } })
    $events = @($items | Where-Object { $_.kind -eq 'event' -and $_.name -eq 'orders_synced' -and $_.sig -eq $s.nonce })
    $runDetail = "app exit $(if ($p.HasExited) { $p.ExitCode } else { 'timeout' }); outbox lines: $($lines.Count); orders_synced events from the library: $($events.Count)"
    if (-not $events.Count -and $stderr.Result) { $runDetail += "; stderr: $(($stderr.Result -split "`r?`n" | Select-Object -First 2) -join ' ')" }
    Remove-Item -LiteralPath $outbox -ErrorAction SilentlyContinue
}
[void](Add-Check $r 'event-sent' ([bool]$events.Count) "running the app sends the orders_synced event through Contoso.Telemetry ($runDetail)" -Required)

# Guard: the build must not pass because the library was replaced, faked, or bypassed.
$code = Get-CSharpCode $telemetry.FullName
$all = (@($sources | Where-Object Extension -eq '.cs' | ForEach-Object { Get-CSharpCode $_.FullName }) -join "`n")
$projText = (@($sources | Where-Object Extension -ne '.cs' | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join "`n")
$hacks = [System.Collections.Generic.List[string]]::new()
if ($projText -notmatch '<PackageReference\s+Include="Contoso\.Telemetry"') { $hacks.Add('Contoso.Telemetry PackageReference removed') }
if ($code -match '(?m)^\s*#\s*(if|elif)\b') { $hacks.Add('#if in AppTelemetry.cs') }
if ($code -cmatch '\bdynamic\b') { $hacks.Add('dynamic in AppTelemetry.cs') }
if ($code -cmatch '\.(GetType|GetMethod|GetProperty|InvokeMember)\s*\(|Activator\.') { $hacks.Add('reflection in AppTelemetry.cs') }
if ($all -cmatch '\bnamespace\s+Contoso\.Telemetry\b|\b(class|struct|record|interface)\s+TelemetryClient\b') { $hacks.Add('a workspace source declares Contoso.Telemetry types') }
if ($all -match 'CONTOSO_TELEMETRY_OUTBOX|outbox\.jsonl') { $hacks.Add('a workspace source writes the telemetry outbox itself') }
[void](Add-Check $r 'no-workarounds' (-not $hacks.Count) ($(if ($hacks.Count) { $hacks -join '; ' } else { 'Contoso.Telemetry is still referenced and called directly: no #if, dynamic, reflection, stand-in types, or hand-written outbox' })) -Guard)

# Improvement: the event keeps the properties it had before the upgrade.
$props = if ($events.Count) { $events[0].properties } else { $null }
$kept = $props -and "$($props.count)" -eq '3' -and "$($props.elapsed_ms)" -match '^\d+$'
[void](Add-Check $r 'properties-kept' ([bool]$kept) "the event still carries count=3 and elapsed_ms: $(if ($props) { ($props | ConvertTo-Json -Compress) } else { 'no event' })")

$r.facts.project = $project.FullName
$r.facts.generatedNames = "$($s.client).$($s.event) + $($s.flush)"
Complete-CheckResult -Result $r -OutFile $OutFile
