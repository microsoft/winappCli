/**
 * Guards for long unattended runs on machines that may sleep or be suspended
 * (e.g. a Dev Box or VM hibernated by its host overnight). A suspended trial
 * hits its wall-clock limit or loses its SDK worker and would score 0, which
 * is machine noise, not an agent result.
 *
 * - `startKeepAwake`: ask Windows to keep the system and display on while the
 *   queue runs (best effort; a host-forced suspend can still happen).
 * - `systemSuspendedBetween`: check the System event log for a sleep/resume
 *   during a trial, so the queue can mark it interrupted and retry it.
 */
import { spawn, spawnSync } from "child_process";

/**
 * PowerShell that sets ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
 * and holds it while process `ownerPid` is alive. Prints the previous state
 * (non-zero on success). 0x80000003 is written as decimal because PowerShell
 * reads hex literals above 0x7FFFFFFF as negative Int32.
 */
export function keepAwakeScript(ownerPid: number): string {
  return [
    "$sig='[DllImport(\"kernel32.dll\")] public static extern uint SetThreadExecutionState(uint f);'",
    "$k=Add-Type -MemberDefinition $sig -Name Pwr -Namespace WinDevBench -PassThru",
    "'prev:' + $k::SetThreadExecutionState([uint32]2147483651)",
    `while (Get-Process -Id ${ownerPid} -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 30 }`,
  ].join("; ");
}

export function startKeepAwake(): () => void {
  if (process.platform !== "win32") return () => {};
  // Held until this Node process exits (the helper polls our PID) or stop() runs.
  const script = keepAwakeScript(process.pid);
  try {
    const child = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
      stdio: "ignore",
      windowsHide: true,
    });
    child.on("error", () => { /* best effort */ });
    return () => { try { child.kill(); } catch { /* already gone */ } };
  } catch {
    return () => {};
  }
}

/** Sleep/hibernate/resume events between two times, as a short description, or null. */
export function systemSuspendedBetween(start: Date, end: Date): string | null {
  if (process.platform !== "win32") return null;
  const fmt = (d: Date) => d.toISOString();
  const cmd =
    `Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=42,107; ` +
    `StartTime=[datetime]'${fmt(start)}'; EndTime=[datetime]'${fmt(end)}'} -ErrorAction SilentlyContinue | ` +
    `Sort-Object TimeCreated | ForEach-Object { '{0:o} {1}' -f $_.TimeCreated, $(if ($_.Id -eq 42) { 'sleep' } else { 'resume' }) }`;
  try {
    const r = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", cmd], {
      encoding: "utf8",
      windowsHide: true,
      timeout: 30_000,
    });
    const lines = (r.stdout || "").split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
    return lines.length ? lines.join(", ") : null;
  } catch {
    return null;
  }
}
