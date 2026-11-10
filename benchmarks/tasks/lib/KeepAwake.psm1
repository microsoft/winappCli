# Keeps Windows from sleeping while a benchmark runs. SetThreadExecutionState applies to the
# calling thread, so call Enable and Disable from the same script thread (the run loop's try/finally).
# It does not stop the screen from locking.

# ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED, and ES_CONTINUOUS alone to release.
$script:KeepAwakeFlags = [uint32]2147483651
$script:ReleaseFlags = [uint32]2147483648

function Get-ExecutionStateApi {
    if (-not ('WinAppTaskBench.Power' -as [type])) {
        Add-Type -Namespace WinAppTaskBench -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
    }
    [WinAppTaskBench.Power]
}

function Enable-KeepAwake {
    [OutputType([bool])]
    param()
    if (-not $IsWindows) { return $false }
    try { return ((Get-ExecutionStateApi)::SetThreadExecutionState($script:KeepAwakeFlags) -ne 0) }
    catch { Write-Warning "Could not keep the machine awake: $($_.Exception.Message)"; return $false }
}

function Disable-KeepAwake {
    param()
    if (-not $IsWindows) { return }
    try { [void](Get-ExecutionStateApi)::SetThreadExecutionState($script:ReleaseFlags) } catch { }
}

Export-ModuleMember -Function Enable-KeepAwake, Disable-KeepAwake
