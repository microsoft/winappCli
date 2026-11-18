import { runCopilotSdkSession } from "./benchmark.js";

function sendAndDisconnect(message: unknown): void {
  if (!process.send) {
    process.disconnect?.();
    return;
  }
  process.send(message, () => process.disconnect?.());
}

process.on("message", async (message: any) => {
  if (message?.type === "ping") {
    sendAndDisconnect({ type: "pong" });
    return;
  }
  if (message?.type !== "run") return;
  try {
    const result = await runCopilotSdkSession({
      ...message.options,
      onOutput: (data: string) => process.send?.({ type: "output", data }),
      onTokenUpdate: (data) => process.send?.({ type: "tokens", data }),
    });
    sendAndDisconnect({ type: "result", data: result });
  } catch (error) {
    sendAndDisconnect({
      type: "error",
      error: error instanceof Error ? `${error.message}\n${error.stack || ""}` : String(error),
    });
  }
});
