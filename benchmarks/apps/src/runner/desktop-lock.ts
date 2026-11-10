/**
 * Locked-desktop guard for the UI checks. When the Windows session is locked
 * (or on a secure desktop such as UAC), `winapp ui` can read the UI tree but
 * cannot send input, so the launch-level and keyboard checks measure nothing.
 * Such a trial is a harness error, not an app result.
 */
import { spawnSync } from "child_process";
import type { QualityCheck } from "./quality-checks.js";

export const DESKTOP_LOCKED_ERROR = "Desktop locked: UI could not be driven";

/** Stop the run after this many trials in a row could not drive the UI. */
export const LOCKED_DESKTOP_STREAK_LIMIT = 3;

/**
 * True when a window holds the foreground. Windows reports no foreground window
 * while the session is locked, which is the same test `winapp ui` uses before input.
 */
export function probeInteractiveDesktop(): boolean {
  if (process.platform !== "win32") return true;
  const script = [
    "$sig='[DllImport(\"user32.dll\")] public static extern System.IntPtr GetForegroundWindow();'",
    "$u=Add-Type -MemberDefinition $sig -Name Fg -Namespace WinDevBench -PassThru",
    "[int64]$u::GetForegroundWindow()",
  ].join("; ");
  try {
    const r = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
      encoding: "utf8",
      windowsHide: true,
      timeout: 30_000,
    });
    const value = Number((r.stdout || "").trim().split(/\r?\n/).pop());
    // An unreadable answer is not evidence of a lock.
    return r.status !== 0 || !Number.isFinite(value) || value !== 0;
  } catch {
    return true;
  }
}

/** Polls `probe` until it reports a usable desktop or `timeoutMs` passes. */
export async function waitForInteractiveDesktop(
  probe: () => boolean,
  opts: { timeoutMs?: number; intervalMs?: number; log?: (msg: string) => void; sleep?: (ms: number) => Promise<void> } = {},
): Promise<boolean> {
  const timeoutMs = opts.timeoutMs ?? 10 * 60_000;
  const intervalMs = opts.intervalMs ?? 30_000;
  const sleep = opts.sleep ?? ((ms: number) => new Promise<void>((r) => setTimeout(r, ms)));
  if (probe()) return true;
  opts.log?.(`  ⏸️  The desktop is locked; waiting up to ${Math.round(timeoutMs / 60_000)} min for it to unlock before the UI checks.`);
  for (let waited = 0; waited < timeoutMs; waited += intervalMs) {
    await sleep(intervalMs);
    if (probe()) {
      opts.log?.("  ▶️  Desktop unlocked; continuing.");
      return true;
    }
  }
  return false;
}

/** True when the UI checks could not send input because the desktop was locked. */
export function uiBlockedByLockedDesktop(checks: QualityCheck[], desktopAvailableNow: boolean): boolean {
  const l2 = checks.find((c) => c.id === "L2");
  if (!l2 || l2.status !== "na") return false;
  if (/desktop locked/i.test(l2.detail ?? "")) return true;
  return /desktop busy/i.test(l2.detail ?? "") && !desktopAvailableNow;
}

/** Counts trials in a row that hit a locked desktop; `record` returns true once the limit is reached. */
export class LockedDesktopStreak {
  private count = 0;
  constructor(private readonly limit = LOCKED_DESKTOP_STREAK_LIMIT) {}
  /** `locked` true: the trial hit a locked desktop. false: the trial drove the UI. undefined: no UI checks ran. */
  record(locked: boolean | undefined): boolean {
    if (locked === true) this.count++;
    else if (locked === false) this.count = 0;
    return this.count >= this.limit;
  }
  get current(): number { return this.count; }
}
