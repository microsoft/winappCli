import { useEffect, useState } from "react";

/**
 * Current time that re-renders the calling component every `intervalMs`
 * while `enabled`. Keep it in the leaf components that show a time, so a
 * tick redraws one line instead of the whole dashboard.
 */
export function useNow(intervalMs: number, enabled: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [intervalMs, enabled]);
  return enabled ? now : Date.now();
}

/** `hh:mm:ss` for the run clock. */
export function formatClock(ms: number): string {
  const diff = Math.max(0, ms);
  const h = Math.floor(diff / 3600000);
  const m = Math.floor((diff % 3600000) / 60000);
  const s = Math.floor((diff % 60000) / 1000);
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
}

/** Run clock text: frozen at `end` once the run is finished, ticking otherwise. */
export function useRunClock(start: Date | null | undefined, end: number | null | undefined): string {
  const now = useNow(1000, !!start && end == null);
  if (!start) return "00:00:00";
  return formatClock((end ?? now) - start.getTime());
}
