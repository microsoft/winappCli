import assert from "node:assert/strict";
import test from "node:test";
import { runUntilSdkCompletion } from "./sdk-lifecycle.js";

class FakeSession {
  private handlers = new Set<(event: any) => void>();
  constructor(private readonly events: any[]) {}
  on(handler: (event: any) => void) {
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }
  async send() {
    queueMicrotask(() => {
      for (const event of this.events) {
        for (const handler of this.handlers) handler(event);
      }
    });
    return "message";
  }
}

test("terminal no-tool turn completes without session.idle", async () => {
  const session = new FakeSession([
    {
      type: "assistant.message",
      data: { content: "Done", toolRequests: [], turnId: "75" },
    },
    { type: "assistant.turn_end", data: { turnId: "75" } },
  ]);
  const result = await runUntilSdkCompletion({
    session,
    prompt: "build",
    timeoutMs: 1_000,
    quiescenceMs: 5,
    onEvent: () => {},
  });
  assert.equal(result.reason, "terminal_turn");
});

test("tool-request turn does not complete early", async () => {
  const session = new FakeSession([
    {
      type: "assistant.message",
      data: { content: "Working", toolRequests: [{ name: "task" }], turnId: "1" },
    },
    { type: "assistant.turn_end", data: { turnId: "1" } },
  ]);
  const result = await runUntilSdkCompletion({
    session,
    prompt: "build",
    timeoutMs: 10,
    quiescenceMs: 1,
    onEvent: () => {},
  });
  assert.equal(result.reason, "hard_timeout");
});

test("sub-agent final messages do not complete the root session", async () => {
  const session = new FakeSession([
    {
      type: "assistant.message",
      agentId: "subagent-1",
      data: { content: "Sub-agent done", toolRequests: [], turnId: "2" },
    },
    { type: "assistant.turn_end", agentId: "subagent-1", data: { turnId: "2" } },
  ]);
  const result = await runUntilSdkCompletion({
    session,
    prompt: "build",
    timeoutMs: 10,
    quiescenceMs: 1,
    onEvent: () => {},
  });
  assert.equal(result.reason, "hard_timeout");
});

test("terminal predicate rejects progress sentinels", async () => {
  const session = new FakeSession([
    {
      type: "assistant.message",
      data: { content: "{\"status\":\"waiting\"}", toolRequests: [], turnId: "8" },
    },
    { type: "assistant.turn_end", data: { turnId: "8" } },
  ]);
  const result = await runUntilSdkCompletion({
    session,
    prompt: "validate",
    timeoutMs: 10,
    quiescenceMs: 1,
    onEvent: () => {},
    isTerminalMessage: (event) => String(event.data.content).includes("project_score"),
  });
  assert.equal(result.reason, "hard_timeout");
});