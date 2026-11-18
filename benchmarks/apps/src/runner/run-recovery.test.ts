import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { loadRunFromDisk } from "./config.js";

test("run-meta reconstructs interrupted and queued trials", () => {
  const dir = mkdtempSync(join(tmpdir(), "benchmark-run-"));
  try {
    writeFileSync(join(dir, "run-meta.json"), JSON.stringify({
      version: 2,
      timestamp: new Date().toISOString(),
      concurrency: 1,
      tasks: [
        { name: "calc-a", scenario: "calc", agent: "winui-dev-repo", model: "gpt-5.6-sol" },
        { name: "calc-b", scenario: "calc", agent: "winui-dev-repo", model: "gpt-5.6-sol" },
      ],
    }));
    mkdirSync(join(dir, "000_calc-a"));
    const loaded = loadRunFromDisk(dir);
    assert.equal(loaded.entries.length, 2);
    assert.equal(loaded.entries[0].status, "interrupted");
    assert.equal(loaded.entries[1].status, "queued");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("failed revalidation remains failed after reload", () => {
  const dir = mkdtempSync(join(tmpdir(), "benchmark-run-"));
  try {
    const trial = "000_calc-winui-dev-repo-main-gpt-i1";
    mkdirSync(join(dir, trial));
    writeFileSync(join(dir, trial, "results.json"), JSON.stringify({
      trial,
      scenario: "calc",
      condition: "winui-dev-repo@main",
      model: "gpt-5.6-sol",
      metrics: {
        score: 0,
        builds: true,
        runs: true,
      },
      fail_reason: "Validation timed out",
    }));

    const loaded = loadRunFromDisk(dir);
    assert.equal(loaded.entries.length, 1);
    assert.equal(loaded.entries[0].status, "failed");
    assert.equal(loaded.entries[0].failReason, "Validation timed out");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
