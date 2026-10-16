import assert from "node:assert/strict";
import test from "node:test";
import { mkdtempSync, rmSync } from "fs";
import { tmpdir } from "os";
import { join } from "path";
import { pinRef, readPin } from "./upstream-pins.js";

const A = "a".repeat(40);
const B = "b".repeat(40);

test("first resolution pins a branch; later trials reuse it even if the branch moved", () => {
  const dir = mkdtempSync(join(tmpdir(), "pins-"));
  try {
    assert.equal(readPin(dir, "microsoft/winappCli", "main"), undefined);
    assert.equal(pinRef(dir, "microsoft/winappCli", "main", A), A);
    assert.equal(pinRef(dir, "microsoft/winappCli", "main", B), A);
    assert.equal(readPin(dir, "Microsoft/WinAppCli", "main"), A);
    assert.equal(readPin(dir, "microsoft/winappCli", "other"), undefined);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("full SHAs are never pinned or remapped", () => {
  const dir = mkdtempSync(join(tmpdir(), "pins-"));
  try {
    assert.equal(pinRef(dir, "o/r", B, A), B);
    assert.equal(readPin(dir, "o/r", B), undefined);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
