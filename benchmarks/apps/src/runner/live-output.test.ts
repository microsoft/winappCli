import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { appendBounded, readFileTail } from "./live-output.js";

test("appendBounded retains only the newest text", () => {
  assert.equal(appendBounded("12345", "67890", 6), "567890");
  assert.equal(appendBounded("", "abcdefgh", 4), "efgh");
});

test("readFileTail does not load the complete file", () => {
  const dir = mkdtempSync(join(tmpdir(), "benchmark-tail-"));
  try {
    const path = join(dir, "log.txt");
    writeFileSync(path, "a".repeat(10_000) + "TAIL");
    assert.equal(readFileTail(path, 4), "TAIL");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
