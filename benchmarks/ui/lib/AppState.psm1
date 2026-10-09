Set-StrictMode -Version Latest

# Desktop side of the UI benchmark: finds, launches, prepares, reads, and closes the benchmark's app
# instances through Win32 and UI Automation. It only ever closes processes by pid after checking that
# the pid still belongs to the same process (name, install folder, start time) that the benchmark owns.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (-not ('UiBench.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace UiBench {
    public sealed class WindowInfo {
        public long Hwnd; public int ProcessId; public string ClassName; public string Title;
        public bool Visible; public bool Minimized; public bool Cloaked;
    }

    public static class Native {
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        private static WindowInfo Describe(IntPtr h) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            var cls = new StringBuilder(256); GetClassName(h, cls, cls.Capacity);
            var title = new StringBuilder(512); GetWindowText(h, title, title.Capacity);
            int cloaked = 0; DwmGetWindowAttribute(h, 14, out cloaked, 4);
            return new WindowInfo {
                Hwnd = h.ToInt64(), ProcessId = (int)pid, ClassName = cls.ToString(), Title = title.ToString(),
                Visible = IsWindowVisible(h), Minimized = IsIconic(h), Cloaked = cloaked != 0
            };
        }

        public static List<WindowInfo> TopLevel() {
            var list = new List<WindowInfo>();
            EnumWindows((h, l) => { list.Add(Describe(h)); return true; }, IntPtr.Zero);
            return list;
        }

        public static List<WindowInfo> Children(long hwnd) {
            var list = new List<WindowInfo>();
            EnumChildWindows(new IntPtr(hwnd), (h, l) => { list.Add(Describe(h)); return true; }, IntPtr.Zero);
            return list;
        }

        public static bool Exists(long hwnd) { return IsWindow(new IntPtr(hwnd)); }
        public static bool Minimized(long hwnd) { return IsIconic(new IntPtr(hwnd)); }
        public static bool Show(long hwnd, int cmd) { return ShowWindow(new IntPtr(hwnd), cmd); }
    }
}
'@
}

$script:SW_SHOWNOACTIVATE = 4
$script:SW_SHOWMINNOACTIVE = 7
# UWP apps (Calculator) live in an ApplicationFrameWindow owned by ApplicationFrameHost. When such an
# app is minimized its CoreWindow can detach from the frame, so remember each process's frame.
$script:FrameByPid = @{}

# Element that shows an app instance finished starting.
$script:ReadyElement = @{ calculator = 'CalculatorResults'; gallery = 'controlsSearchBox' }
# What a fresh launch waits for. Calculator reopens in the user's last mode, and only the calculator
# modes have CalculatorResults (Date calculation and the converters don't); the nav button is in every mode.
$script:LaunchElement = @{ calculator = 'TogglePaneButton'; gallery = 'controlsSearchBox' }
$script:BidiMarks = '[\u200E\u200F\u202A-\u202E\u2066-\u2069]'

#region App and process discovery

function Get-AppInfo {
    # Package, install folder, launch id, and process name of a benchmark app.
    param([Parameter(Mandatory)][ValidateSet('calculator', 'gallery')][string]$App, [Parameter(Mandatory)]$Config)
    $name = if ($App -eq 'calculator') { 'Microsoft.WindowsCalculator' } else { [string]$Config.gallery.packageName }
    $pkg = @(Get-AppxPackage -Name $name | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1)
    if (-not $pkg) {
        $fix = if ($App -eq 'calculator') { 'Install Windows Calculator from the Microsoft Store.' } else { 'Run benchmarks\ui\setup-gallery.ps1 to build and install it.' }
        throw "The $App app ($name) is not installed. $fix"
    }
    $pkg = $pkg[0]
    [xml]$manifest = Get-Content -Raw -LiteralPath (Join-Path $pkg.InstallLocation 'AppxManifest.xml')
    $application = @($manifest.Package.Applications.Application)[0]
    [pscustomobject]@{
        App               = $App
        PackageName       = $pkg.Name
        PackageFamilyName = $pkg.PackageFamilyName
        Version           = [string]$pkg.Version
        Architecture      = [string]$pkg.Architecture
        InstallLocation   = $pkg.InstallLocation.TrimEnd('\') + '\'
        Aumid             = "$($pkg.PackageFamilyName)!$($application.Id)"
        ProcessName       = [System.IO.Path]::GetFileNameWithoutExtension([string]$application.Executable)
        Protocol          = if ($App -eq 'gallery') { [string]$Config.gallery.protocol } else { $null }
        DisplayName       = if ($App -eq 'gallery') { [string]$Config.gallery.displayName } else { 'Calculator' }
    }
}

function Get-ProcessStartTime {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process)
    try { return $Process.StartTime.ToUniversalTime().ToString('o') } catch { return $null }
}

function Get-AppProcesses {
    # Running processes with the app's executable name. Instances run from the app's install folder;
    # related ones (another install of the same app, e.g. the Store WinUI Gallery) come from elsewhere.
    param([Parameter(Mandatory)]$AppInfo)
    foreach ($p in @(Get-Process -Name $AppInfo.ProcessName -ErrorAction SilentlyContinue)) {
        $path = try { $p.Path } catch { $null }
        [pscustomobject]@{
            pid       = $p.Id
            name      = $p.ProcessName
            startTime = Get-ProcessStartTime $p
            path      = $path
            instance  = [bool]($path -and $path.StartsWith($AppInfo.InstallLocation, [StringComparison]::OrdinalIgnoreCase))
        }
    }
}

function Find-InstanceWindow {
    # Top-level window of an app process: its own window (WinUI 3, Win32), or the UWP frame that hosts
    # its CoreWindow. Returns $null when the process has no window yet.
    param([Parameter(Mandatory)][int]$ProcessId)
    $top = [UiBench.Native]::TopLevel()
    $own = @($top | Where-Object { $_.ProcessId -eq $ProcessId -and $_.Visible -and -not $_.Cloaked -and $_.Title -and $_.ClassName -ne 'Windows.UI.Core.CoreWindow' })
    if ($own) { return ($own | Sort-Object { $_.ClassName -ne 'WinUIDesktopWin32WindowClass' } | Select-Object -First 1).Hwnd }
    foreach ($frame in $top | Where-Object ClassName -eq 'ApplicationFrameWindow') {
        if (@([UiBench.Native]::Children($frame.Hwnd) | Where-Object { $_.ClassName -eq 'Windows.UI.Core.CoreWindow' -and $_.ProcessId -eq $ProcessId })) {
            $script:FrameByPid[$ProcessId] = $frame.Hwnd
            return $frame.Hwnd
        }
    }
    $cached = $script:FrameByPid[$ProcessId]
    if ($cached -and [UiBench.Native]::Exists($cached)) { return $cached }
    return $null
}

#endregion

#region UI Automation

function Get-WindowElement {
    param([Parameter(Mandatory)][long]$Hwnd)
    return [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Hwnd)
}

function Find-Element {
    # First descendant with an AutomationId, or with a Name when the selector starts with 'name:'.
    param([Parameter(Mandatory)]$Root, [Parameter(Mandatory)][string]$Selector)
    $property = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
    $value = $Selector
    if ($Selector.StartsWith('name:')) { $property = [System.Windows.Automation.AutomationElement]::NameProperty; $value = $Selector.Substring(5) }
    $condition = [System.Windows.Automation.PropertyCondition]::new($property, $value)
    try { return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition) }
    catch { return $null }
}

function Wait-InstanceElement {
    # Waits for an element in a process's window; returns the element and the window handle.
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$Selector, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $hwnd = Find-InstanceWindow -ProcessId $ProcessId
        if ($hwnd) {
            $el = Find-Element -Root (Get-WindowElement $hwnd) -Selector $Selector
            if ($el) { return [pscustomobject]@{ Element = $el; Hwnd = $hwnd } }
        }
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { throw "Process $ProcessId exited while waiting for '$Selector'." }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out after $TimeoutSeconds s waiting for '$Selector' in process $ProcessId."
}

function Get-Pattern {
    param([Parameter(Mandatory)]$Element, [Parameter(Mandatory)]$Pattern)
    $obj = $null
    if ($Element.TryGetCurrentPattern($Pattern, [ref]$obj)) { return $obj }
    return $null
}

function Invoke-Element {
    # Activates an element with the first pattern it supports: invoke, select, toggle, or expand.
    param([Parameter(Mandatory)]$Element)
    $p = Get-Pattern $Element ([System.Windows.Automation.InvokePattern]::Pattern)
    if ($p) { $p.Invoke(); return }
    $p = Get-Pattern $Element ([System.Windows.Automation.SelectionItemPattern]::Pattern)
    if ($p) { $p.Select(); return }
    $p = Get-Pattern $Element ([System.Windows.Automation.TogglePattern]::Pattern)
    if ($p) { $p.Toggle(); return }
    $p = Get-Pattern $Element ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($p) { $p.Expand(); return }
    throw "Element '$($Element.Current.AutomationId)' supports no invoke, select, toggle, or expand pattern."
}

function ConvertTo-PlainText {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return $null }
    return ($Text -replace $script:BidiMarks, '').Trim()
}

function Read-AppValue {
    # One state key of an app window. $null means unreadable (the element is not on screen).
    param([Parameter(Mandatory)]$Root, [Parameter(Mandatory)][string]$App, [Parameter(Mandatory)][string]$Key)
    switch ("$App.$Key") {
        'calculator.display' {
            $el = Find-Element $Root 'CalculatorResults'
            if (-not $el) { return $null }
            $text = ConvertTo-PlainText $el.Current.Name
            return ($text -replace '^(?i)display is\s*', '' -replace '[,\s]', '')
        }
        'calculator.mode' {
            $el = Find-Element $Root 'Header'
            if (-not $el) { return $null }
            $text = ConvertTo-PlainText $el.Current.Name
            foreach ($m in 'scientific', 'standard', 'programmer', 'graphing', 'date') { if ($text -match "(?i)\b$m\b") { return $m } }
            return $text.ToLowerInvariant()
        }
        'gallery.colors' {
            $el = Find-Element $Root 'Combo1'
            if (-not $el) { return $null }
            $sel = Get-Pattern $el ([System.Windows.Automation.SelectionPattern]::Pattern)
            if ($sel) {
                $items = @($sel.Current.GetSelection())
                if ($items) { return ConvertTo-PlainText $items[0].Current.Name }
            }
            $val = Get-Pattern $el ([System.Windows.Automation.ValuePattern]::Pattern)
            if ($val) { return ConvertTo-PlainText $val.Current.Value }
            return ''
        }
        'gallery.toggleWork' {
            $el = Find-Element $Root 'ToggleSwitch2'
            if (-not $el) { return $null }
            $t = Get-Pattern $el ([System.Windows.Automation.TogglePattern]::Pattern)
            if (-not $t) { return $null }
            return ($t.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
        }
        'gallery.dialogOpen' {
            return [bool]((Find-Element $Root 'name:Save your work?') -or (Find-Element $Root "name:Don't Save"))
        }
        'gallery.dialogResult' {
            $el = Find-Element $Root 'DialogResult'
            if (-not $el) { return $null }
            return ConvertTo-PlainText $el.Current.Name
        }
        'gallery.page' {
            $el = Find-Element $Root '__CurrentPage'
            if (-not $el) { return $null }
            return ConvertTo-PlainText $el.Current.Name
        }
        default { throw "Unknown state key '$Key' for $App." }
    }
}

function Get-AppKeys {
    param([Parameter(Mandatory)][string]$App)
    if ($App -eq 'calculator') { return @('display', 'mode') }
    return @('colors', 'toggleWork', 'dialogOpen', 'dialogResult', 'page')
}

function Read-InstanceState {
    # Every state key of one instance. A minimized window is restored without activation only when
    # -AllowRestore is set (never for windows the benchmark does not own), since a minimized UWP app
    # is suspended and cannot answer UI Automation.
    param([Parameter(Mandatory)]$AppInfo, [Parameter(Mandatory)][int]$ProcessId, [switch]$AllowRestore)
    $values = [ordered]@{}
    $restored = $false
    $hwnd = Find-InstanceWindow -ProcessId $ProcessId
    if (-not $hwnd) { return [pscustomobject]@{ values = $values; restored = $false; minimized = $null; error = 'no window found' } }
    $minimized = [UiBench.Native]::Minimized($hwnd)
    if ($minimized) {
        if (-not $AllowRestore) { return [pscustomobject]@{ values = $values; restored = $false; minimized = $true; error = 'window is minimized; not read' } }
        [void][UiBench.Native]::Show($hwnd, $script:SW_SHOWNOACTIVATE)
        $restored = $true
        try { $hwnd = (Wait-InstanceElement -ProcessId $ProcessId -Selector $script:ReadyElement[$AppInfo.App] -TimeoutSeconds 15).Hwnd }
        catch { return [pscustomobject]@{ values = $values; restored = $true; minimized = $true; error = "restored but not readable: $($_.Exception.Message)" } }
    }
    $root = Get-WindowElement $hwnd
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($k in Get-AppKeys $AppInfo.App) {
        try { $values[$k] = Read-AppValue -Root $root -App $AppInfo.App -Key $k }
        catch { $values[$k] = $null; $errors.Add("$k`: $($_.Exception.Message)") }
    }
    [pscustomobject]@{ values = $values; restored = $restored; minimized = $minimized; error = if ($errors.Count) { $errors -join '; ' } else { $null } }
}

#endregion

#region Launch, setup, teardown

function Start-AppInstance {
    # Starts a new instance (through its launch id, or a deep link) and returns its pid once the
    # window is ready. The new process is identified by diffing the app's pids before and after.
    param([Parameter(Mandatory)]$AppInfo, [string]$Uri, [int]$TimeoutSeconds = 90)
    $before = @(Get-AppProcesses $AppInfo | ForEach-Object pid)
    $target = if ($Uri) { $Uri } else { "shell:AppsFolder\$($AppInfo.Aumid)" }
    Start-Process -FilePath $target
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $new = @()
    while (-not $new -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $new = @(Get-AppProcesses $AppInfo | Where-Object { $_.instance -and $_.pid -notin $before })
    }
    if (-not $new) { throw "$($AppInfo.DisplayName) did not start a new process within $TimeoutSeconds s (launched '$target')." }
    if ($new.Count -gt 1) { throw "$($AppInfo.DisplayName) started $($new.Count) processes ($(@($new.pid) -join ', ')); expected one." }
    $remaining = [Math]::Max(5, [int]($deadline - [DateTime]::UtcNow).TotalSeconds)
    $ready = Wait-InstanceElement -ProcessId $new[0].pid -Selector $script:LaunchElement[$AppInfo.App] -TimeoutSeconds $remaining
    [pscustomobject]@{ pid = $new[0].pid; startTime = $new[0].startTime; hwnd = $ready.Hwnd }
}

function Get-CalculatorMode {
    # The mode a Calculator window shows (see Read-AppValue calculator.mode); $null when unreadable.
    param([Parameter(Mandatory)][int]$ProcessId, [int]$TimeoutSeconds = 10)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $hwnd = Find-InstanceWindow -ProcessId $ProcessId
        if ($hwnd) {
            $mode = try { Read-AppValue -Root (Get-WindowElement $hwnd) -App calculator -Key mode } catch { $null }
            if ($mode) { return $mode }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function ConvertTo-CalculatorNavId {
    # The nav item AutomationId for a mode as Read-AppValue reports it (standard -> Standard, date -> Date).
    param([Parameter(Mandatory)][string]$Mode)
    return (Get-Culture).TextInfo.ToTitleCase($Mode.ToLowerInvariant())
}

function Set-CalculatorMode {
    # Switches through the nav pane. Scenarios use standard or scientific; restoring the user's mode
    # may need any other nav item (programmer, date, a converter).
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$Mode)
    $hwnd = (Wait-InstanceElement -ProcessId $ProcessId -Selector 'TogglePaneButton' -TimeoutSeconds 15).Hwnd
    if ((Read-AppValue -Root (Get-WindowElement $hwnd) -App calculator -Key mode) -eq $Mode) { return }
    Invoke-Element (Wait-InstanceElement -ProcessId $ProcessId -Selector 'TogglePaneButton' -TimeoutSeconds 10).Element
    $item = Wait-InstanceElement -ProcessId $ProcessId -Selector (ConvertTo-CalculatorNavId $Mode) -TimeoutSeconds 10
    Invoke-Element $item.Element
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        $hwnd = Find-InstanceWindow -ProcessId $ProcessId
        if ($hwnd -and (Read-AppValue -Root (Get-WindowElement $hwnd) -App calculator -Key mode) -eq $Mode) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Calculator (pid $ProcessId) did not switch to $Mode mode."
}

function Wait-InstanceValues {
    # Polls until every expected state value holds; throws with what was seen otherwise.
    param([Parameter(Mandatory)]$AppInfo, [Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][System.Collections.IDictionary]$Expect, [int]$TimeoutSeconds = 15)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $state = Read-InstanceState -AppInfo $AppInfo -ProcessId $ProcessId
        $wrong = @(foreach ($k in $Expect.Keys) {
                $have = $state.values[$k]
                $haveText = if ($have -is [bool]) { $have.ToString().ToLowerInvariant() } else { [string]$have }
                $wantText = if ($Expect[$k] -is [bool]) { $Expect[$k].ToString().ToLowerInvariant() } else { [string]$Expect[$k] }
                if ($null -eq $have -or $haveText -ne $wantText) { "$k is '$haveText', expected '$wantText'" }
            })
        if (-not $wrong) { return $state }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Setup of pid $ProcessId did not reach the expected state: $($wrong -join '; ')."
}

function Invoke-InstanceSetup {
    # Launches and prepares one scenario instance; returns its identity and the state read before any
    # minimize (a minimized UWP app cannot be read).
    param([Parameter(Mandatory)]$AppInfo, [Parameter(Mandatory)][System.Collections.IDictionary]$Instance)
    $get = { param($k) if ($Instance.Contains($k)) { $Instance[$k] } else { $null } }
    $expect = [ordered]@{}
    if ($AppInfo.App -eq 'calculator') {
        $started = Start-AppInstance -AppInfo $AppInfo
        if (-not $script:CalculatorRestoreMode) {
            # The first Calculator the benchmark opens still shows the user's saved mode.
            $saved = Get-CalculatorMode -ProcessId $started.pid
            if ($saved) { [void](Register-CalculatorMode -Mode $saved) }
            elseif (-not $script:CalculatorModeUnreadable) {
                $script:CalculatorModeUnreadable = $true
                Write-Warning "Could not read Calculator's current mode, so it can't be restored afterwards."
            }
        }
        $mode = if (& $get 'mode') { [string](& $get 'mode') } else { 'standard' }
        Set-CalculatorMode -ProcessId $started.pid -Mode $mode
        $expect['mode'] = $mode
        $digits = [string](& $get 'enter')
        Invoke-Element (Wait-InstanceElement -ProcessId $started.pid -Selector 'clearButton' -TimeoutSeconds 10).Element
        foreach ($d in $digits.ToCharArray()) { Invoke-Element (Wait-InstanceElement -ProcessId $started.pid -Selector "num$($d)Button" -TimeoutSeconds 10).Element }
        $expect['display'] = if ($digits) { [string][long]$digits } else { '0' }
    }
    else {
        $page = [string](& $get 'page')
        $uri = if ($page) { "$($AppInfo.Protocol)://item/$page" } else { $null }
        $started = Start-AppInstance -AppInfo $AppInfo -Uri $uri
        foreach ($id in @(& $get 'waitFor') | Where-Object { $_ }) { [void](Wait-InstanceElement -ProcessId $started.pid -Selector $id -TimeoutSeconds 30) }
        foreach ($id in @(& $get 'invoke') | Where-Object { $_ }) { Invoke-Element (Wait-InstanceElement -ProcessId $started.pid -Selector $id -TimeoutSeconds 30).Element }
    }
    foreach ($k in @((& $get 'expect') ?? @{}).Keys) { $expect[$k] = $Instance.expect[$k] }
    $state = Wait-InstanceValues -AppInfo $AppInfo -ProcessId $started.pid -Expect $expect
    $hwnd = Find-InstanceWindow -ProcessId $started.pid
    $minimize = [bool](& $get 'minimize')
    if ($minimize) {
        [void][UiBench.Native]::Show($hwnd, $script:SW_SHOWMINNOACTIVE)
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while (-not [UiBench.Native]::Minimized($hwnd) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (-not [UiBench.Native]::Minimized($hwnd)) { throw "Could not minimize pid $($started.pid)." }
    }
    [pscustomobject]@{
        pid        = $started.pid
        startTime  = $started.startTime
        hwnd       = $hwnd
        role       = [string](& $get 'role')
        target     = [bool](& $get 'target')
        launchedBy = 'harness'
        alive      = $true
        minimized  = $minimize
        values     = $state.values
        restored   = $false
        error      = $null
    }
}

function Get-ForeignInstances {
    # Instances of the app the benchmark does not own (not in -Owned, matched by pid and start time).
    param([Parameter(Mandatory)]$AppInfo, [AllowEmptyCollection()][object[]]$Owned = @())
    @(Get-AppProcesses $AppInfo | Where-Object { $_.instance } | Where-Object {
            $p = $_
            -not @($Owned | Where-Object { [int]$_.pid -eq $p.pid -and (-not $_.startTime -or $_.startTime -eq $p.startTime) })
        })
}

function Get-AppSnapshot {
    # State of every instance of the app plus related processes. -Known instances (from setup) keep
    # their identity and are marked dead when gone; other instances are new or foreign.
    param(
        [Parameter(Mandatory)]$AppInfo,
        [AllowEmptyCollection()][object[]]$Known = @(),
        [AllowEmptyCollection()][object[]]$Foreign = @(),
        [switch]$RestoreOwnedMinimized
    )
    $procs = @(Get-AppProcesses $AppInfo)
    $instances = [System.Collections.Generic.List[object]]::new()
    $matched = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($k in $Known) {
        $p = @($procs | Where-Object { $_.instance -and $_.pid -eq [int]$k.pid -and $_.startTime -eq $k.startTime })
        $entry = [ordered]@{ pid = [int]$k.pid; startTime = $k.startTime; role = $k.role; target = [bool]$k.target; launchedBy = $k.launchedBy; alive = [bool]$p }
        if ($p) {
            [void]$matched.Add([int]$k.pid)
            $s = Read-InstanceState -AppInfo $AppInfo -ProcessId $k.pid -AllowRestore:($RestoreOwnedMinimized -and $k.launchedBy -ne 'foreign')
            $entry.values = $s.values; $entry.restored = $s.restored; $entry.minimized = $s.minimized; $entry.error = $s.error
        }
        $instances.Add([pscustomobject]$entry)
    }
    foreach ($p in $procs | Where-Object { $_.instance -and -not $matched.Contains($_.pid) }) {
        $isForeign = [bool]@($Foreign | Where-Object { [int]$_.pid -eq $p.pid -and $_.startTime -eq $p.startTime })
        $s = Read-InstanceState -AppInfo $AppInfo -ProcessId $p.pid -AllowRestore:($RestoreOwnedMinimized -and -not $isForeign)
        $entry = [ordered]@{ pid = $p.pid; startTime = $p.startTime; alive = $true; values = $s.values; restored = $s.restored; minimized = $s.minimized; error = $s.error }
        if ($isForeign) { $entry.launchedBy = 'foreign' }
        $instances.Add([pscustomobject]$entry)
    }
    [pscustomobject]@{
        takenAt   = [DateTime]::UtcNow.ToString('o')
        instances = @($instances)
        related   = @($procs | Where-Object { -not $_.instance } | ForEach-Object { [pscustomobject]@{ pid = $_.pid; startTime = $_.startTime; name = $_.name; path = $_.path } })
    }
}

function Stop-OwnedInstance {
    # Closes one process by pid, but only if it is still the same app process (name, install folder,
    # start time). Calculator remembers its mode across launches, so it is first switched back to the
    # mode recorded before the benchmark changed it (see Register-CalculatorMode).
    param([Parameter(Mandatory)]$AppInfo, [Parameter(Mandatory)][int]$ProcessId, [string]$StartTime)
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) { return 'gone' }
    $path = try { $p.Path } catch { $null }
    if ($p.ProcessName -ne $AppInfo.ProcessName -or -not $path -or -not $path.StartsWith($AppInfo.InstallLocation, [StringComparison]::OrdinalIgnoreCase)) { return 'not-ours' }
    if ($StartTime -and (Get-ProcessStartTime $p) -ne $StartTime) { return 'not-ours' }
    if ($AppInfo.App -eq 'calculator' -and $script:CalculatorRestoreMode) {
        try {
            $hwnd = Find-InstanceWindow -ProcessId $ProcessId
            if ($hwnd -and [UiBench.Native]::Minimized($hwnd)) { [void][UiBench.Native]::Show($hwnd, $script:SW_SHOWNOACTIVATE) }
            Set-CalculatorMode -ProcessId $ProcessId -Mode $script:CalculatorRestoreMode
        }
        catch {
            $script:CalculatorRestoreFailures++
            Write-Warning "Could not switch Calculator (pid $ProcessId) back to $($script:CalculatorRestoreMode) mode: $($_.Exception.Message)"
        }
    }
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    if (-not $p.WaitForExit(15000)) { return 'still-running' }
    $script:FrameByPid.Remove($ProcessId)
    return 'stopped'
}

#endregion

#region Ownership ledger

function Save-OwnedLedger {
    # Processes the harness currently owns, written after every launch so an interrupted run can
    # close them on the next start. An empty list removes the ledger.
    param([Parameter(Mandatory)][string]$Path, [AllowEmptyCollection()][object[]]$Entries = @())
    if ($Entries) { ConvertTo-Json -InputObject @($Entries) -Depth 4 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
    else { Remove-Item -LiteralPath $Path -ErrorAction SilentlyContinue }
}

function Stop-RelatedProcess {
    # A related app (e.g. another WinUI Gallery install) that started during a run: same pid, same start time.
    param([Parameter(Mandatory)]$Entry)
    $p = Get-Process -Id ([int]$Entry.pid) -ErrorAction SilentlyContinue
    if (-not $p) { return 'gone' }
    if ($p.ProcessName -ne $Entry.name -or (Get-ProcessStartTime $p) -ne $Entry.startTime) { return 'not-ours' }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    if (-not $p.WaitForExit(15000)) { return 'still-running' }
    return 'stopped'
}

function Stop-OwnedEntry {
    # Closes one ledger entry ({app, kind: instance|related, pid, startTime, name}); returns
    # gone, not-ours, stopped, or still-running.
    param([Parameter(Mandatory)]$Entry, [Parameter(Mandatory)][hashtable]$Apps, [Parameter(Mandatory)]$Config)
    if ($Entry.kind -eq 'related') { return Stop-RelatedProcess -Entry $Entry }
    $info = $Apps[[string]$Entry.app]
    if (-not $info) { $info = Get-AppInfo -App ([string]$Entry.app) -Config $Config; $Apps[[string]$Entry.app] = $info }
    return Stop-OwnedInstance -AppInfo $info -ProcessId ([int]$Entry.pid) -StartTime ([string]$Entry.startTime)
}

function Clear-OwnedLedger {
    # Closes processes left in a ledger by an interrupted run; throws if any is still running.
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][hashtable]$Apps, [Parameter(Mandatory)]$Config)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $stale = @(Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json)
    Write-Host "Closing $($stale.Count) process(es) left by an interrupted run..."
    $left = @($stale | Where-Object { (Stop-OwnedEntry -Entry $_ -Apps $Apps -Config $Config) -eq 'still-running' })
    if ($left) { throw "Could not close pid(s) $(@($left.pid) -join ', ') from an interrupted run; close them and rerun." }
    Save-OwnedLedger -Path $Path
}

#endregion

#region Scenario setup and adoption

function Start-ScenarioInstances {
    # Launches and prepares every setup instance of a scenario. Each launched process is added to
    # -Owned and the ledger is saved before anything else can fail, so teardown always closes it.
    param(
        [Parameter(Mandatory)]$AppInfo,
        [Parameter(Mandatory)]$Scenario,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Owned,
        [Parameter(Mandatory)][string]$LedgerPath
    )
    $setup = [System.Collections.Generic.List[object]]::new()
    foreach ($inst in $Scenario.Instances) {
        $before = @(Get-AppProcesses $AppInfo | ForEach-Object pid)
        try { $si = Invoke-InstanceSetup -AppInfo $AppInfo -Instance $inst }
        catch {
            foreach ($p in @(Get-AppProcesses $AppInfo | Where-Object { $_.instance -and $_.pid -notin $before })) {
                $Owned.Add([pscustomobject]@{ app = $AppInfo.App; kind = 'instance'; pid = $p.pid; startTime = $p.startTime })
            }
            Save-OwnedLedger -Path $LedgerPath -Entries @($Owned)
            throw
        }
        $Owned.Add([pscustomobject]@{ app = $AppInfo.App; kind = 'instance'; pid = $si.pid; startTime = $si.startTime })
        Save-OwnedLedger -Path $LedgerPath -Entries @($Owned)
        $setup.Add($si)
    }
    return , $setup
}

function Get-SetupSnapshot {
    # The state after setup: setup's own reads (a minimized UWP app cannot be read again without
    # restoring it), plus foreign instances and related processes as they are now.
    param([Parameter(Mandatory)]$AppInfo, [AllowEmptyCollection()][object[]]$Setup = @(), [AllowEmptyCollection()][object[]]$Foreign = @())
    $instances = [System.Collections.Generic.List[object]]::new()
    foreach ($s in $Setup) { $instances.Add($s) }
    foreach ($f in $Foreign) {
        $st = Read-InstanceState -AppInfo $AppInfo -ProcessId $f.pid
        $instances.Add([pscustomobject]@{ pid = $f.pid; startTime = $f.startTime; launchedBy = 'foreign'; alive = $true; values = $st.values; minimized = $st.minimized; error = $st.error })
    }
    [pscustomobject]@{
        takenAt   = [DateTime]::UtcNow.ToString('o')
        instances = @($instances)
        related   = @(Get-AppProcesses $AppInfo | Where-Object { -not $_.instance } | ForEach-Object { [pscustomobject]@{ pid = $_.pid; startTime = $_.startTime; name = $_.name; path = $_.path } })
    }
}

function Add-StartedProcesses {
    # Takes ownership of instances and related processes that appeared between two snapshots
    # (launched during the run), so teardown closes them by pid.
    param(
        [Parameter(Mandatory)]$AppInfo,
        [Parameter(Mandatory)]$Before,
        [Parameter(Mandatory)]$After,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Owned,
        [Parameter(Mandatory)][string]$LedgerPath
    )
    foreach ($a in @($After.instances | Where-Object { $_.alive -and -not ($_.PSObject.Properties['launchedBy'] -and $_.launchedBy) })) {
        $Owned.Add([pscustomobject]@{ app = $AppInfo.App; kind = 'instance'; pid = $a.pid; startTime = $a.startTime })
    }
    foreach ($rel in @($After.related)) {
        if (@($Before.related | Where-Object { $_.pid -eq $rel.pid -and $_.startTime -eq $rel.startTime })) { continue }
        $Owned.Add([pscustomobject]@{ app = $AppInfo.App; kind = 'related'; pid = $rel.pid; startTime = $rel.startTime; name = $rel.name })
    }
    Save-OwnedLedger -Path $LedgerPath -Entries @($Owned)
}

function Stop-OwnedEntries {
    # Closes every owned entry; returns one {pid, kind, result} per entry.
    param([AllowEmptyCollection()][object[]]$Owned = @(), [Parameter(Mandatory)][hashtable]$Apps, [Parameter(Mandatory)]$Config)
    @(foreach ($o in $Owned) {
            [pscustomobject]@{ pid = $o.pid; kind = $o.kind; result = (Stop-OwnedEntry -Entry $o -Apps $Apps -Config $Config) }
        })
}

#endregion

#region Calculator's saved mode

# Calculator reopens in the last mode used, so a scenario that switches it to Scientific would change
# the user's Calculator. The mode the first launched Calculator shows is recorded in a file, every
# owned Calculator is switched back to it before it is closed, and the record is removed once the
# benchmark finishes cleanly. A benchmark that stops early keeps the record, so the next one restores
# the user's mode instead of recording the mode the interrupted run left behind.
$script:CalculatorModeRecordPath = $null
$script:CalculatorRestoreMode = $null
$script:CalculatorRestoreFailures = 0
$script:CalculatorModeUnreadable = $false

function Initialize-CalculatorModeRecord {
    [CmdletBinding()]
    # Uses -Path for the record and loads the mode an unfinished benchmark recorded there; returns it,
    # or $null when there is none (an unreadable record counts as none).
    param([Parameter(Mandatory)][string]$Path)
    $script:CalculatorModeRecordPath = $Path
    $script:CalculatorRestoreMode = $null
    $script:CalculatorRestoreFailures = 0
    $script:CalculatorModeUnreadable = $false
    if (Test-Path -LiteralPath $Path) {
        try {
            $mode = [string](Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json).mode
            if ([string]::IsNullOrWhiteSpace($mode)) { throw 'no mode' }
            $script:CalculatorRestoreMode = $mode
        }
        catch { Write-Warning "Ignoring unreadable Calculator mode record '$Path': $($_.Exception.Message)" }
    }
    return $script:CalculatorRestoreMode
}

function Register-CalculatorMode {
    # Records the user's mode, read from the first Calculator the benchmark launches. The first
    # recorded mode wins; returns the mode that will be restored.
    param([Parameter(Mandatory)][string]$Mode)
    if ($script:CalculatorRestoreMode) { return $script:CalculatorRestoreMode }
    $script:CalculatorRestoreMode = $Mode
    if ($script:CalculatorModeRecordPath) {
        $dir = Split-Path -Parent $script:CalculatorModeRecordPath
        if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        [ordered]@{ mode = $Mode; recordedAt = (Get-Date).ToString('o') } | ConvertTo-Json |
            Set-Content -LiteralPath $script:CalculatorModeRecordPath -Encoding utf8NoBOM
    }
    return $Mode
}

function Get-CalculatorRestoreMode { return $script:CalculatorRestoreMode }

function Complete-CalculatorModeRecord {
    [CmdletBinding()]
    # Removes the record after a clean finish. Keeps it when a Calculator could not be switched back,
    # or when -Unfinished (owned processes are still left to close), so the next benchmark restores it.
    param([switch]$Unfinished)
    $path = $script:CalculatorModeRecordPath
    if (-not $path) { return }
    if ($script:CalculatorRestoreFailures) {
        Write-Warning "Calculator may not reopen in your $($script:CalculatorRestoreMode) mode; switch it back by hand. The next benchmark run also restores the mode recorded in '$path'."
        return
    }
    if ($Unfinished) { return }
    Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
}

#endregion

Export-ModuleMember -Function Get-AppInfo, Get-AppProcesses, Find-InstanceWindow, Find-Element, Wait-InstanceElement, Invoke-Element,
Read-AppValue, Get-AppKeys, Read-InstanceState, Start-AppInstance, Get-CalculatorMode, ConvertTo-CalculatorNavId, Set-CalculatorMode,
Wait-InstanceValues, Invoke-InstanceSetup, Get-ForeignInstances, Get-AppSnapshot, Stop-OwnedInstance, Save-OwnedLedger, Stop-RelatedProcess,
Stop-OwnedEntry, Clear-OwnedLedger, Start-ScenarioInstances, Get-SetupSnapshot, Add-StartedProcesses, Stop-OwnedEntries,
Initialize-CalculatorModeRecord, Register-CalculatorMode, Get-CalculatorRestoreMode, Complete-CalculatorModeRecord
