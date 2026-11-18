import assert from "node:assert/strict";
import test from "node:test";
import { evaluateConcurrentProcessCommand } from "./process-safety.js";

test("allows explicit numeric PID termination", () => {
  assert.equal(
    evaluateConcurrentProcessCommand("Stop-Process -Id 1234 -Force -ErrorAction SilentlyContinue").allowed,
    true
  );
  assert.equal(
    evaluateConcurrentProcessCommand("Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue").allowed,
    true
  );
  assert.equal(
    evaluateConcurrentProcessCommand("taskkill.exe /PID 1234 /T /F").allowed,
    true
  );
  assert.equal(
    evaluateConcurrentProcessCommand("taskkill /PID $AppPid /F").allowed,
    true
  );
});

test("rejects process-name and pipeline termination", () => {
  for (const command of [
    "Stop-Process -Name CounterApp -Force",
    "Get-Process CounterApp | Stop-Process -Force",
    "Get-Process | Where-Object ProcessName -like '*Counter*' | ForEach-Object { Stop-Process -Id $_.Id }",
    "taskkill.exe /IM CounterApp.exe /F",
    "taskkill /FI \"IMAGENAME eq CounterApp.exe\" /F",
    "Get-Process CounterApp | ForEach-Object { taskkill /PID $_.Id /F }",
    "$AppPid = (Get-Process CounterApp).Id; taskkill /PID $AppPid /F",
  ]) {
    assert.equal(evaluateConcurrentProcessCommand(command).allowed, false, command);
  }
});
