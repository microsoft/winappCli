export interface ProcessSafetyDecision {
  allowed: boolean;
  reason?: string;
}

export function evaluateConcurrentProcessCommand(command: string): ProcessSafetyDecision {
  if (/\btaskkill(?:\.exe)?\b/i.test(command)) {
    const exactPidTaskkill =
      /^\s*taskkill(?:\.exe)?\s+\/PID\s+(?:\d+|\$AppPid)(?:\s+\/(?:F|T))*\s*$/i;
    if (exactPidTaskkill.test(command)) return { allowed: true };
    return {
      allowed: false,
      reason:
        "Concurrent benchmark trials may only use taskkill with one literal numeric /PID or $AppPid. " +
        "Image-name, filter, pipeline, variable-derived, and compound commands can terminate peer trials.",
    };
  }

  if (!/\bStop-Process\b/i.test(command)) return { allowed: true };

  const exactPidStop =
    /^\s*Stop-Process\s+-Id\s+(?:\d+(?:\s*,\s*\d+)*|\$AppPid)(?:\s+-(?:Force|ErrorAction\s+\w+))*\s*$/i;
  if (exactPidStop.test(command)) return { allowed: true };
  return {
    allowed: false,
    reason:
      "Concurrent benchmark trials may only use Stop-Process with literal numeric -Id values or $AppPid. " +
      "Name, wildcard, pipeline, variable-derived, and compound commands can terminate peer trials.",
  };
}

export function extractCommand(toolArgs: unknown): string {
  if (typeof toolArgs === "string") return toolArgs;
  if (!toolArgs || typeof toolArgs !== "object") return "";
  const command = (toolArgs as { command?: unknown }).command;
  return typeof command === "string" ? command : "";
}
