import type { RunEntry } from "../types.js";
import { runBenchmark, type BenchmarkCallbacks } from "./benchmark.js";
import {
  appendFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  renameSync,
  statSync,
  writeFileSync,
} from "fs";
import { join } from "path";
import { spawnSync } from "child_process";
import { loadRunFromDisk } from "./config.js";
import { startKeepAwake, systemSuspendedBetween } from "./suspend-guard.js";

export interface QueueOptions {
  maxBuildMinutes: number;
  concurrency: number;
  /** Optional cap on `/continue` follow-ups per trial. Currently consumed by
   *  callers (sweep / desktop runner-bridge) for forward-compat; the queue
   *  itself doesn't read it yet. Declared here so call-site object literals
   *  don't trip TS2353 excess-property checks. */
  maxContinues?: number;
}

export interface QueueCallbacks {
  onOutput: (entryId: string, data: string) => void;
  onStatusChange: (entry: RunEntry) => void;
}

export class BenchmarkQueue {
  private entries: RunEntry[];
  private runDir: string;
  private options: QueueOptions;
  private callbacks: QueueCallbacks;
  private _running = false;
  private lastPersistedStatus = new Map<string, string>();
  /** Trials already retried once after a system suspend. */
  private retried = new Set<string>();

  constructor(
    entries: RunEntry[],
    runDir: string,
    options: QueueOptions,
    callbacks: QueueCallbacks
  ) {
    this.entries = entries;
    this.runDir = runDir;
    this.options = options;
    this.callbacks = callbacks;
  }

  get progress(): { current: number; total: number } {
    const completed = this.entries.filter((e) =>
      ["done", "failed", "interrupted", "timeout"].includes(e.status)
    ).length;
    return { current: completed, total: this.entries.length };
  }

  get isRunning(): boolean {
    return this._running;
  }

  async start(): Promise<void> {
    this._running = true;
    const stopKeepAwake = startKeepAwake();
    try {
      await this.runAll();
    } finally {
      stopKeepAwake();
    }
  }

  private async runAll(): Promise<void> {
    const concurrency = this.options.concurrency;
    let nextIndex = 0;

    const runNext = async (): Promise<void> => {
      while (nextIndex < this.entries.length && this._running) {
        const i = nextIndex++;
        const entry = this.entries[i];
        if (!(await this.waitForMemoryBudget())) {
          entry.status = "interrupted";
          entry.failReason = "Queue paused before Node heap exhaustion; restart and resume incomplete trials.";
          this.persistState(entry);
          this.callbacks.onStatusChange(entry);
          this._running = false;
          break;
        }
        entry.currentOutput = "";
        this.prepareFreshAttempt(entry);
        const trialDir = join(this.runDir, entry.trialName);
        const liveOutputPath = join(trialDir, "live-output.txt");
        const emit = (data: string) => {
          try { appendFileSync(liveOutputPath, data); } catch { /* UI still receives output */ }
          this.callbacks.onOutput(entry.id, data);
        };
        const notifyStatus = (updated: RunEntry) => {
          this.persistState(updated);
          this.callbacks.onStatusChange(updated);
        };
        emit(`\n${"═".repeat(60)}\n`);
        notifyStatus(entry);
        const trialStart = new Date();
        try {
          await runBenchmark(entry, this.runDir, this.options, {
            onOutput: emit,
            onStatusChange: notifyStatus,
          });
        } catch (err) {
          // Catch-all so an uncaught throw from runBenchmark still leaves a
          // post-mortem on disk (UI reload would otherwise show nothing).
          const msg = err instanceof Error
            ? `${err.message}\n${err.stack ?? ""}`
            : String(err);
          const banner = `\n❌ BENCHMARK UNCAUGHT (queue layer):\n${msg}\n`;
          try { emit(banner); } catch { /* never break the queue */ }

          // runBenchmark may have thrown before creating the trial dir.
          const trialDir = join(this.runDir, entry.trialName);
          const logsDir = join(trialDir, "session-logs-dir");
          try {
            if (!existsSync(trialDir)) mkdirSync(trialDir, { recursive: true });
            if (!existsSync(logsDir)) mkdirSync(logsDir, { recursive: true });
            const slPath = join(logsDir, "session-log.txt");
            appendFileSync(slPath, "\n\n--- BENCHMARK UNCAUGHT (queue layer) ---\n" + msg + "\n");
          } catch { /* best-effort */ }
          try {
            writeFileSync(
              join(trialDir, "results.json"),
              JSON.stringify({
                trial: entry.trialName,
                scenario: entry.scenarioConfigName,
                condition: entry.condition,
                model: entry.model,
                metrics: { score: 0, builds: false, runs: false, timeout: false },
                fail_reason: `runBenchmark threw: ${msg.split("\n")[0]}`,
                error_excerpt: msg.slice(0, 4000),
              }, null, 2)
            );
          } catch { /* best-effort */ }

          entry.status = "failed";
          entry.failReason = err instanceof Error ? err.message : String(err);
          notifyStatus(entry);
        }
        // A trial that spanned a system sleep/suspend (e.g. a VM hibernated by
        // its host overnight) timed out or lost its worker for reasons that
        // have nothing to do with the agent. Set its result aside, mark it
        // interrupted (so --resume-run picks it up), and retry it once now.
        const suspended = systemSuspendedBetween(trialStart, new Date());
        if (suspended) {
          const resultsPath = join(trialDir, "results.json");
          try { if (existsSync(resultsPath)) renameSync(resultsPath, join(trialDir, "results.suspended.json")); } catch { /* best effort */ }
          entry.status = "interrupted";
          entry.failReason = `System suspended during the trial (${suspended})`;
          emit(`\n⏸️  ${entry.failReason}. Result set aside; ${this.retried.has(entry.trialName) ? "resume the run to retry" : "retrying"}.\n`);
          notifyStatus(entry);
          if (!this.retried.has(entry.trialName) && this._running) {
            this.retried.add(entry.trialName);
            this.entries.push(entry);
          }
        }
        (globalThis as typeof globalThis & { gc?: () => void }).gc?.();
      }
    };

    // Launch N workers
    const workers = Array.from({ length: Math.min(concurrency, this.entries.length) }, () => runNext());
    await Promise.all(workers);

    // Aggregate iterations if multiple
    if (this._running) {
      const reportEntries = loadRunFromDisk(this.runDir).entries;
      this.aggregateIterations(reportEntries);
    }
    this._running = false;
  }

  private prepareFreshAttempt(entry: RunEntry): void {
    const trialDir = join(this.runDir, entry.trialName);
    if (!existsSync(trialDir)) {
      mkdirSync(trialDir, { recursive: true });
      return;
    }

    const children = readdirSync(trialDir);
    if (children.length === 0) return;

    const launchOutput = join(trialDir, "launch-output.txt");
    if (existsSync(launchOutput)) {
      try {
        const text = readFileSync(launchOutput, "utf8");
        const pid = Number(
          text.match(/"(?:ProcessId|processId|pid|Pid)"\s*:\s*(\d+)/)?.[1]
        );
        if (Number.isInteger(pid) && pid > 0 && this.isRecordedProcessStillCurrent(pid, launchOutput)) {
          process.kill(pid);
        }
      } catch {
        // The normal trial cleanup also removes apps; this is crash-recovery only.
      }
    }

    const attemptsRoot = join(this.runDir, ".attempts", entry.trialName);
    mkdirSync(attemptsRoot, { recursive: true });
    let attempt = 1;
    while (existsSync(join(attemptsRoot, String(attempt).padStart(3, "0")))) attempt++;
    const archiveDir = join(attemptsRoot, String(attempt).padStart(3, "0"));
    mkdirSync(archiveDir, { recursive: true });
    for (const child of children) {
      renameSync(join(trialDir, child), join(archiveDir, child));
    }
  }

  private isRecordedProcessStillCurrent(pid: number, launchOutput: string): boolean {
    if (process.platform !== "win32") return false;
    const result = spawnSync(
      "powershell.exe",
      [
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        `(Get-Process -Id ${pid} -ErrorAction Stop).StartTime.ToUniversalTime().ToString('o')`,
      ],
      { encoding: "utf8", windowsHide: true, timeout: 10_000 }
    );
    if (result.status !== 0) return false;
    const processStartedAt = Date.parse(result.stdout.trim());
    if (!Number.isFinite(processStartedAt)) return false;
    const launchRecordedAt = statSync(launchOutput).mtimeMs;
    return processStartedAt <= launchRecordedAt + 60_000
      && processStartedAt >= launchRecordedAt - 10 * 60_000;
  }

  private persistState(entry: RunEntry): void {
    if (this.lastPersistedStatus.get(entry.id) === entry.status) return;
    this.lastPersistedStatus.set(entry.id, entry.status);
    const trialDir = join(this.runDir, entry.trialName);
    mkdirSync(trialDir, { recursive: true });
    const path = join(trialDir, "state.json");
    const temp = `${path}.${process.pid}.tmp`;
    try {
      writeFileSync(temp, JSON.stringify({
        trial: entry.trialName,
        status: entry.status,
        updated_at: new Date().toISOString(),
      }, null, 2));
      renameSync(temp, path);
    } catch {
      // Results and live logs remain the recovery source if checkpointing fails.
    }
  }

  private async waitForMemoryBudget(): Promise<boolean> {
    const warningBytes = 1_500 * 1024 * 1024;
    for (let attempt = 0; attempt < 12; attempt++) {
      (globalThis as typeof globalThis & { gc?: () => void }).gc?.();
      if (process.memoryUsage().heapUsed < warningBytes) return true;
      await new Promise((resolve) => setTimeout(resolve, 5_000));
    }
    return process.memoryUsage().heapUsed < warningBytes;
  }

  private aggregateIterations(sourceEntries: RunEntry[]): void {
    // Group entries by scenario + base condition + model
    const groups = new Map<string, RunEntry[]>();
    for (const entry of sourceEntries) {
      if ((entry.totalIterations ?? 1) <= 1) continue;
      // Strip iteration label from condition for grouping
      const baseCondition = entry.condition.replace(/\s*\[\d+\/\d+\]$/, "");
      const key = `${entry.scenarioConfigName}|${baseCondition}|${entry.model}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(entry);
    }

    for (const [key, entries] of groups) {
      const completed = entries.filter(
        (e) => e.status === "done" && e.score != null
      );
      if (completed.length === 0) continue;

      const [scenarioName, baseCondition, model] = key.split("|");
      const avgScore = Math.round(
        completed.reduce((sum, e) => sum + (e.score ?? 0), 0) / completed.length
      );
      const scores = completed.map((e) => e.score ?? 0);
      const minScore = Math.min(...scores);
      const maxScore = Math.max(...scores);
      const buildRate = completed.filter((e) => e.builds).length / completed.length;
      const runRate = completed.filter((e) => e.runs).length / completed.length;

      // Write aggregated results.json at the run dir level
      const aggregatedPath = join(this.runDir, `${baseCondition}-${model.replace(/[^a-zA-Z0-9.\-]/g, "")}-aggregated.json`);

      const aggregated = {
        scenario: scenarioName,
        condition: baseCondition,
        model,
        iterations: entries.length,
        completed: completed.length,
        aggregated: {
          avg_score: avgScore,
          min_score: minScore,
          max_score: maxScore,
          scores,
          build_rate: `${Math.round(buildRate * 100)}%`,
          run_rate: `${Math.round(runRate * 100)}%`,
        },
        iteration_results: completed.map((e) => ({
          iteration: e.iteration,
          score: e.score,
          builds: e.builds,
          runs: e.runs,
          session_time: e.sessionTime,
          build_session_id: e.buildSessionId,
          validation_session_id: e.validationSessionId,
        })),
      };

      try {
        writeFileSync(
          aggregatedPath,
          JSON.stringify(aggregated, null, 2)
        );
      } catch {
        // Parent dir might not exist if all iterations failed early
      }
    }
  }

  stop(): void {
    this._running = false;
  }
}
