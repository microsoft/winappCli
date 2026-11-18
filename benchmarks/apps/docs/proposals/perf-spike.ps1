param([int]$Reps = 5)
# Perf noise spike (see performance.md): repeated winapp perf captures of the same apps to
# measure test-retest variation per metric. Writes perf-spike.csv next to this script.
# Expects app copies in %TEMP%\perfspike\<name> (folders mg-bare, mg-pub, fe-main, tm-bare in the spike).
$ErrorActionPreference = 'Continue'
$sp = "$env:TEMP\perfspike"
$outCsv = Join-Path $PSScriptRoot 'perf-spike.csv'
$env:WINAPP_UI_WORKFLOW_ID = "perfspike-$([guid]::NewGuid().ToString('N').Substring(0,12))"
$rows = @()

function J($text) { try { return $text | ConvertFrom-Json } catch { return $null } }
function UiBusy($a) { ($a.activity | Where-Object { $_.category -ne 'Unclassified' } | Measure-Object observedMs -Sum).Sum }
function CatMs($a, $name) { ($a.activity | Where-Object { $_.category -eq $name }).observedMs }
function Range($cap, $from, $to) {
  $a = J (winapp perf analyze $cap --from-marker $from --to-marker $to --json --max-bytes 60000 2>$null | Out-String)
  $h = J (winapp perf analyze $cap --view hotspots --from-marker $from --to-marker $to --limit 100 --json --max-bytes 200000 2>$null | Out-String)
  $frames = @($h.rows | ForEach-Object { $_.call.durationMs })
  [pscustomobject]@{
    busyMs = [math]::Round((UiBusy $a), 1)
    layoutMs = [math]::Round((CatMs $a 'Layout'), 1)
    parsingMs = [math]::Round((CatMs $a 'Parsing'), 1)
    cpuMs = $a.processResources.totalCpuMs
    slowFrames = [int]$h.total
    worstFrameMs = if ($frames.Count) { [math]::Round(($frames | Measure-Object -Maximum).Maximum, 1) } else { 0 }
    complete = $a.coverage.complete
  }
}

foreach ($app in 'mg-bare', 'mg-pub', 'fe-main', 'tm-bare') {
  $cs = (Get-ChildItem "$sp\$app" -Recurse -Filter *.csproj | Where-Object FullName -notmatch '\\(bin|obj)\\' | Select-Object -First 1).FullName
  foreach ($rep in 1..$Reps) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if ($rep -eq 1) { $runOut = winapp run $cs -c Release --arch x64 -p Platform=x64 --detach --json 2>&1 | Out-String }
    else { $runOut = winapp run $cs -c Release --arch x64 -p Platform=x64 --detach --json --no-build 2>&1 | Out-String }
    $procId = [regex]::Match($runOut, '"ProcessId"\s*:\s*(\d+)').Groups[1].Value
    if (-not $procId) { Write-Host "$app#${rep}: $(($runOut.Trim() -split "`n" | Select-Object -Last 3) -join ' ')" }
    if (-not $procId) { Write-Host "$app#${rep}: launch failed"; continue }
    $windowMs = $null
    foreach ($i in 1..40) {
      if ((winapp ui list-windows -a $procId --json 2>$null | Out-String) -match '"hwnd"') { $windowMs = $sw.ElapsedMilliseconds; break }
      Start-Sleep -Milliseconds 250
    }
    Start-Sleep 2
    $cap = "$sp\cap-$app-$rep"; Remove-Item $cap -Recurse -Force -EA SilentlyContinue
    $id = (J (winapp perf start --app $procId --output $cap --duration-sec 120 --max-size-mib 1024 --json 2>$null | Out-String)).id
    winapp perf mark $id --name idle-start --json | Out-Null; Start-Sleep 10; winapp perf mark $id --name idle-end --json | Out-Null

    $insp = J (winapp ui inspect -a $procId --interactive --json 2>$null | Out-String)
    $els = New-Object System.Collections.Generic.List[object]
    $stack = New-Object System.Collections.Stack; foreach ($e in $insp.windows[0].elements) { $stack.Push($e) }
    while ($stack.Count) { $e = $stack.Pop(); $els.Add($e); foreach ($c in @($e.children)) { if ($c) { $stack.Push($c) } } }
    $nav = @($els | Where-Object { $_.type -eq 'TabItem' } | Select-Object -First 6)
    $items = @($els | Where-Object { $_.type -in 'ListItem', 'TreeItem', 'DataItem' } | Select-Object -First 1)

    winapp perf mark $id --name nav-start --json | Out-Null
    foreach ($n in $nav) { winapp ui invoke $n.selector -a $procId 2>&1 | Out-Null; Start-Sleep -Milliseconds 800 }
    if ($nav.Count) { winapp ui invoke $nav[0].selector -a $procId 2>&1 | Out-Null; Start-Sleep -Milliseconds 800 }
    winapp perf mark $id --name nav-end --json | Out-Null

    winapp perf mark $id --name scroll-start --json | Out-Null
    if ($items.Count) {
      winapp ui focus $items[0].selector -a $procId 2>&1 | Out-Null
      foreach ($k in 'end', 'home', 'end') { winapp ui send-keys $k -a $procId --via send-input 2>&1 | Out-Null; Start-Sleep -Milliseconds 700 }
    }
    winapp perf mark $id --name scroll-end --json | Out-Null
    winapp perf stop $id --json 2>&1 | Out-Null
    winapp ui yield 2>&1 | Out-Null
    Stop-Process -Id $procId -ErrorAction SilentlyContinue

    $idle = Range $cap 'idle-start' 'idle-end'
    $navR = Range $cap 'nav-start' 'nav-end'
    $scr = Range $cap 'scroll-start' 'scroll-end'
    $res = (J (winapp perf analyze $cap --from-marker idle-start --to-marker idle-end --json --max-bytes 60000 2>$null | Out-String)).processResources
    $rows += [pscustomobject]@{
      app = $app; rep = $rep; windowMs = if ($rep -gt 1) { $windowMs } else { $null }
      idleCpuMs = $idle.cpuMs; privateMB = [math]::Round($res.last.privateBytes / 1MB, 1); workingSetMB = [math]::Round($res.last.workingSetBytes / 1MB, 1)
      handles = $res.last.handleCount; threads = $res.last.threadCount
      navItems = $nav.Count; navBusyMs = $navR.busyMs; navLayoutMs = $navR.layoutMs; navParsingMs = $navR.parsingMs; navCpuMs = $navR.cpuMs; navSlowFrames = $navR.slowFrames; navWorstFrameMs = $navR.worstFrameMs
      scrolled = [bool]$items.Count; scrollBusyMs = $scr.busyMs; scrollCpuMs = $scr.cpuMs; scrollSlowFrames = $scr.slowFrames; scrollWorstFrameMs = $scr.worstFrameMs
      complete = $idle.complete -and $navR.complete
    }
    Write-Host ("{0}#{1}: window={2}ms idleCpu={3} priv={4}MB navBusy={5} navSlow={6} worst={7}" -f $app, $rep, $windowMs, $idle.cpuMs, $rows[-1].privateMB, $navR.busyMs, $navR.slowFrames, $navR.worstFrameMs)
    $rows | Export-Csv $outCsv -NoTypeInformation
  }
}
Write-Host "Wrote $outCsv"
