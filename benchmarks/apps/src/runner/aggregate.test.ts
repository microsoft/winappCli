import assert from "node:assert/strict";
import test from "node:test";
import { formatDurationText, parseDurationText } from "./aggregate.js";

test("session time is averaged, not picked", () => {
  const secs = ["1m 0s", "9m 0s"].map((t) => parseDurationText(t)!);
  assert.equal(formatDurationText(secs.reduce((a, b) => a + b, 0) / secs.length), "5m 0s");
  assert.equal(parseDurationText("1h 2m 3s"), 3723);
  assert.equal(parseDurationText(undefined), undefined);
});
