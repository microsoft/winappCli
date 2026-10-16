import React, { useState, useEffect, useCallback } from "react";
import { Box, Text, useInput } from "ink";
import SelectInput from "ink-select-input";
import TextInput from "ink-text-input";
import figures from "figures";
import { join } from "path";
import { discoverScenarios, discoverAgentSetups, discoverRuns, loadRunMatrix, getJudgeModel } from "../runner/config.js";
import { discoverAvailableModels, type AvailableModel } from "../runner/models.js";
import { listRefs, type RefInfo } from "../runner/repo-cache.js";

interface Props {
  onComplete: (config: SetupResult) => void;
}

export interface SetupResult {
  scenarios: Array<{ name: string; path: string }>;
  conditions: Array<{ name: string; pluginPath: string; upstreamRef?: string }>;
  models: string[];
  concurrency: number;
  iterations: number;
  maxBuildMinutes: number;
  skipValidation?: boolean; // Skip the LLM-judge step (build/launch only, no score)
  loadRunPath?: string; // If set, load this run instead of starting a new one
}

type SetupStep = "mode" | "loadRun" | "rerun" | "loadFile" | "scenarios" | "agents" | "models" | "concurrency" | "iterations" | "timeout" | "validation" | "confirm";

// ANSI color helpers (used by both buildScenarioItems and the rendered SelectInput
// labels — moved to module scope so they're shared and unambiguous).
const ansi = {
  dim: (s: string) => `\x1b[90m${s}\x1b[39m`,
  bold: (s: string) => `\x1b[1m${s}\x1b[22m`,
  cyan: (s: string) => `\x1b[36m${s}\x1b[39m`,
  magenta: (s: string) => `\x1b[35m${s}\x1b[39m`,
  green: (s: string) => `\x1b[32m${s}\x1b[39m`,
  yellow: (s: string) => `\x1b[33m${s}\x1b[39m`,
};

// A SelectInput variant that does NOT wrap at list boundaries. Up at the
// first item stays at the first item; Down at the last item stays at the
// last item. ink-select-input's built-in `arrayToRotated` makes the list
// circular, which is surprising when there's a terminal "Done" sentinel —
// reaching it should feel like the end, not a U-turn back to the top.
//
// Implementation choice: own the absolute cursor index and the scroll window
// here, rather than monkey-patching the upstream component (no extension
// point exists). Keeps the same Item/Indicator visual language as upstream.
type NonWrapItem = { label: string; value: string };
function NonWrappingSelectInput({
  items,
  limit,
  isFocused = true,
  initialIndex = 0,
  onSelect,
  onHighlight,
}: {
  items: NonWrapItem[];
  limit?: number;
  isFocused?: boolean;
  /** Row the cursor starts on (e.g. skip a "Continue" row at the top). */
  initialIndex?: number;
  onSelect?: (item: NonWrapItem) => void;
  onHighlight?: (item: NonWrapItem) => void;
}) {
  const [absIdx, setAbsIdx] = useState(Math.min(initialIndex, Math.max(0, items.length - 1)));
  const maxIdx = Math.max(0, items.length - 1);
  const windowSize = limit && limit < items.length ? limit : items.length;

  // Clamp cursor when the underlying list shrinks across renders (e.g. when
  // toggling a group changes label text but not item count — safe no-op).
  useEffect(() => {
    if (absIdx > maxIdx) setAbsIdx(maxIdx);
  }, [maxIdx, absIdx]);

  // Scroll window — try to keep the cursor mid-window, anchored at the
  // ends so the visible range never extends beyond `items`.
  const scrollStart = Math.max(
    0,
    Math.min(items.length - windowSize, absIdx - Math.floor(windowSize / 2))
  );
  const visible = items.slice(scrollStart, scrollStart + windowSize);
  const cursorInWindow = absIdx - scrollStart;

  // Notify the parent whenever the highlighted absolute item changes.
  useEffect(() => {
    if (items[absIdx]) onHighlight?.(items[absIdx]);
    // intentionally not depending on `items` or `onHighlight` so we don't
    // fire on every re-render — only when the cursor actually moves.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [absIdx]);

  useInput(
    useCallback(
      (input: string, key: { upArrow?: boolean; downArrow?: boolean; return?: boolean }) => {
        if (key.upArrow || input === "k") {
          if (absIdx > 0) setAbsIdx(absIdx - 1);
        } else if (key.downArrow || input === "j") {
          if (absIdx < maxIdx) setAbsIdx(absIdx + 1);
        } else if (key.return || input === " ") {
          const it = items[absIdx];
          if (it) onSelect?.(it);
        } else if (/^[1-9]$/.test(input)) {
          // Number-key jump targets the currently-visible window, mirroring
          // upstream SelectInput behaviour.
          const targetVisibleIdx = parseInt(input, 10) - 1;
          if (targetVisibleIdx < visible.length) {
            const it = visible[targetVisibleIdx];
            if (it) onSelect?.(it);
          }
        }
      },
      [absIdx, maxIdx, items, visible, onSelect]
    ),
    { isActive: isFocused }
  );

  return (
    <Box flexDirection="column">
      {visible.map((item, i) => {
        const isSel = i === cursorInWindow;
        return (
          <Box key={item.value}>
            <Box marginRight={1}>
              <Text color={isSel ? "blue" : undefined}>{isSel ? figures.pointer : " "}</Text>
            </Box>
            <Text color={isSel ? "blue" : undefined}>{item.label}</Text>
          </Box>
        );
      })}
    </Box>
  );
}

export function SetupView({ onComplete }: Props) {
  const [step, setStep] = useState<SetupStep>("mode");
  const [selectedScenarios, setSelectedScenarios] = useState<Set<string>>(new Set());
  const [selectedAgents, setSelectedAgents] = useState<Set<string>>(new Set());
  const [selectedModels, setSelectedModels] = useState<Set<string>>(new Set());
  const [availableModels, setAvailableModels] = useState<AvailableModel[]>([]);
  const [modelsLoading, setModelsLoading] = useState(true);
  const [modelsError, setModelsError] = useState<string | null>(null);
  const [concurrency, setConcurrency] = useState(3);
  const [iterations, setIterations] = useState(1);
  const [jsonPath, setJsonPath] = useState("");
  const [loadError, setLoadError] = useState<string | null>(null);
  const [maxBuildMinutes, setMaxBuildMinutes] = useState(60);
  const [skipValidation, setSkipValidation] = useState(false);
  // from_repo support: cache of available branches per repo, plus per-repo
  // loading / error state. Selected agent values use the form
  // ``agent@ref`` for from_repo agents (one selected entry per ref), and just
  // ``agent`` for everything else.
  const [refCache, setRefCache] = useState<Map<string, RefInfo[]>>(new Map());
  const [refLoading, setRefLoading] = useState<Set<string>>(new Set());
  const [refLoadError, setRefLoadError] = useState<Map<string, string>>(new Map());
  const [needSelection, setNeedSelection] = useState(false);

  const scenarios = discoverScenarios();
  const agents = discoverAgentSetups();

  const loadModels = useCallback(async () => {
    setModelsLoading(true);
    setModelsError(null);
    try {
      const models = await discoverAvailableModels();
      if (models.length === 0) {
        throw new Error("Copilot returned no available models.");
      }
      setAvailableModels(models);
      setSelectedModels(prev => new Set([...prev].filter(model => models.some(m => m.id === model))));
    } catch (error) {
      setAvailableModels([]);
      setModelsError(error instanceof Error ? error.message : String(error));
    } finally {
      setModelsLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadModels();
  }, [loadModels]);

  // Flat, one line per scenario: "● name  short description", with the
  // description cut to fit the terminal width.
  const buildScenarioItems = (): Array<{ label: string; value: string; selectable: boolean }> => {
    const termCols = process.stdout.columns ?? 100;
    const nameWidth = Math.max(...scenarios.map(s => s.name.length), 10);
    return [...scenarios]
      .sort((a, b) => a.name.localeCompare(b.name))
      .map((s) => {
        const name = s.name.padEnd(nameWidth);
        // padding(2) + cursor(2) + marker(2) + name + gap(2) + safety(2)
        const room = Math.max(0, termCols - 10 - nameWidth);
        const raw = (s.config?.description ?? "").replace(/\s+/g, " ").trim();
        const desc = raw.length > room ? `${raw.slice(0, Math.max(0, room - 1))}…` : raw;
        return {
          label: `${name}  ${ansi.dim(desc)}`,
          value: s.name,
          selectable: true,
        };
      });
  };

  /** Apply a loaded matrix to pre-populate selections. Returns error message or null. */
  const applyMatrix = (filePath: string): string | null => {
    const matrix = loadRunMatrix(filePath);
    if (!matrix) return `Could not load matrix from: ${filePath}`;

    const availableScenarioNames = new Set(scenarios.map(s => s.name));
    const availableAgentNames = new Set(agents.map(a => a.name));
    if (modelsLoading) return "Available models are still loading. Try again in a moment.";
    if (modelsError) return `Could not load available models: ${modelsError}`;

    const availableModelNames = new Set(availableModels.map(model => model.id));

    const validScenarios = matrix.scenarios.filter(s => availableScenarioNames.has(s));
    const validAgents = matrix.agents.filter(a => availableAgentNames.has(a));
    const validModels = matrix.models.filter(m => availableModelNames.has(m));

    const warnings: string[] = [];
    const missingScenarios = matrix.scenarios.filter(s => !availableScenarioNames.has(s));
    const missingAgents = matrix.agents.filter(a => !availableAgentNames.has(a));
    const missingModels = matrix.models.filter(m => !availableModelNames.has(m));
    if (missingScenarios.length) warnings.push(`Skipped scenarios (not found): ${missingScenarios.join(", ")}`);
    if (missingAgents.length) warnings.push(`Skipped agents (not found): ${missingAgents.join(", ")}`);
    if (missingModels.length) warnings.push(`Skipped models (not available): ${missingModels.join(", ")}`);

    if (validScenarios.length === 0 && validAgents.length === 0 && validModels.length === 0) {
      return `No valid selections found. ${warnings.join(". ")}`;
    }

    setSelectedScenarios(new Set(validScenarios));
    setSelectedAgents(new Set(validAgents));
    setSelectedModels(new Set(validModels));
    setConcurrency(Math.max(1, Math.min(5, matrix.concurrency)));
    setIterations(matrix.iterations);
    setLoadError(warnings.length > 0 ? warnings.join(". ") : null);
    return null;
  };

  // Setups whose ref the user picks: from_repo without a catalog. (A catalog
  // setup's ref is a catalog version in another repo; it always uses its default.)
  const fromRepoAgents = () => agents.filter(a => !!a.config?.from_repo && !a.config.from_repo.catalog);

  // Kick off ref-list fetches (open PRs + branches) on mount, so by the time
  // the user reaches the "agents" step the list is already there.
  useEffect(() => {
    for (const a of fromRepoAgents()) {
      const repo = a.config!.from_repo!.repo;
      if (refCache.has(repo) || refLoading.has(repo)) continue;
      setRefLoading(prev => { const n = new Set(prev); n.add(repo); return n; });
      listRefs(repo, ["branches", "prs"])
        .then((refs) => {
          setRefCache(prev => { const n = new Map(prev); n.set(repo, refs); return n; });
          setRefLoading(prev => { const n = new Set(prev); n.delete(repo); return n; });
        })
        .catch((e) => {
          setRefLoadError(prev => { const n = new Map(prev); n.set(repo, e?.message || String(e)); return n; });
          setRefLoading(prev => { const n = new Set(prev); n.delete(repo); return n; });
        });
    }
    // Intentionally run once on mount; ``agents`` is stable.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /**
   * Build the flat list of selectable items for the agents step, kept short:
   *
   *   none
   *   production
   *   winappcli main
   *   PRs:
   *     #1234 Title (author)
   *
   * Plain and catalog setups are one row each. A ref-picking setup gets its
   * default ref plus its repo's open pull requests (newest first). Branches,
   * tags and SHAs are available through headless runs (`--agent name@ref`).
   */
  const buildAgentItems = (): Array<{ label: string; value: string; selectable: boolean }> => {
    const items: Array<{ label: string; value: string; selectable: boolean }> = [];
    const gray = (s: string) => `${"\x1b[90m"}${s}${"\x1b[39m"}`;
    const byNewest = (x: RefInfo, y: RefInfo) =>
      (y.committedAt ? Date.parse(y.committedAt) : 0) - (x.committedAt ? Date.parse(x.committedAt) : 0);
    const pickers = agents.filter(a => !!a.config?.from_repo && !a.config.from_repo.catalog);
    for (const a of agents) {
      if (pickers.includes(a)) continue;
      items.push({ label: a.name, value: a.name, selectable: true });
    }
    for (const a of pickers) {
      const fr = a.config!.from_repo!;
      const defaultRef = fr.default_ref || "main";
      items.push({ label: `${a.name} ${defaultRef}`, value: `${a.name}@${defaultRef}`, selectable: true });
      const refs = refCache.get(fr.repo);
      const err = refLoadError.get(fr.repo);
      const prs = (refs ?? []).filter(r => r.kind === "pr").sort(byNewest);
      const status = refLoading.has(fr.repo) ? gray("  (loading…)") : err ? `  ${"\x1b[31m"}(couldn't list PRs: ${err})${"\x1b[39m"}` : prs.length ? "" : gray("  (none open)");
      items.push({ label: `PRs${pickers.length > 1 ? ` (${a.name})` : ""}:${status}`, value: `__hdr__${a.name}`, selectable: false });
      for (const pr of prs) {
        const title = (pr.title ?? "").length > 70 ? `${pr.title!.slice(0, 69)}…` : (pr.title ?? "");
        items.push({
          label: `#${pr.number} ${title}${pr.author ? `  ${gray(`(${pr.author}${pr.draft ? ", draft" : ""})`)}` : ""}`,
          value: `${a.name}@${pr.name}`,
          selectable: true,
        });
      }
    }
    return items;
  };
  // Advance from a multi-select step; needs at least one selection.
  const nextStep: Partial<Record<SetupStep, SetupStep>> = { scenarios: "agents", agents: "models", models: "concurrency" };
  const prevStep: Partial<Record<SetupStep, SetupStep>> = { scenarios: "mode", agents: "scenarios", models: "agents" };
  const selectionCount = (): number =>
    step === "scenarios" ? selectedScenarios.size : step === "agents" ? selectedAgents.size : selectedModels.size;
  const advance = () => {
    const next = nextStep[step];
    if (!next) return;
    if (selectionCount() === 0) {
      setNeedSelection(true);
      return;
    }
    setNeedSelection(false);
    setStep(next);
  };

  // Select-all, continue (D / Tab / →) and back (←) shortcuts.
  useInput((input, key) => {
    if (!nextStep[step]) return;
    if (input === "a") {
      setNeedSelection(false);
      if (step === "scenarios") {
        const allSelected = selectedScenarios.size === scenarios.length;
        setSelectedScenarios(allSelected ? new Set() : new Set(scenarios.map(s => s.name)));
      } else if (step === "agents") {
        const leafValues = buildAgentItems().filter(i => i.selectable).map(i => i.value);
        const allSelected = leafValues.every(v => selectedAgents.has(v)) && leafValues.length > 0;
        setSelectedAgents(allSelected ? new Set() : new Set(leafValues));
      } else if (step === "models") {
        const allSelected = selectedModels.size === availableModels.length;
        setSelectedModels(allSelected ? new Set() : new Set(availableModels.map(model => model.id)));
      }
    } else if (input === "d" || key.tab || key.rightArrow) {
      advance();
    } else if (key.leftArrow) {
      setNeedSelection(false);
      setStep(prevStep[step]!);
    }
  });

  /**
   * Checklist used by the scenarios, agents and models steps. Row 0 is
   * "Continue", so the user never has to scroll to the end to move on; the
   * cursor starts on the first real item. Rows whose value starts with
   * `__hdr__` are headings and can't be toggled.
   */
  const CONTINUE = "__continue__";
  const renderChecklist = (
    items: Array<{ label: string; value: string; selectable?: boolean }>,
    selected: Set<string>,
    onToggle: (value: string) => void,
    noun: string,
  ) => {
    const termRows = process.stdout.rows ?? 30;
    const continueLabel = selected.size > 0
      ? `${ansi.bold(ansi.green("Continue →"))} ${ansi.dim(`(${selected.size} selected)`)}`
      : ansi.dim("Continue →  (select at least one)");
    const rows = [
      { label: continueLabel, value: CONTINUE },
      ...items.map(i => ({
        label: i.selectable === false
          ? `  ${i.label}`
          : `${selected.has(i.value) ? ansi.green("●") : ansi.dim("○")} ${i.label}`,
        value: i.value,
      })),
    ];
    return (
      <Box flexDirection="column">
        <Text color="gray">  ↑↓ move · Space/Enter toggle · A all · D/Tab/→ continue · ← back</Text>
        <NonWrappingSelectInput
          key={step}
          limit={Math.max(5, termRows - 9)}
          initialIndex={1}
          items={rows}
          onSelect={(item) => {
            if (item.value === CONTINUE) advance();
            else if (!item.value.startsWith("__hdr__")) {
              setNeedSelection(false);
              onToggle(item.value);
            }
          }}
        />
        {needSelection && <Text color="yellow">  Select at least one {noun.replace(/s$/, "")} to continue.</Text>}
      </Box>
    );
  };

  const toggleIn = (set: Set<string>, setter: (s: Set<string>) => void) => (v: string) => {
    const next = new Set(set);
    next.has(v) ? next.delete(v) : next.add(v);
    setter(next);
  };
  if (step === "mode") {
    const runs = discoverRuns();
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">🏁 Benchmark Dashboard</Text>
        <Box marginTop={1}>
          <SelectInput
            items={[
              { label: "▶ New benchmark run", value: "new" },
              ...(runs.length > 0 ? [
                { label: `📂 Benchmark run status (${runs.length} available)`, value: "load" },
                { label: `🔁 Rerun previous matrix`, value: "rerun" },
              ] : []),
            ]}
            onSelect={(item) => {
              if (item.value === "new") setStep("scenarios");
              else if (item.value === "load") setStep("loadRun");
              else if (item.value === "rerun") setStep("rerun");
            }}
          />
        </Box>
      </Box>
    );
  }

  if (step === "loadRun") {
    const runs = discoverRuns();
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Select a run to load:</Text>
        <Box marginTop={1} flexDirection="column">
          <SelectInput
            items={[
              ...runs.slice(0, 20).map(r => ({
                label: `${r.name}  (${r.date.toLocaleDateString()} ${r.date.toLocaleTimeString()})`,
                value: r.path,
              })),
              { label: "← Back", value: "__back__" },
            ]}
            onSelect={(item) => {
              if (item.value === "__back__") {
                setStep("mode");
              } else {
                onComplete({
                  scenarios: [],
                  conditions: [],
                  models: [],
                  concurrency: 1,
                  iterations: 1,
                  maxBuildMinutes: 60,
                  loadRunPath: item.value,
                });
              }
            }}
          />
        </Box>
      </Box>
    );
  }

  if (step === "rerun") {
    const runs = discoverRuns();
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Select a run to rerun:</Text>
        <Text color="gray">  The matrix will be loaded and you can modify it before starting.</Text>
        <Box marginTop={1} flexDirection="column">
          <SelectInput
            items={[
              ...runs.slice(0, 20).map(r => ({
                label: `${r.name}  (${r.date.toLocaleDateString()} ${r.date.toLocaleTimeString()})`,
                value: r.path,
              })),
              { label: "📄 Load from JSON file", value: "__file__" },
              { label: "← Back", value: "__back__" },
            ]}
            onSelect={(item) => {
              if (item.value === "__back__") {
                setStep("mode");
              } else if (item.value === "__file__") {
                setJsonPath(""); setLoadError(null); setStep("loadFile");
              } else {
                const metaPath = join(item.value, "run-meta.json");
                const err = applyMatrix(metaPath);
                if (err) {
                  setLoadError(err);
                  setStep("mode");
                } else {
                  setStep("confirm");
                }
              }
            }}
          />
        </Box>
      </Box>
    );
  }

  if (step === "loadFile") {
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Enter path to matrix JSON file:</Text>
        <Text color="gray">  File should have: scenarios, agents, models, concurrency, iterations</Text>
        <Box marginTop={1}>
          <Text color="green">{'> '}</Text>
          <TextInput
            value={jsonPath}
            onChange={setJsonPath}
            onSubmit={(value) => {
              const trimmed = value.trim().replace(/^["']|["']$/g, "");
              const err = applyMatrix(trimmed);
              if (err) {
                setLoadError(err);
              } else {
                setStep("confirm");
              }
            }}
          />
        </Box>
        {loadError && (
          <Box marginTop={1}>
            <Text color="red">Error: {loadError}</Text>
          </Box>
        )}
        <Box marginTop={1}>
          <Text color="gray">  Press Enter to load, or Ctrl+C to cancel</Text>
        </Box>
      </Box>
    );
  }

  if (step === "scenarios") {
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Select Scenarios</Text>
        {renderChecklist(buildScenarioItems(), selectedScenarios, toggleIn(selectedScenarios, setSelectedScenarios), "scenarios")}
      </Box>
    );
  }
  if (step === "agents") {
    if (agents.length === 0) {
      return (
        <Box flexDirection="column" padding={1}>
          <Text bold color="red">No agents found.</Text>
          <Text color="gray">Add agent folders to agents/ with a config.json,</Text>
          <Text color="gray">or add an experiment under agents/.local/.</Text>
          <Text> </Text>
          <Text color="gray">Example: agents/my-agent/config.json</Text>
          <Text color="gray">{'{'} "description": "My agent", "preset_scripts": ["run-dotnetnew-winui"], ... {'}'}</Text>
        </Box>
      );
    }
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Select Agents to Benchmark</Text>
        <Text color="gray">  Pick the baselines and any pull requests to compare.</Text>
        {renderChecklist(buildAgentItems(), selectedAgents, toggleIn(selectedAgents, setSelectedAgents), "setups")}
      </Box>
    );
  }


  if (step === "models") {
    if (modelsLoading) {
      return (
        <Box flexDirection="column" padding={1}>
          <Text bold color="cyan">Select Models:</Text>
          <Text color="gray">Loading available models from Copilot...</Text>
        </Box>
      );
    }

    if (modelsError) {
      return (
        <Box flexDirection="column" padding={1}>
          <Text bold color="red">Could not load available models.</Text>
          <Text color="gray">{modelsError}</Text>
          <Box marginTop={1}>
            <SelectInput
              items={[
                { label: "Retry", value: "retry" },
                { label: "← Back", value: "back" },
              ]}
              onSelect={(item) => {
                if (item.value === "retry") void loadModels();
                else setStep("agents");
              }}
            />
          </Box>
        </Box>
      );
    }

    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Select Models</Text>
        {renderChecklist(
          availableModels.map(model => ({
            label: model.name === model.id ? model.id : `${model.name} (${model.id})`,
            value: model.id,
          })),
          selectedModels,
          toggleIn(selectedModels, setSelectedModels),
          "models"
        )}
      </Box>
    );
  }

  if (step === "concurrency") {
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Parallel runs (default: 3):</Text>
        <Box marginTop={1}>
          <SelectInput
            items={[
              { label: "1 (sequential)", value: "1" },
              { label: "2", value: "2" },
              { label: "3 (default)", value: "3" },
              { label: "4", value: "4" },
              { label: "5", value: "5" },
            ]}
            initialIndex={2}
            onSelect={(item) => {
              setConcurrency(parseInt(item.value));
              setStep("iterations");
            }}
          />
        </Box>
      </Box>
    );
  }

  if (step === "iterations") {
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Iterations per run (results get averaged):</Text>
        <Box marginTop={1}>
          <SelectInput
            items={[
              { label: "1 (no repeat)", value: "1" },
              { label: "2", value: "2" },
              { label: "3 (recommended for variance)", value: "3" },
              { label: "5", value: "5" },
            ]}
            onSelect={(item) => {
              setIterations(parseInt(item.value));
              setStep("timeout");
            }}
          />
        </Box>
      </Box>
    );
  }

  if (step === "timeout") {
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Build timeout (minutes per trial):</Text>
        <Box marginTop={1}>
          <SelectInput
            items={[
              { label: "30 minutes", value: "30" },
              { label: "45 minutes", value: "45" },
              { label: "60 minutes (default)", value: "60" },
              { label: "90 minutes", value: "90" },
              { label: "120 minutes", value: "120" },
            ]}
            initialIndex={2}
            onSelect={(item) => {
              setMaxBuildMinutes(parseInt(item.value));
              setStep("validation");
            }}
          />
        </Box>
      </Box>
    );
  }

  if (step === "validation") {
    return (
      <Box flexDirection="column" padding={1}>
        <Text bold color="cyan">Scoring / validation:</Text>
        <Text color="gray">  Skip to only build + launch each trial (no LLM-judge score). Useful</Text>
        <Text color="gray">  for sweeps, or flat runs that do their own scoring as separate tasks.</Text>
        <Box marginTop={1}>
          <SelectInput
            items={[
              { label: "Run validation (LLM-judge scores each trial) — default", value: "validate" },
              { label: "Skip validation (build + launch only, no score)", value: "skip" },
            ]}
            initialIndex={skipValidation ? 1 : 0}
            onSelect={(item) => {
              setSkipValidation(item.value === "skip");
              setStep("confirm");
            }}
          />
        </Box>
      </Box>
    );
  }

  // Confirm step — selectedAgents now contains either ``name`` (plain agent)
  // or ``name@ref`` (one entry per ref for from_repo agents). Expand both
  // forms into the final condition list.
  const expandedConditions: Array<{ name: string; pluginPath: string; upstreamRef?: string }> = [];
  // Map of agent name → list of refs selected (for the confirm summary display).
  const refsByAgent = new Map<string, string[]>();
  for (const sel of selectedAgents) {
    const atIdx = sel.indexOf("@");
    if (atIdx > 0) {
      const name = sel.slice(0, atIdx);
      const ref = sel.slice(atIdx + 1);
      const agent = agents.find(a => a.name === name);
      if (!agent) continue;
      expandedConditions.push({ name, pluginPath: agent.path, upstreamRef: ref });
      const cur = refsByAgent.get(name) || [];
      cur.push(ref);
      refsByAgent.set(name, cur);
    } else {
      const agent = agents.find(a => a.name === sel);
      if (!agent) continue;
      if (agent.config?.from_repo) {
        // Bare agent name selected for a from_repo agent → use default_ref.
        const ref = agent.config.from_repo.default_ref || "main";
        expandedConditions.push({ name: sel, pluginPath: agent.path, upstreamRef: ref });
        const cur = refsByAgent.get(sel) || [];
        cur.push(ref);
        refsByAgent.set(sel, cur);
      } else {
        expandedConditions.push({ name: sel, pluginPath: agent.path });
      }
    }
  }
  // Stable ordering for the summary: agents in discovery order.
  const selectedAgentItems = agents.filter(a =>
    [...selectedAgents].some(sel => sel === a.name || sel.startsWith(a.name + "@"))
  );
  const totalRuns = selectedScenarios.size * expandedConditions.length * selectedModels.size;
  const totalWithIter = totalRuns * iterations;

  return (
    <Box flexDirection="column" padding={1}>
      <Text bold color="cyan">Confirm Benchmark Matrix:</Text>
      {loadError && (
        <Box marginTop={1}>
          <Text color="yellow">⚠ {loadError}</Text>
        </Box>
      )}
      <Box flexDirection="column" marginTop={1}>
        <Text>  Scenarios:   {[...selectedScenarios].join(", ")}</Text>
        <Text>  Agents:</Text>
        {selectedAgentItems.map(c => {
          const refs = refsByAgent.get(c.name);
          const refStr = refs && refs.length > 0 ? refs.join(", ") : null;
          return (
            <Text key={c.name} color="white">
              {"    • "}{c.name}
              {refStr ? <Text color="cyan">  @ {refStr}</Text> : null}
              {c.config?.description ? <Text color="gray">  {c.config.description}</Text> : null}
            </Text>
          );
        })}
        <Text>  Models:      {[...selectedModels].join(", ")}</Text>
        <Text>  Parallel:    {concurrency}</Text>
        <Text>  Timeout:     {maxBuildMinutes} minutes per trial</Text>
        <Text>  Iterations:  {iterations}{iterations > 1 ? " (results averaged)" : ""}</Text>
        <Text>  Validation:  {skipValidation ? <Text color="yellow">skipped (build + launch only, no score)</Text> : `LLM judge (${getJudgeModel()}) scores each trial`}</Text>
        <Text bold color="yellow">  Total: {totalWithIter} runs ({totalRuns} unique × {iterations} iter, ~{Math.round(totalWithIter * 30 / 60 / concurrency)}-{Math.round(totalWithIter * 45 / 60 / concurrency)}h with {concurrency} parallel)</Text>
      </Box>
      <Box marginTop={1}>
        <SelectInput
          items={[
            { label: "▶ Start benchmark", value: "start" },
            { label: "← Edit scenarios", value: "scenarios" },
            { label: "← Edit agents", value: "agents" },
            { label: "← Edit models", value: "models" },
            { label: "← Edit parallel/iterations", value: "concurrency" },
            { label: "← Edit validation", value: "validation" },
            { label: "← Back to start", value: "mode" },
          ]}
          onSelect={(item) => {
            if (item.value === "start") {
              onComplete({
                scenarios: scenarios.filter(s => selectedScenarios.has(s.name)).map(s => ({ name: s.name, path: s.path })),
                conditions: expandedConditions,
                models: [...selectedModels],
                concurrency,
                iterations,
                maxBuildMinutes,
                skipValidation
              });
            } else {
              setStep(item.value as SetupStep);
            }
          }}
        />
      </Box>
    </Box>
  );
}
