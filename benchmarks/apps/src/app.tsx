import React, { useState, useCallback, useEffect, useRef, useMemo } from "react";
import { Box, Text, useInput, useApp } from "ink";
import { exec } from "child_process";
import { platform } from "os";
import { StatusBar } from "./components/status-bar.js";
import { formatClock } from "./components/clock.js";
import { SetupView, type SetupResult } from "./views/setup.js";
import { LiveView } from "./views/live.js";
import { ProgressView } from "./views/progress.js";
import { ResultsView } from "./views/results.js";
import { ChartsView } from "./views/charts.js";
import { SummaryView } from "./views/summary.js";
import { BenchmarkQueue } from "./runner/queue.js";
import { revalidateBenchmark, runPreflight } from "./runner/benchmark.js";
import { generateHtmlReport } from "./runner/report.js";
import { aggregateEntries } from "./runner/aggregate.js";
import { getNextRunName, resultsRoot, loadRunFromDisk, discoverScenarios, discoverAgentSetups, validateFlatTasks } from "./runner/config.js";
import { makeFlatTrialName, sanitizeRefForId } from "./runner/format.js";
import { appendBounded, readFileTail } from "./runner/live-output.js";
import type { RunEntry, ViewName, FlatTask, RunMetaV2 } from "./types.js";
import { appendFileSync, mkdirSync, existsSync, readFileSync, writeFileSync } from "fs";
import { join } from "path";

/** Open a file or folder with the OS default handler (cross-platform). */
function shellOpen(target: string): void {
  const p = platform();
  if (p === "win32") exec(`explorer "${target}"`);
  else if (p === "darwin") exec(`open "${target}"`);
  else exec(`xdg-open "${target}"`);
}

interface Props {
  showResultsOnly?: boolean;
  runName?: string;
  maxBuildMinutes?: number;
  concurrency?: number;
  quickRun?: { scenario: string; agent: string; model: string };
}

export function App({ showResultsOnly, runName: initialRunName, maxBuildMinutes = 60, concurrency = 3, quickRun }: Props) {
  const { exit } = useApp();

  // Synchronously load run data when --results --run <name> is passed,
  // so the first render already has entries (avoids ink exiting on empty state).
  const initialLoad = (() => {
    if (showResultsOnly && initialRunName) {
      const runPath = join(resultsRoot, initialRunName);
      if (existsSync(runPath)) {
        const loaded = loadRunFromDisk(runPath);
        return { entries: loaded.entries, runName: loaded.runName, runPath };
      }
    }
    return null;
  })();

  const [view, setView] = useState<ViewName>(showResultsOnly ? "results" : "setup");
  const [entries, setEntries] = useState<RunEntry[]>(initialLoad?.entries || []);
  const [runName, setRunName] = useState(initialLoad?.runName || initialRunName || "");
  const [configuredMaxBuildMinutes, setConfiguredMaxBuildMinutes] = useState(maxBuildMinutes);
  const [configuredConcurrency, setConfiguredConcurrency] = useState(concurrency);
  const [startTime, setStartTime] = useState<Date | null>(null);
  const queueRef = useRef<BenchmarkQueue | null>(null);

  // Per-run output storage
  const outputMapRef = useRef<Map<string, string>>(new Map());

  // Set initial terminal title. Historical logs are loaded only when selected.
  useEffect(() => {
    process.stdout.write(`\x1b]0;Benchmark Dashboard\x07`);

    if (initialLoad) {
      const runPath = initialLoad.runPath;
      // Restore run settings from run-meta.json
      const metaPath = join(runPath, "run-meta.json");
      if (existsSync(metaPath)) {
        try {
          const meta = JSON.parse(readFileSync(metaPath, "utf-8"));
          // New v2 metas store the timeout only per-task; derive a run-level
          // value (max across tasks) to pre-fill the control. Fall back to an
          // early-v2 `defaults` block or a v1 top-level field for old runs.
          const taskMax = Array.isArray(meta.tasks)
            ? Math.max(0, ...meta.tasks.map((t: { maxBuildMinutes?: number }) => (typeof t.maxBuildMinutes === "number" ? t.maxBuildMinutes : 0)))
            : 0;
          const mbm = meta.defaults?.maxBuildMinutes ?? meta.maxBuildMinutes ?? (taskMax > 0 ? taskMax : undefined);
          if (typeof mbm === "number") setConfiguredMaxBuildMinutes(mbm);
          if (typeof meta.concurrency === "number") setConfiguredConcurrency(meta.concurrency);
        } catch {}
      }
    }
  }, []);

  const [selectedRunIndex, setSelectedRunIndex] = useState(0);
  const [scrollOffset, setScrollOffset] = useState(0);
  const [resultsCursorIndex, setResultsCursorIndex] = useState(0);
  // Counter to force re-renders when output changes
  const [outputVersion, setOutputVersion] = useState(0);
  const [memory, setMemory] = useState(() => {
    const usage = process.memoryUsage();
    return {
      heapMb: Math.round(usage.heapUsed / 1024 / 1024),
      rssMb: Math.round(usage.rss / 1024 / 1024),
      visibleKb: 0,
    };
  });
  const [preflightFailure, setPreflightFailure] = useState("");
  const selectedRunIdRef = useRef<string | null>(null);
  const viewRef = useRef<ViewName>(view);
  const pendingOutputRef = useRef("");
  const outputFlushTimerRef = useRef<NodeJS.Timeout | null>(null);

  const flushVisibleOutput = useCallback(() => {
    outputFlushTimerRef.current = null;
    const entryId = selectedRunIdRef.current;
    const data = pendingOutputRef.current;
    pendingOutputRef.current = "";
    if (!entryId || !data) return;
    const previous = outputMapRef.current.get(entryId) || "";
    outputMapRef.current.clear();
    outputMapRef.current.set(entryId, appendBounded(previous, data));
    if (viewRef.current === "live") setOutputVersion(v => v + 1);
  }, []);

  const storeVisibleOutput = useCallback((entryId: string, data: string) => {
    if (entryId !== selectedRunIdRef.current) return;
    pendingOutputRef.current = appendBounded(pendingOutputRef.current, data);
    if (!outputFlushTimerRef.current) {
      outputFlushTimerRef.current = setTimeout(flushVisibleOutput, 2_000);
    }
  }, [flushVisibleOutput]);

  useEffect(() => () => {
    if (outputFlushTimerRef.current) clearTimeout(outputFlushTimerRef.current);
  }, []);

  useEffect(() => {
    viewRef.current = view;
    if (view === "live") setOutputVersion(v => v + 1);
  }, [view]);

  useEffect(() => {
    const timer = setInterval(() => {
      const usage = process.memoryUsage();
      const entryId = selectedRunIdRef.current;
      if (viewRef.current === "live") {
        setMemory({
          heapMb: Math.round(usage.heapUsed / 1024 / 1024),
          rssMb: Math.round(usage.rss / 1024 / 1024),
          visibleKb: Math.round((entryId ? outputMapRef.current.get(entryId)?.length || 0 : 0) / 1024),
        });
      }
      if (runName) {
        try {
          appendFileSync(
            join(resultsRoot, runName, "memory.jsonl"),
            JSON.stringify({
              timestamp: new Date().toISOString(),
              heapUsed: usage.heapUsed,
              heapTotal: usage.heapTotal,
              rss: usage.rss,
              external: usage.external,
            }) + "\n"
          );
        } catch {}
      }
    }, 60_000);
    return () => clearInterval(timer);
  }, [runName]);

  useEffect(() => {
    const selected = entries[selectedRunIndex];
    const entryId = selected?.id ?? null;
    selectedRunIdRef.current = entryId;
    pendingOutputRef.current = "";
    outputMapRef.current.clear();
    if (!selected || !runName) {
      setOutputVersion(v => v + 1);
      return;
    }

    const flatDir = join(resultsRoot, runName, selected.trialName);
    const nestedDir = join(resultsRoot, runName, selected.scenarioConfigName, selected.trialName);
    const trialDir = existsSync(flatDir) ? flatDir : nestedDir;
    const candidates = [
      join(trialDir, "live-output.txt"),
      join(trialDir, "session-logs-dir", "session-log.txt"),
      join(trialDir, "session-log.txt"),
    ];
    const logFile = candidates.find(existsSync);
    if (logFile) outputMapRef.current.set(entryId, readFileTail(logFile));
    setOutputVersion(v => v + 1);
  }, [selectedRunIndex, runName]);

  // Once every trial has finished, freeze the run clock at the last finish
  // time so it shows the real run duration.
  const runEnd = useMemo(() => {
    if (entries.length === 0) return null;
    if (!entries.every(e => ["done", "failed", "interrupted", "timeout"].includes(e.status))) return null;
    const last = entries.reduce((max, e) => Math.max(max, e.finishedAt?.getTime() ?? 0), 0);
    return last > 0 ? last : null;
  }, [entries]);

  // Terminal title + Windows Terminal progress ring. The on-screen clocks tick
  // inside StatusBar/ProgressView/LiveView (components/clock.ts).
  useEffect(() => {
    if (!startTime) return;
    const timer = setInterval(() => {
      const completed = entries.filter(e => ["done", "failed", "interrupted", "timeout"].includes(e.status)).length;
      const active = entries.find(e => !["queued", "done", "failed", "interrupted", "timeout"].includes(e.status));
      const total = entries.length;
      const allDone = total > 0 && completed === total;
      const timeStr = formatClock((runEnd ?? Date.now()) - startTime.getTime());
      // Update terminal title + Windows Terminal progress ring
      let title: string;
      if (allDone) {
        title = `✅ Benchmark [${completed}/${total}] ${timeStr}`;
        // Clear progress ring
        process.stdout.write(`\x1b]9;4;0;0\x07`);
      } else if (active) {
        const runElapsed = active.startedAt
          ? `${Math.floor((Date.now() - active.startedAt.getTime()) / 60000)}m`
          : "";
        title = `Benchmark [${completed}/${total}] ${runElapsed ? runElapsed + " " : ""}— ${active.condition}`;
        // Set progress ring (indeterminate = state 3)
        process.stdout.write(`\x1b]9;4;3;0\x07`);
      } else {
        title = `Benchmark [${completed}/${total}] ${timeStr}`;
        process.stdout.write(`\x1b]9;4;3;0\x07`);
      }
      process.stdout.write(`\x1b]0;${title}\x07`);
    }, 30_000);
    return () => {
      clearInterval(timer);
      // Clear progress ring on unmount
      process.stdout.write(`\x1b]9;4;0;0\x07`);
    };
  }, [startTime, entries, runEnd]);

  // Track which run is currently active (for auto-follow)
  const activeRunIndex = entries.findIndex(e =>
    !["queued", "done", "failed", "interrupted", "timeout"].includes(e.status)
  );

  // Keyboard navigation
  useInput((input, key) => {
    // Skip all app-level key handling during setup — setup view manages its own input
    if (view === "setup") {
      if (input === "q") exit();
      return;
    }

    // View switching (reset scroll when changing views)
    if (input === "1") { setView("live"); setScrollOffset(0); }
    else if (input === "2") { setView("progress"); setScrollOffset(0); }
    else if (input === "3") { setView("results"); setScrollOffset(0); }
    else if (input === "4") { setView("charts"); setScrollOffset(0); }
    else if (input === "5") { setView("summary"); setScrollOffset(0); }
    else if (input === "b") { setView("setup"); setScrollOffset(0); }
    else if (input === "q") exit();
    else if (key.tab) {
      const views: ViewName[] = ["live", "progress", "results", "charts", "summary"];
      const idx = views.indexOf(view);
      setView(views[(idx + 1) % views.length]);
      setScrollOffset(0);
    }

    // Scroll with ↑↓ (skip when progress owns keys — it has its own virtual scroll)
    const noneActive = entries.length > 0 && !entries.some(e => ["setup", "building", "build_done", "dotnet_build", "launching", "validating", "retrospective"].includes(e.status));
    const progressOwnsKeys = view === "progress" && noneActive && !queueRef.current?.isRunning;
    const resultsUsesArrows = view === "results";

    if (!progressOwnsKeys && !resultsUsesArrows) {
      if (key.upArrow) {
        setScrollOffset(prev => prev + 3);
      } else if (key.downArrow) {
        setScrollOffset(prev => Math.max(0, prev - 3));
      }
    }

    // Results view: ↑↓ for row selection
    if (resultsUsesArrows) {
      if (key.upArrow) {
        setResultsCursorIndex(prev => Math.max(0, prev - 1));
      } else if (key.downArrow) {
        setResultsCursorIndex(prev => prev + 1); // clamped in ResultsView
      }
    }
    // Don't modify scrollOffset when progress view owns keys (it has its own virtual scroll)
    if (!progressOwnsKeys) {
      if (key.pageUp || input === "[") {
        const pageSize = (process.stdout.rows || 30) - 8;
        setScrollOffset(prev => prev + pageSize);
      } else if (key.pageDown || input === "]") {
        const pageSize = (process.stdout.rows || 30) - 8;
        setScrollOffset(prev => Math.max(0, prev - pageSize));
      } else if (input === "e") {
        setScrollOffset(0); // End — jump to bottom
      } else if (input === "h") {
        // Home — jump to top
        const totalLines = (outputMapRef.current.get(entries[selectedRunIndex]?.id || "") || "").split("\n").length;
        setScrollOffset(totalLines);
      }
    }

    // Live view specific: run selection with ←→
    if (view === "live" && entries.length > 0) {
      if (key.leftArrow) {
        setSelectedRunIndex(prev => Math.max(0, prev - 1));
        setScrollOffset(0);
      } else if (key.rightArrow) {
        setSelectedRunIndex(prev => Math.min(entries.length - 1, prev + 1));
        setScrollOffset(0);
      }
      // 'f' = follow active run
      else if (input === "f") {
        if (activeRunIndex >= 0) {
          setSelectedRunIndex(activeRunIndex);
          setScrollOffset(0);
        }
      }
      // 'o' = open trial folder in explorer
      else if (input === "o") {
        const selected = entries[selectedRunIndex];
        if (selected && runName) {
          // Try flat structure first (new), then nested (old runs)
          const flatFolder = join(resultsRoot, runName, selected.trialName);
          const nestedFolder = join(resultsRoot, runName, selected.scenarioConfigName, selected.trialName);
          const runFolder = join(resultsRoot, runName);
          const folderToOpen = existsSync(flatFolder) ? flatFolder
            : existsSync(nestedFolder) ? nestedFolder
            : runFolder;
          shellOpen(folderToOpen);
        } else if (runName) {
          shellOpen(join(resultsRoot, runName));
        }
      }
    }

    // Results view: O = open selected scenario's result folder
    if (view === "results" && input === "o" && runName) {
      const agg = aggregateEntries(entries);
      const selected = agg[resultsCursorIndex];
      if (selected && selected.entries.length > 0) {
        // Pick first iteration entry
        const first = selected.entries.reduce((a, b) => (a.iteration ?? 1) <= (b.iteration ?? 1) ? a : b);
        const flatFolder = join(resultsRoot, runName, first.trialName);
        const nestedFolder = join(resultsRoot, runName, first.scenarioConfigName, first.trialName);
        const runFolder = join(resultsRoot, runName);
        const folderToOpen = existsSync(flatFolder) ? flatFolder
          : existsSync(nestedFolder) ? nestedFolder
          : runFolder;
        shellOpen(folderToOpen);
      } else {
        shellOpen(join(resultsRoot, runName));
      }
    }

    // O = open run folder (other non-live views)
    if (view !== "live" && view !== "results" && input === "o" && runName) {
      shellOpen(join(resultsRoot, runName));
    }

    // H = generate HTML report and open it (skip when progress view uses h/H for navigation)
    if (input === "h" && runName && !progressOwnsKeys) {
      const rd = join(resultsRoot, runName);
      const reportPath = generateHtmlReport(entries, rd);
      shellOpen(reportPath);
    }
  }, { isActive: view !== "setup" });

  // Auto-follow: when a new run starts, select it (unless user has manually selected a different one)
  const prevActiveRef = useRef(-1);
  useEffect(() => {
    if (activeRunIndex >= 0 && activeRunIndex !== prevActiveRef.current) {
      prevActiveRef.current = activeRunIndex;
      setSelectedRunIndex(activeRunIndex);
      setScrollOffset(0);
    }
  }, [activeRunIndex]);

  const handleSetupComplete = useCallback((config: SetupResult) => {
    // Handle loaded run
    if (config.loadRunPath) {
      const loaded = loadRunFromDisk(config.loadRunPath);
      setRunName(loaded.runName);
      setEntries(loaded.entries);
      setStartTime(null);

      outputMapRef.current.clear();

      // Restore run settings from run-meta.json
      const metaPath = join(config.loadRunPath, "run-meta.json");
      if (existsSync(metaPath)) {
        try {
          const meta = JSON.parse(readFileSync(metaPath, "utf-8"));
          // New v2 metas store the timeout only per-task; derive a run-level
          // value (max across tasks) to pre-fill the control. Fall back to an
          // early-v2 `defaults` block or a v1 top-level field for old runs.
          const taskMax = Array.isArray(meta.tasks)
            ? Math.max(0, ...meta.tasks.map((t: { maxBuildMinutes?: number }) => (typeof t.maxBuildMinutes === "number" ? t.maxBuildMinutes : 0)))
            : 0;
          const mbm = meta.defaults?.maxBuildMinutes ?? meta.maxBuildMinutes ?? (taskMax > 0 ? taskMax : undefined);
          if (typeof mbm === "number") setConfiguredMaxBuildMinutes(mbm);
          if (typeof meta.concurrency === "number") setConfiguredConcurrency(meta.concurrency);
        } catch {}
      }

      setView("results");
      return;
    }

    setPreflightFailure("");
    const preflight = runPreflight(
      config.conditions.map((condition) => ({
        name: condition.name,
        pluginPath: condition.pluginPath,
      }))
    );
    if (!preflight.ok) {
      setPreflightFailure(
        preflight.failures.map((failure) => `[${failure.agent}] ${failure.label}`).join("\n")
      );
      return;
    }

    const name = getNextRunName();
    setRunName(name);
    setConfiguredMaxBuildMinutes(config.maxBuildMinutes);
    setConfiguredConcurrency(config.concurrency);
    // Per-task `skipValidation` (below) is authoritative, but mirror it to the
    // env so any code path that consults SKIP_VALIDATION stays consistent for
    // this run. Clear it otherwise so a prior skipped run doesn't leak.
    if (config.skipValidation) process.env.SKIP_VALIDATION = "1";
    else delete process.env.SKIP_VALIDATION;

    // Build the flat task list — interleave iterations so all variants run iter1
    // first, then iter2, etc. This gives early comparison data across variants.
    const iters = config.iterations || 1;
    const multiCond = config.conditions.length > 1;
    const multiModel = config.models.length > 1;
    const multiIter = iters > 1;
    const tasks: FlatTask[] = [];
    for (let iter = 1; iter <= iters; iter++) {
      for (const scenario of config.scenarios) {
        for (const cond of config.conditions) {
          for (const model of config.models) {
            // Derive a readable task name. The NNN_ index prefix guarantees a
            // unique folder, so this only needs to be human-legible: add the
            // agent / ref / model / iteration tokens that are actually varying.
            let taskName = scenario.name;
            if (multiCond) taskName += `-${cond.name}`;
            if (cond.upstreamRef) taskName += `-${sanitizeRefForId(cond.upstreamRef)}`;
            if (multiModel) taskName += `-${model}`;
            if (multiIter) taskName += `-i${iter}`;
            tasks.push({
              name: taskName,
              scenario: scenario.name,
              agent: cond.name,
              model,
              maxBuildMinutes: config.maxBuildMinutes,
              upstreamRef: cond.upstreamRef ?? null,
              skipValidation: config.skipValidation || undefined,
              iteration: iter,
              totalIterations: iters,
            });
          }
        }
      }
    }

    // Validate the whole task list before creating the run — collect every
    // problem and abort if any. The wizard always produces valid tasks, so this
    // is a safety net for future direct-task paths.
    const taskErrors = validateFlatTasks(tasks, {
      scenarioNames: new Set(discoverScenarios().map(s => s.name)),
      agentNames: new Set(discoverAgentSetups().map(a => a.name)),
    });
    if (taskErrors.length > 0) {
      console.error(`[app] run-meta validation failed — ${taskErrors.length} problem(s):`);
      for (const e of taskErrors) console.error(`  • ${e}`);
      return;
    }

    const runDir = join(resultsRoot, name);
    mkdirSync(runDir, { recursive: true });

    // Save run metadata (flat v2). Top level is whole-list controls only
    // (`concurrency`); `tasks[]` is the single source of truth — no `defaults`
    // block. The distinct scenario/agent/model lists + trial count are derived
    // from it on read, not stored here.
    const runMeta: RunMetaV2 = {
      version: 2,
      timestamp: new Date().toISOString(),
      concurrency: config.concurrency,
      tasks,
    };
    writeFileSync(join(runDir, "run-meta.json"), JSON.stringify(runMeta, null, 2));

    // Resolve each task to a RunEntry. Folder = `{NNN}_{name}`.
    const scenarioPathByName = new Map(config.scenarios.map(s => [s.name, s.path]));
    const pluginPathByCond = new Map(config.conditions.map(c => [`${c.name}@${c.upstreamRef ?? ""}`, c.pluginPath]));
    const newEntries: RunEntry[] = tasks.map((t, i) => {
      const trialName = makeFlatTrialName(i, t.name);
      const iterLabel = (t.totalIterations ?? 1) > 1 ? ` [${t.iteration}/${t.totalIterations}]` : "";
      const condLabel = t.upstreamRef
        ? `${t.agent}@${t.upstreamRef}${iterLabel}`
        : t.agent + iterLabel;
      return {
        id: trialName,
        name: t.name,
        scenario: t.scenario,
        scenarioPath: scenarioPathByName.get(t.scenario) || "",
        scenarioConfigName: t.scenario,
        condition: condLabel,
        pluginPath: pluginPathByCond.get(`${t.agent}@${t.upstreamRef ?? ""}`) || "",
        model: t.model ?? config.models[0],
        trialName,
        maxBuildMinutes: t.maxBuildMinutes,
        skipValidation: t.skipValidation,
        iteration: t.iteration ?? 1,
        totalIterations: t.totalIterations ?? 1,
        status: "queued",
        currentOutput: "",
        ...(t.upstreamRef ? { upstreamRef: t.upstreamRef } : {}),
      };
    });

    setEntries(newEntries);
    setStartTime(new Date());
    setView("live");

    // Start the queue
    const queue = new BenchmarkQueue(
      newEntries,
      runDir,
      { maxBuildMinutes: config.maxBuildMinutes, concurrency: config.concurrency },
      {
        onOutput: storeVisibleOutput,
        onStatusChange: (entry) => {
          setEntries(prev => prev.map(e => e.id === entry.id ? { ...entry } : e));
        }
      }
    );
    queueRef.current = queue;
    queue.start().catch((err) => {
      console.error("Queue error:", err);
    });
  }, []);

  // Quick-run mode: auto-start with CLI args (--scenario X --agent Y --model Z)
  useEffect(() => {
    if (!quickRun) return;
    const allScenarios = discoverScenarios();
    const allAgents = discoverAgentSetups();
    const scenario = allScenarios.find(s => s.name === quickRun.scenario);
    const agent = allAgents.find(a => a.name === quickRun.agent);
    if (!scenario) { console.error(`Scenario not found: ${quickRun.scenario}`); return; }
    if (!agent) { console.error(`Agent not found: ${quickRun.agent}`); return; }
    handleSetupComplete({
      scenarios: [scenario],
      conditions: [{ name: agent.name, pluginPath: agent.path }],
      models: [quickRun.model],
      concurrency: 1,
      iterations: 1,
      maxBuildMinutes,
    });
  }, []);

  const handleRerun = useCallback((entryIds: string[]) => {
    const runDir = join(resultsRoot, runName);
    if (!existsSync(runDir)) mkdirSync(runDir, { recursive: true });

    // Reset selected entries to queued
    setEntries(prev => prev.map(e =>
      entryIds.includes(e.id)
        ? { ...e, status: "queued" as const, score: undefined, builds: undefined, runs: undefined,
            sessionTime: undefined, apiTime: undefined, inputTokens: undefined, outputTokens: undefined,
            cachedTokens: undefined, failReason: undefined, startedAt: undefined, finishedAt: undefined,
            currentOutput: "" }
        : e
    ));

    // Clear output for rerun entries
    outputMapRef.current.clear();

    setStartTime(new Date());
    setView("live");

    // Get the actual entry objects for the queue
    const rerunEntries = entries.filter(e => entryIds.includes(e.id)).map(e => ({
      ...e, status: "queued" as const, score: undefined, builds: undefined, runs: undefined,
      sessionTime: undefined, apiTime: undefined, inputTokens: undefined, outputTokens: undefined,
      cachedTokens: undefined, failReason: undefined, startedAt: undefined, finishedAt: undefined,
      currentOutput: "",
    }));

    const queue = new BenchmarkQueue(
      rerunEntries,
      runDir,
      { maxBuildMinutes: configuredMaxBuildMinutes, concurrency: configuredConcurrency },
      {
        onOutput: storeVisibleOutput,
        onStatusChange: (entry) => {
          setEntries(prev => prev.map(e => e.id === entry.id ? { ...entry } : e));
        }
      }
    );
    queueRef.current = queue;
    queue.start().catch((err) => {
      console.error("Rerun queue error:", err);
    });
  }, [entries, runName, configuredMaxBuildMinutes, configuredConcurrency]);

  const handleRevalidate = useCallback(async (entryIds: string[]) => {
    const runDir = join(resultsRoot, runName);

    // Reset selected entries
    setEntries(prev => prev.map(e =>
      entryIds.includes(e.id)
        ? { ...e, status: "queued" as const, score: undefined, builds: undefined, runs: undefined,
            failReason: undefined, startedAt: undefined, finishedAt: undefined, currentOutput: "" }
        : e
    ));
    outputMapRef.current.clear();
    setStartTime(new Date());
    setView("live");

    // Run revalidation sequentially for selected entries
    const revalEntries = entries.filter(e => entryIds.includes(e.id)).map(e => ({
      ...e, status: "queued" as const, score: undefined, builds: undefined, runs: undefined,
      failReason: undefined, startedAt: undefined, finishedAt: undefined, currentOutput: "",
    }));

    for (const entry of revalEntries) {
      try {
        await revalidateBenchmark(entry, runDir, {
          onOutput: (data) => storeVisibleOutput(entry.id, data),
          onStatusChange: (e) => {
            setEntries(prev => prev.map(p => p.id === e.id ? { ...e } : p));
          }
        });
      } catch {
        // The failed result is persisted by revalidateBenchmark; continue the batch.
      }
    }
  }, [entries, runName]);

  // Get selected run and its output
  const selectedRun = entries[selectedRunIndex];
  const selectedOutput = selectedRun ? (outputMapRef.current.get(selectedRun.id) || "") : "";

  const currentRun = entries.find(e => !["queued", "done", "failed", "interrupted", "timeout"].includes(e.status));
  const completedCount = entries.filter(e => ["done", "failed", "interrupted", "timeout"].includes(e.status)).length;
  const progress = {
    // While a task is running, show it as the "current" one (completed + 1);
    // once everything is finished there is no in-progress task, so cap at total.
    current: Math.min(completedCount + (currentRun ? 1 : 0), entries.length),
    total: entries.length
  };

  if (view === "setup") {
    return (
      <Box flexDirection="column">
        {preflightFailure && (
          <Box borderStyle="single" borderColor="red" paddingX={1}>
            <Text color="red">Preflight failed. Run /winui-setup, then retry.{"\n"}{preflightFailure}</Text>
          </Box>
        )}
        <SetupView onComplete={handleSetupComplete} />
      </Box>
    );
  }

  return (
    <Box flexDirection="column" height={process.stdout.rows || 40}>
      <StatusBar
        currentRun={currentRun}
        progress={progress}
        runStart={startTime}
        runEnd={runEnd}
        activeView={view}
        runName={runName}
        memory={memory}
      />
      {view === "live" && (
        <LiveView
          selectedRun={selectedRun}
          output={selectedOutput}
          scrollOffset={scrollOffset}
          runIndex={selectedRunIndex}
          totalRuns={entries.length}
        />
      )}
      <Box flexDirection="column" overflow="hidden" flexGrow={1} marginTop={view !== "live" && view !== "progress" ? -scrollOffset : 0}>
        {view === "progress" && <ProgressView entries={entries} runName={runName} runStart={startTime} runEnd={runEnd} isRunning={queueRef.current?.isRunning} onRerun={handleRerun} onRevalidate={handleRevalidate} />}
        {view === "results" && <ResultsView entries={entries} runDir={runName ? join(resultsRoot, runName) : undefined} cursorIndex={resultsCursorIndex} onCursorClamp={(max) => { if (resultsCursorIndex > max) setResultsCursorIndex(max); }} />}
        {view === "charts" && <ChartsView entries={entries} />}
        {view === "summary" && <SummaryView entries={entries} runDir={runName ? join(resultsRoot, runName) : undefined} />}
      </Box>
      <Box paddingX={1}>
        <Text color="gray">
          {"1-5 or Tab: views | ↑↓ scroll"}
          {view === "live" ? " | F: follow" : ""}
          {" | O: open folder | H: HTML report | B: back | Q: quit"}
        </Text>
      </Box>
    </Box>
  );
}
