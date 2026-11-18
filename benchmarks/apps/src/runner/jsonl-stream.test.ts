import assert from "node:assert/strict";
import test from "node:test";
import { mkdtempSync, rmSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import { join } from "path";
import { readLinesSync } from "./jsonl-stream.js";

test("readLinesSync keeps multi-byte characters split across reads", () => {
  const root = mkdtempSync(join(tmpdir(), "jsonl-"));
  try {
    const file = join(root, "events.jsonl");
    writeFileSync(file, '{"m":"café"}\n{"m":"日本"}\n', "utf8");
    assert.deepEqual([...readLinesSync(file, 1)], ['{"m":"café"}', '{"m":"日本"}']);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

