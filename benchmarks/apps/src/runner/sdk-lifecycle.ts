export type SdkCompletionReason =
  | "sdk_idle"
  | "terminal_turn"
  | "hard_timeout"
  | "session_error";

export interface SdkCompletion {
  reason: SdkCompletionReason;
  error?: string;
}

interface SessionLike {
  send(options: { prompt: string }): Promise<string>;
  on(handler: (event: any) => void): () => void;
}

export async function runUntilSdkCompletion(options: {
  session: SessionLike;
  prompt: string;
  timeoutMs: number;
  quiescenceMs?: number;
  onEvent: (event: any) => void;
  isTerminalMessage?: (event: any) => boolean;
}): Promise<SdkCompletion> {
  const { session, prompt, timeoutMs, onEvent } = options;
  const quiescenceMs = options.quiescenceMs ?? 5_000;
  const activeTools = new Set<string>();
  let terminalTurnId: string | null = null;
  let terminalTurnEnded = false;
  let settled = false;
  let hardTimer: NodeJS.Timeout | undefined;
  let quietTimer: NodeJS.Timeout | undefined;

  let resolveCompletion!: (completion: SdkCompletion) => void;
  const completion = new Promise<SdkCompletion>((resolve) => {
    resolveCompletion = resolve;
  });

  const finish = (result: SdkCompletion) => {
    if (settled) return;
    settled = true;
    if (hardTimer) clearTimeout(hardTimer);
    if (quietTimer) clearTimeout(quietTimer);
    resolveCompletion(result);
  };

  const cancelQuiescence = () => {
    if (quietTimer) clearTimeout(quietTimer);
    quietTimer = undefined;
  };

  const maybeStartQuiescence = () => {
    cancelQuiescence();
    if (!terminalTurnId || !terminalTurnEnded || activeTools.size > 0) return;
    quietTimer = setTimeout(
      () => finish({ reason: "terminal_turn" }),
      quiescenceMs
    );
  };

  const unsubscribe = session.on((event: any) => {
    onEvent(event);
    const type = String(event?.type ?? "");
    const data = event?.data ?? {};
    switch (type) {
      case "tool.execution_start":
        cancelQuiescence();
        if (data.toolCallId) activeTools.add(String(data.toolCallId));
        break;
      case "tool.execution_complete":
        if (data.toolCallId) activeTools.delete(String(data.toolCallId));
        maybeStartQuiescence();
        break;
      case "assistant.message":
        if (
          !event.agentId &&
          !data.parentToolCallId &&
          data.content &&
          (data.toolRequests?.length ?? 0) === 0 &&
          (options.isTerminalMessage?.(event) ?? true)
        ) {
          terminalTurnId = data.turnId != null ? String(data.turnId) : null;
          terminalTurnEnded = false;
          cancelQuiescence();
        }
        break;
      case "assistant.turn_start":
        if (event.agentId) break;
        if (terminalTurnId && String(data.turnId ?? "") !== terminalTurnId) {
          terminalTurnId = null;
          terminalTurnEnded = false;
          cancelQuiescence();
        }
        break;
      case "assistant.turn_end":
        if (event.agentId) break;
        if (terminalTurnId && (!data.turnId || String(data.turnId) === terminalTurnId)) {
          terminalTurnEnded = true;
          maybeStartQuiescence();
        }
        break;
      case "session.idle":
        finish({ reason: "sdk_idle" });
        break;
      case "session.error":
        finish({ reason: "session_error", error: data.message || "SDK session error" });
        break;
    }
  });

  try {
    hardTimer = setTimeout(() => finish({ reason: "hard_timeout" }), timeoutMs);
    await session.send({ prompt });
    return await completion;
  } finally {
    unsubscribe();
    if (hardTimer) clearTimeout(hardTimer);
    if (quietTimer) clearTimeout(quietTimer);
  }
}
