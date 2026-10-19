import { existsSync, readFileSync, readdirSync, writeFileSync, statSync } from "fs";
import { join, basename } from "path";
import type { RunEntry } from "../types.js";
import { expandAgentName } from "./format.js";
import { scenariosDir } from "./config.js";

interface ReqResult {
  text: string;
  passed: boolean;
  reason: string;
}

interface ResearchQuery {
  query: string;
  source: string;
  found: string;
  useful: string;
  issue: string | null;
}

interface FailedApi {
  api: string;
  origin: string;
  reason: string;
  discovery: string;
  alternative: string | null;
}

interface RetroData {
  what_went_well: string[];
  what_went_wrong: string[];
  tools_used: string[];
  time_sinks: string[];
  build_fix_cycles: number;
  confidence_score: number;
  known_issues: string[];
  missing_tools_or_knowledge: string[];
  suggestions: string[];
  summary: string;
  research_queries: ResearchQuery[];
  failed_apis: FailedApi[];
}

interface TrialData {
  trialName: string;
  condition: string;
  model: string;
  scenario: string;
  score: number;
  /**
   * Scoring v1: project/ui/visual/functionality subscores (0–10).
   * Scoring v2: projectScore = requirements %/10, uiScore = quality-check %/10,
   * visualScore = design (0–10), functionalityScore unused (0).
   */
  projectScore: number;
  uiScore: number;
  visualScore: number;
  functionalityScore: number;
  scoringVersion: 1 | 2;
  /** Requirements keyed by numeric id string */
  reqResults: Map<string, ReqResult>;
  screenshotSrc: string | null;
  inputTokens: string;
  outputTokens: string;
  cachedTokens: string;
  sessionTime: string;
  codeChanges: string;
  builds: boolean;
  runs: boolean;
  timedOut: boolean;
  failReason: string;
  buildErrors: string;
  retro: RetroData | null;
}

function findTrialDirs(runDir: string): string[] {
  const dirs: string[] = [];
  if (!existsSync(runDir)) return dirs;
  for (const entry of readdirSync(runDir, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const full = join(runDir, entry.name);
    // A trial dir has results.json directly inside
    if (existsSync(join(full, "results.json"))) {
      dirs.push(full);
    } else {
      // Check one level deeper (nested scenario/trial structure)
      for (const sub of readdirSync(full, { withFileTypes: true })) {
        if (sub.isDirectory() && existsSync(join(full, sub.name, "results.json"))) {
          dirs.push(join(full, sub.name));
        }
      }
    }
  }
  return dirs;
}

function parseValidationLog(content: string): Record<string, any> | null {
  const jsonMatch = content.match(/```json\s*(\{.+?\})\s*```/s);
  if (!jsonMatch) return null;
  try {
    return JSON.parse(jsonMatch[1]);
  } catch {
    return null;
  }
}

function findScreenshot(trialDir: string): string | null {
  // Check trial root first, then app/ subfolder
  const paths = [
    join(trialDir, "final-screenshot.png"),
    join(trialDir, "screenshot.png"),
    join(trialDir, "app", "final-screenshot.png"),
    join(trialDir, "app", "screenshot.png"),
  ];
  for (const p of paths) {
    if (existsSync(p)) return p;
  }
  return null;
}

/** Extract a numeric key from a requirement string (e.g. "1." or "10.") */
function extractReqKey(text: string): string | null {
  const m = text.match(/^(\d+)\.\s*/);
  return m ? m[1] : null;
}

/** Strip the leading number prefix to get the clean description */
function stripReqNumber(text: string): string {
  return text.replace(/^\d+\.\s*/, "").trim();
}

/** Get the core requirement text without failure explanations after colon/dash */
function getCleanReqText(text: string): string {
  let clean = stripReqNumber(text);
  // Strip failure explanations: text after ": " or " - " that follows the requirement
  // Only strip if the requirement part is long enough (>20 chars before the separator)
  const colonIdx = clean.indexOf(": ");
  if (colonIdx > 20) clean = clean.substring(0, colonIdx);
  const dashIdx = clean.indexOf(" - ");
  if (dashIdx > 20) clean = clean.substring(0, dashIdx);
  // Also strip "FAILED - " prefix
  clean = clean.replace(/^FAILED\s*[-–—]\s*/i, "");
  return clean.trim();
}

/** Extract significant words from text for fuzzy matching */
function significantWords(text: string): Set<string> {
  const stopWords = new Set(["a", "an", "the", "to", "or", "and", "in", "for", "of", "with", "must", "should", "is", "be", "not", "as", "by", "it", "its", "that", "this", "from"]);
  return new Set(
    text.toLowerCase()
      .replace(/[^a-z0-9\s]/g, " ")
      .split(/\s+/)
      .filter(w => w.length > 2 && !stopWords.has(w))
  );
}

/** Compute word overlap score between two texts (Jaccard-like) */
function wordOverlap(a: string, b: string): number {
  const wa = significantWords(a);
  const wb = significantWords(b);
  if (wa.size === 0 || wb.size === 0) return 0;
  let intersection = 0;
  for (const w of wa) if (wb.has(w)) intersection++;
  return intersection / Math.min(wa.size, wb.size);
}

function loadTrialData(trialDir: string): TrialData | null {
  const resultsPath = join(trialDir, "results.json");
  if (!existsSync(resultsPath)) return null;

  let results: Record<string, any>;
  try {
    results = JSON.parse(readFileSync(resultsPath, "utf-8"));
  } catch {
    return null;
  }

  const m = results.metrics || {};
  const tt = m.time_and_tokens || {};

  let projectScore = 0, uiScore = 0, visualScore = 0, functionalityScore = 0;
  let scoringVersion: 1 | 2 = 1;
  const reqResults = new Map<string, ReqResult>();

  // === Scoring v2 (score_breakdown) ===
  if (m.score_breakdown?.version === 2 && Array.isArray(m.requirements)) {
    scoringVersion = 2;
    const b = m.score_breakdown;
    const tenth = (v: number | null | undefined) => (typeof v === "number" ? Math.round(v * 100) / 10 : 0);
    projectScore = tenth(b.requirements?.pct);
    uiScore = tenth(b.quality?.pct);
    visualScore = typeof b.design === "number" ? b.design : 0;
    for (const req of m.requirements) {
      const id = String(req.id);
      reqResults.set(id, { text: req.text || `Requirement ${id}`, passed: req.status === "pass", reason: req.reason || "" });
    }
  }
  // === Try structured data from results.json first (new format) ===
  else if (m.subscores && m.requirements && Array.isArray(m.requirements)) {
    projectScore = m.subscores.project || 0;
    uiScore = m.subscores.ui || 0;
    visualScore = m.subscores.visual || 0;
    functionalityScore = m.subscores.functionality || 0;

    for (const req of m.requirements) {
      const id = String(req.id);
      reqResults.set(id, {
        text: req.text || `Requirement ${id}`,
        passed: req.status === "pass",
        reason: req.reason || "",
      });
    }
  }
  // === Fallback: parse validation-log.txt (old format) ===
  else {
    const valLogPath = join(trialDir, "validation-log.txt");
    if (existsSync(valLogPath)) {
      try {
        const valContent = readFileSync(valLogPath, "utf-8");
        const validation = parseValidationLog(valContent);
        if (validation) {
          projectScore = validation.project_score || 0;
          uiScore = validation.ui_score || 0;
          visualScore = validation.visual_score || 0;
          functionalityScore = validation.functionality_score || 0;

          const passed: string[] = Array.isArray(validation.requirements_passed) ? validation.requirements_passed : [];
          const failed: string[] = Array.isArray(validation.requirements_failed) ? validation.requirements_failed : [];

          for (const r of passed) {
            const key = extractReqKey(r) || `_unkeyed_pass_${r}`;
            reqResults.set(key, { text: r, passed: true, reason: "" });
          }
          for (const r of failed) {
            const key = extractReqKey(r) || `_unkeyed_fail_${r}`;
            reqResults.set(key, { text: r, passed: false, reason: stripReqNumber(r) });
          }
        }
      } catch { /* ignore */ }
    }
  }

  // Screenshot
  let screenshotSrc: string | null = null;
  const imgPath = findScreenshot(trialDir);
  if (imgPath) {
    try {
      const imgData = readFileSync(imgPath);
      const base64 = imgData.toString("base64");
      screenshotSrc = `data:image/png;base64,${base64}`;
    } catch { /* ignore */ }
  }

  // Token info — aggregate across all models used in the trial
  let inputTokens = "", outputTokens = "", cachedTokens = "";
  if (tt.models) {
    const firstModel = Object.keys(tt.models)[0];
    if (firstModel) {
      inputTokens = tt.models[firstModel].input || "";
      outputTokens = tt.models[firstModel].output || "";
      cachedTokens = tt.models[firstModel].cached || "";
    }
  }

  // Code changes
  const codeChanges = tt.code_changes || "";

  // Retrospective data
  let retro: RetroData | null = null;
  const retroPath = join(trialDir, "retrospective.json");
  if (existsSync(retroPath)) {
    try {
      const r = JSON.parse(readFileSync(retroPath, "utf-8"));
      retro = {
        what_went_well: Array.isArray(r.what_went_well) ? r.what_went_well : [],
        what_went_wrong: Array.isArray(r.what_went_wrong) ? r.what_went_wrong : [],
        tools_used: Array.isArray(r.tools_used) ? r.tools_used : [],
        time_sinks: Array.isArray(r.time_sinks) ? r.time_sinks : [],
        build_fix_cycles: r.build_fix_cycles ?? 0,
        confidence_score: r.confidence_score ?? 0,
        known_issues: Array.isArray(r.known_issues) ? r.known_issues : [],
        missing_tools_or_knowledge: Array.isArray(r.missing_tools_or_knowledge) ? r.missing_tools_or_knowledge : [],
        suggestions: Array.isArray(r.suggestions) ? r.suggestions : [],
        summary: r.summary || "",
        research_queries: Array.isArray(r.research_queries) ? r.research_queries : [],
        failed_apis: Array.isArray(r.failed_apis) ? r.failed_apis : [],
      };
    } catch { /* ignore */ }
  }

  const returnVal: TrialData = {
    trialName: results.trial || basename(trialDir),
    condition: (results.condition || "").replace(/\s*\[\d+\/\d+\]$/, ""),
    model: results.model || "unknown",
    scenario: results.scenario || "",
    score: m.score ?? 0,
    projectScore,
    uiScore,
    visualScore,
    functionalityScore,
    scoringVersion,
    reqResults,
    screenshotSrc,
    inputTokens,
    outputTokens,
    cachedTokens,
    sessionTime: tt.session_time || "",
    codeChanges,
    builds: m.builds !== false,
    runs: m.runs !== false,
    timedOut: m.timeout === true,
    failReason: results.fail_reason || "",
    buildErrors: results.build_errors || "",
    retro,
  };

  // For older results without fail_reason/build_errors, try loading build-output.txt
  if (!returnVal.builds && !returnVal.buildErrors) {
    const buildOutputPath = join(trialDir, "build-output.txt");
    if (existsSync(buildOutputPath)) {
      try {
        const output = readFileSync(buildOutputPath, "utf-8");
        const errorLines = output.split("\n").filter(l =>
          /\berror\b/i.test(l) && !/\d+ Warning/.test(l) && !/Build succeeded/.test(l)
        );
        returnVal.buildErrors = errorLines.slice(0, 20).join("\n").trim();
        if (!returnVal.buildErrors) {
          returnVal.buildErrors = output.split("\n").slice(-20).join("\n").trim();
        }
      } catch { /* ignore */ }
    }
  }
  if (!returnVal.failReason && !returnVal.builds) returnVal.failReason = "Build failed";
  if (!returnVal.failReason && !returnVal.runs) returnVal.failReason = "App did not launch";

  return returnVal;
}

function scoreColor(score: number): string {
  if (score > 70) return "#3fb950";
  if (score >= 40) return "#d29922";
  return "#f85149";
}

function escapeHtml(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

export function generateHtmlReport(entries: RunEntry[], runDir: string): string {
  // Scan for trial data
  const trialDirs = findTrialDirs(runDir);
  const allTrials: TrialData[] = [];
  for (const dir of trialDirs) {
    const data = loadTrialData(dir);
    if (data) allTrials.push(data);
  }

  // Sort by condition then model for consistent ordering
  allTrials.sort((a, b) => a.condition.localeCompare(b.condition) || a.model.localeCompare(b.model));

  // Group trials by scenario
  const scenarioMap = new Map<string, TrialData[]>();
  for (const t of allTrials) {
    const key = t.scenario || "(default)";
    if (!scenarioMap.has(key)) scenarioMap.set(key, []);
    scenarioMap.get(key)!.push(t);
  }
  const scenarioNames = Array.from(scenarioMap.keys()).sort();
  const multiScenario = scenarioNames.length > 1;

  // Run metadata
  const runName = basename(runDir);
  let timestamp = "";
  const metaPath = join(runDir, "run-meta.json");
  if (existsSync(metaPath)) {
    try {
      const meta = JSON.parse(readFileSync(metaPath, "utf-8"));
      timestamp = meta.timestamp || "";
    } catch { /* ignore */ }
  }
  if (!timestamp) {
    try {
      timestamp = statSync(runDir).mtime.toISOString();
    } catch { /* ignore */ }
  }
  const displayTime = timestamp ? new Date(timestamp).toLocaleString() : "";

  // ── Per-scenario data computation ──
  interface ScenarioComputed {
    name: string;
    description: string;
    prompt: string;
    requirements: string[];
    testAssets: Array<{ name: string; description?: string; includeInBuild: boolean }>;
    testNotes: string;
    trials: TrialData[];
    allReqKeys: string[];
    canonicalReqs: Map<string, string>;
    chartTrials: Array<Record<string, unknown>>;
    uniqueConditions: string[];
    uniqueModels: string[];
    reqPassRates: Array<{ key: string; label: string; passCount: number; totalCount: number }>;
    detailData: Array<{
      reqKey: string;
      reqLabel: string;
      trials: Array<{ condition: string; model: string; status: string; reason: string } | null>;
    }>;
    modalData: Array<{ src: string; label: string }>;
  }

  const scenarios: ScenarioComputed[] = [];

  for (let si = 0; si < scenarioNames.length; si++) {
    const scenarioName = scenarioNames[si];
    const trials = scenarioMap.get(scenarioName)!;

    // === Two-pass requirement normalization (per-scenario) ===
    const canonicalReqs = new Map<string, string>();
    const canonicalFromPassed = new Map<string, boolean>();
    for (const t of trials) {
      for (const [key, result] of t.reqResults) {
        if (/^\d+$/.test(key)) {
          const wasPassed = canonicalFromPassed.get(key) || false;
          const cleanText = getCleanReqText(result.text);
          if (!canonicalReqs.has(key) ||
              (result.passed && !wasPassed) ||
              (result.passed === wasPassed && cleanText.length > (canonicalReqs.get(key)?.length || 0))) {
            canonicalReqs.set(key, cleanText);
            canonicalFromPassed.set(key, result.passed);
          }
        }
      }
    }

    for (const t of trials) {
      const newResults = new Map<string, ReqResult>();
      for (const [key, result] of t.reqResults) {
        if (/^\d+$/.test(key)) {
          newResults.set(key, result);
        } else {
          const cleanText = getCleanReqText(result.text);
          let bestKey: string | null = null;
          let bestScore = 0;
          for (const [numKey, canonText] of canonicalReqs) {
            const score = wordOverlap(cleanText, canonText);
            if (score > bestScore && score >= 0.4) {
              bestScore = score;
              bestKey = numKey;
            }
          }
          if (bestKey && !newResults.has(bestKey)) {
            newResults.set(bestKey, result);
          }
        }
      }
      (t as any).reqResults = newResults;
    }

    const allReqKeys = Array.from(canonicalReqs.keys()).sort((a, b) => parseInt(a, 10) - parseInt(b, 10));

    // Chart data
    const chartTrials = trials.map(t => ({
      name: t.trialName,
      condition: t.condition,
      model: t.model,
      score: t.score,
      project: t.projectScore,
      ui: t.uiScore,
      visual: t.visualScore,
      functionality: t.functionalityScore,
      v2: t.scoringVersion === 2,
      inputTokens: t.inputTokens,
      outputTokens: t.outputTokens,
      cachedTokens: t.cachedTokens,
      sessionTime: t.sessionTime,
      reqsPassed: Array.from(t.reqResults.values()).filter(r => r.passed).length,
      reqsTotal: t.reqResults.size,
    }));

    const uniqueConditions = [...new Set(trials.map(t => t.condition))].sort();
    const uniqueModels = [...new Set(trials.map(t => t.model))].sort();

    const reqPassRates: Array<{ key: string; label: string; passCount: number; totalCount: number }> = [];
    for (const key of allReqKeys) {
      let passCount = 0, totalCount = 0;
      for (const t of trials) {
        const result = t.reqResults.get(key);
        if (result) {
          totalCount++;
          if (result.passed) passCount++;
        }
      }
      reqPassRates.push({
        key,
        label: `${key}. ${(canonicalReqs.get(key) || key).substring(0, 60)}`,
        passCount,
        totalCount,
      });
    }

    const detailData: ScenarioComputed["detailData"] = [];
    const modalData = trials.map(t => ({
      src: t.screenshotSrc || "",
      label: `${t.condition} · ${t.model} — ${t.score}/100`,
    }));

    // Build detail data while we have canonicalReqs; the heatmap HTML uses it
    for (const key of allReqKeys) {
      const label = canonicalReqs.get(key) || key;
      const displayLabel = `${key}. ${label}`;
      const trialDetails: ScenarioComputed["detailData"][0]["trials"] = [];
      for (const t of trials) {
        const result = t.reqResults.get(key);
        if (!result) {
          trialDetails.push(null);
        } else {
          const reason = result.reason || (result.passed ? "" : result.text);
          trialDetails.push({
            condition: t.condition,
            model: t.model,
            status: result.passed ? "pass" : "fail",
            reason,
          });
        }
      }
      detailData.push({ reqKey: key, reqLabel: displayLabel, trials: trialDetails });
    }

    // Load scenario definition (prompt, requirements, test assets)
    let scenarioDescription = "";
    let scenarioPrompt = "";
    let scenarioRequirements: string[] = [];
    let scenarioTestAssets: Array<{ name: string; description?: string; includeInBuild: boolean }> = [];
    let scenarioTestNotes = "";

    // Try to find scenario.md — runDir is like .../results/run26, scenarios is at .../scenarios/
    // Check both flat (scenarios/name/) and nested group folders (scenarios/group/name/)
    const resultsRoot = join(runDir, "..");  // .../results/
    const benchmarkRoot = join(resultsRoot, "..");  // .../benchmarks/apps/
    const scenarioRoots = [
      scenariosDir,
      join(benchmarkRoot, "scenarios"),
      join(resultsRoot, "scenarios"),
    ];
    const scenarioDirs: string[] = [];
    for (const root of scenarioRoots) {
      // Flat: scenarios/name/
      scenarioDirs.push(join(root, scenarioName));
      // Nested: scenarios/group/name/ (one level deep)
      if (existsSync(root)) {
        try {
          for (const group of readdirSync(root)) {
            const nested = join(root, group, scenarioName);
            if (existsSync(nested) && statSync(nested).isDirectory()) {
              scenarioDirs.push(nested);
            }
          }
        } catch {}
      }
    }
    for (const scenarioDir of scenarioDirs) {
      const scenarioMd = join(scenarioDir, "scenario.md");
      if (existsSync(scenarioMd)) {
        try {
          const raw = readFileSync(scenarioMd, "utf-8").replace(/\r\n/g, "\n");
          const fmMatch = raw.match(/^---\s*\n([\s\S]*?)\n---\s*\n([\s\S]*)$/);
          if (fmMatch) {
            // Parse YAML frontmatter manually for key fields
            const fm = fmMatch[1];
            const promptBody = fmMatch[2].trim();
            scenarioPrompt = promptBody;

            const descMatch = fm.match(/description:\s*"?([^\n"]+)"?/);
            if (descMatch) scenarioDescription = descMatch[1].trim();

            // Extract requirements from YAML
            const reqMatch = fm.match(/requirements:\s*\n((?:\s+-\s+.+\n?)+)/);
            if (reqMatch) {
              scenarioRequirements = reqMatch[1]
                .split("\n")
                .map(l => l.replace(/^\s*-\s*"?/, "").replace(/"?\s*$/, ""))
                .filter(l => l.length > 0);
            }

            // Extract test_notes
            const notesMatch = fm.match(/test_notes:\s*["|]([^"]+)/);
            if (notesMatch) scenarioTestNotes = notesMatch[1].trim();

            // Extract test_assets with include_in_build flag
            const assetItemMatches = fm.matchAll(/- name:\s*"?([^\n"]+)"?\n((?:\s+\w[^\n]*\n?)*)/g);
            for (const m of assetItemMatches) {
              const name = m[1].trim();
              const body = m[2] || "";
              const descM = body.match(/description:\s*"?([^\n"]+)"?/);
              const inclM = body.match(/include_in_build:\s*(true|false)/i);
              scenarioTestAssets.push({
                name,
                description: descM?.[1]?.trim(),
                includeInBuild: inclM ? inclM[1].toLowerCase() === "true" : false,
              });
            }
          }
        } catch { /* ignore */ }
        break;
      }
    }

    scenarios.push({
      name: scenarioName,
      description: scenarioDescription,
      prompt: scenarioPrompt,
      requirements: scenarioRequirements,
      testAssets: scenarioTestAssets,
      testNotes: scenarioTestNotes,
      trials,
      allReqKeys,
      canonicalReqs,
      chartTrials,
      uniqueConditions,
      uniqueModels,
      reqPassRates,
      detailData,
      modalData,
    });
  }

  // ── Build HTML for a single scenario panel ──
  function buildPanelHtml(sc: ScenarioComputed, si: number): string {
    const sfx = multiScenario ? `-${si}` : "";
    const trials = sc.trials;

    // Scenario context
    let contextHtml = `<div class="scenario-context">`;
    contextHtml += `<h2>📋 Scenario: ${escapeHtml(sc.name)}</h2>`;
    if (sc.description) {
      contextHtml += `<p class="scenario-desc">${escapeHtml(sc.description)}</p>`;
    }
    contextHtml += `<div class="scenario-meta"><span>${trials.length} trials</span><span>${sc.uniqueConditions.length} conditions</span><span>${sc.uniqueModels.length} models</span></div>`;

    if (sc.prompt) {
      contextHtml += `<div class="scenario-section"><h3>📤 Prompt given to the agent</h3><p class="scenario-section-note">This is the only instruction the building agent receives. It does not see the requirements or scoring criteria below.</p><pre class="scenario-prompt">${escapeHtml(sc.prompt)}</pre></div>`;
    }
    if (sc.testAssets.length > 0) {
      const allShared = sc.testAssets.every(a => a.includeInBuild);
      const noneShared = sc.testAssets.every(a => !a.includeInBuild);
      const assetNote = allShared
        ? "These assets were shared with the building agent and used during validation."
        : noneShared
          ? "These assets were NOT shared with the building agent — used only during validation and scoring."
          : "Some assets were shared with the building agent (marked below), others used only during validation.";
      contextHtml += `<div class="scenario-section"><h3>📦 Test Assets</h3><p class="scenario-section-note">${assetNote}</p><ul class="scenario-list">${sc.testAssets.map(a => {
        const badge = a.includeInBuild
          ? ` <span class="asset-badge shared">shared with agent</span>`
          : ` <span class="asset-badge validation">validation only</span>`;
        return `<li><strong>${escapeHtml(a.name)}</strong>${badge}${a.description ? ` — ${escapeHtml(a.description)}` : ""}</li>`;
      }).join("")}</ul></div>`;
    }
    if (sc.requirements.length > 0 || sc.testNotes) {
      contextHtml += `<div class="scenario-section"><h3>🧪 Validation criteria (not shared with agent)</h3><p class="scenario-section-note">These requirements are used only for scoring. The building agent never sees them — it must infer what to build from the prompt alone.</p>`;
      if (sc.requirements.length > 0) {
        contextHtml += `<ol class="scenario-list">${sc.requirements.map(r => `<li>${escapeHtml(r)}</li>`).join("")}</ol>`;
      }
      if (sc.testNotes) {
        contextHtml += `<p class="scenario-test-notes"><strong>Test notes:</strong> ${escapeHtml(sc.testNotes)}</p>`;
      }
      contextHtml += `</div>`;
    }

    // Scoring methodology
    const allV2 = trials.length > 0 && trials.every((t) => t.scoringVersion === 2);
    contextHtml += allV2 ? `<div class="scenario-section">
      <h3>📊 Scoring methodology (v2)</h3>
      <p class="scenario-section-note">Total score out of 100. Failing any gate (builds, launches, right framework) scores 0. Components that don't apply are dropped and the rest renormalized.</p>
      <table class="scoring-table">
        <tr><td class="scoring-cat">Requirements (60)</td><td>Share of the scenario's requirements that pass, tested by the validation agent.</td></tr>
        <tr><td class="scoring-cat">Quality checks (30)</td><td>Share of the default quality checks that pass: project hygiene, packaging, title bar and backdrop, typography and color resources, x:Bind, build warnings, and accessible names and keyboard navigation (checked by the harness), plus empty/loading/error states (judged).</td></tr>
        <tr><td class="scoring-cat">Design (10)</td><td>The judge's 0–10 design score, relative to an unmodified template.</td></tr>
      </table>
    </div>` : `<div class="scenario-section">
      <h3>📊 Scoring methodology</h3>
      <p class="scenario-section-note">Total score out of 100. Projects that fail to build or run score 0.</p>
      <table class="scoring-table">
        <tr><td class="scoring-cat">Builds &amp; runs (10 pts)</td><td>Awarded only if the project compiles and the app launches successfully. Otherwise the total score is 0.</td></tr>
        <tr><td class="scoring-cat">Quality subscores (up to 40 pts)</td><td>Four subscores, each 0–10, awarded by a validation agent:<br/>
          <span class="scoring-sub">Project quality</span> (correct framework, packages, app identity) +
          <span class="scoring-sub">UI completeness</span> (all expected controls present) +
          <span class="scoring-sub">Visual quality</span> (layout, Fluent Design, spacing) +
          <span class="scoring-sub">Functionality</span> (controls work, correct behavior)</td></tr>
        <tr><td class="scoring-cat">Requirements (up to 50 pts)</td><td>50 × (passed / total). Each requirement above is tested by the validation agent and marked pass or fail.</td></tr>
      </table>
    </div>`;
    contextHtml += `</div>`;

    // Gallery
    const galleryItems = trials.map((t, i) => {
      const label = escapeHtml(`${t.condition} · ${t.model}`);
      const img = t.screenshotSrc
        ? `<img src="${t.screenshotSrc}" alt="${label}" loading="lazy" onclick="openModal(${si},${i})" />`
        : `<div class="no-screenshot">No screenshot</div>`;
      return `
      <div class="gallery-card">
        ${img}
        <div class="gallery-label">${label}</div>
        <div class="gallery-score" style="color:${scoreColor(t.score)}">${t.score}/100</div>
      </div>`;
    }).join("\n");

    // Heatmap
    let heatmapHtml = "";
    if (sc.allReqKeys.length > 0) {
      const headerCells = trials.map(t => {
        return `<th><div class="heatmap-header"><span>${escapeHtml(t.condition)}</span><span class="hm-model">${escapeHtml(t.model)}</span><span class="hm-score" style="color:${scoreColor(t.score)}">${t.score}</span></div></th>`;
      }).join("");

      // Count validated trials (those with at least one requirement result)
      const validatedTrials = trials.filter(t => t.reqResults.size > 0);

      const rows = sc.allReqKeys.map((key, ri) => {
        const label = sc.canonicalReqs.get(key) || key;
        const displayLabel = `${key}. ${label}`;
        const cells = trials.map((t, ti) => {
          const result = t.reqResults.get(key);
          if (!result) return `<td class="cell-na" title="Not evaluated">—</td>`;
          const shortTip = result.passed ? "Passed" : "Failed — click for details";
          if (result.passed) return `<td class="cell-pass" title="${escapeHtml(shortTip)}" onclick="showDetail(${si},${ri},${ti})"></td>`;
          return `<td class="cell-fail" title="${escapeHtml(shortTip)}" onclick="showDetail(${si},${ri},${ti})"></td>`;
        }).join("");

        // Pass rate for this requirement across validated trials
        let passed = 0, evaluated = 0;
        for (const t of validatedTrials) {
          const result = t.reqResults.get(key);
          if (result) { evaluated++; if (result.passed) passed++; }
        }
        const rate = evaluated > 0 ? Math.round(100 * passed / evaluated) : 0;
        const rateColor = rate >= 70 ? "#3fb950" : rate >= 40 ? "#d29922" : "#f85149";
        const rateCell = evaluated > 0
          ? `<td class="pass-rate" style="color:${rateColor}" title="${passed}/${evaluated} trials passed">${rate}%</td>`
          : `<td class="pass-rate cell-na">—</td>`;

        return `<tr><td class="req-label" title="${escapeHtml(displayLabel)}" onclick="showReqDetail(${si},${ri})" style="cursor:pointer">${escapeHtml(displayLabel)}</td>${cells}${rateCell}</tr>`;
      }).join("\n");

      heatmapHtml = `
    <h2>Requirements Heatmap</h2>
    <p class="hint">Click a cell for details · Click a requirement label to compare across all trials</p>
    <div class="table-wrap">
      <table class="heatmap">
        <thead><tr><th class="req-label">Requirement</th>${headerCells}<th>Pass Rate</th></tr></thead>
        <tbody>${rows}</tbody>
      </table>
    </div>
    <div id="detail-panel${sfx}" class="detail-panel" style="display:none">
      <div class="detail-header">
        <span id="detail-title${sfx}"></span>
        <button onclick="hideDetail(${si})" class="detail-close">✕</button>
      </div>
      <div id="detail-body${sfx}"></div>
    </div>`;
    }

    // Subscores table
    const subscoreRows = trials.map(t => {
      const total = t.score;
      return `<tr>
      <td>${escapeHtml(t.trialName)}</td>
      <td>${escapeHtml(t.condition)}</td>
      <td>${escapeHtml(t.model)}</td>
      <td>${t.projectScore}</td>
      <td>${t.uiScore}</td>
      <td>${t.visualScore}</td>
      <td>${t.functionalityScore}</td>
      <td style="color:${scoreColor(total)};font-weight:600">${total}</td>
      <td>${escapeHtml(t.inputTokens)}</td>
      <td>${escapeHtml(t.outputTokens)}</td>
      <td>${escapeHtml(t.sessionTime)}</td>
    </tr>`;
    }).join("\n");

    // Per-trial retrospective cards (include failed trials even without retro)
    const retroCards = trials.map(t => {
      const r = t.retro;

      // Failed trial without retrospective — show failure details
      if (!r) {
        if (!t.failReason && t.score > 0) return ""; // Normal trial, just no retro
        return `
      <div class="retro-card expanded">
        <div class="retro-card-header" onclick="this.parentElement.classList.toggle('expanded')">
          <div class="retro-card-title">
            <span class="retro-score" style="color:${scoreColor(t.score)}">${t.score}</span>
            <span>${escapeHtml(t.condition)} · ${escapeHtml(t.model)}</span>
            <span class="retro-meta">${t.timedOut ? "⏰ Timed out" : !t.builds ? "❌ Build failed" : !t.runs ? "❌ App did not launch" : ""} · ${escapeHtml(t.sessionTime)}</span>
          </div>
          <span class="retro-expand">▸</span>
        </div>
        <div class="retro-card-body">
          ${t.failReason ? `<div class="retro-fail-reason">Failure: ${escapeHtml(t.failReason)}</div>` : ""}
          ${t.buildErrors ? `<div class="retro-build-errors"><h4>Build Errors</h4><pre>${escapeHtml(t.buildErrors)}</pre></div>` : ""}
        </div>
      </div>`;
      }

      const wellItems = r.what_went_well.map(w => `<li>${escapeHtml(w)}</li>`).join("");
      const wrongItems = r.what_went_wrong.map(w => `<li>${escapeHtml(w)}</li>`).join("");
      const sinkItems = r.time_sinks.map(s => `<li>${escapeHtml(s)}</li>`).join("");
      const issueItems = r.known_issues.map(i => `<li>${escapeHtml(i)}</li>`).join("");
      const missingItems = r.missing_tools_or_knowledge.map(m => `<li>${escapeHtml(m)}</li>`).join("");

      // Research queries table
      const researchRows = r.research_queries.length > 0
        ? `<h4>🔍 Research Queries</h4>
           <table class="retro-table">
             <thead><tr><th>Query</th><th>Source</th><th>Useful</th><th>Issue</th></tr></thead>
             <tbody>${r.research_queries.map(q => `<tr>
               <td>${escapeHtml(q.query)}</td>
               <td><code>${escapeHtml(q.source)}</code></td>
               <td class="${q.useful === 'yes' ? 'cell-pass' : q.useful === 'no' ? 'cell-fail' : ''}">${escapeHtml(q.useful)}</td>
               <td>${q.issue ? escapeHtml(q.issue) : "—"}</td>
             </tr>`).join("")}</tbody>
           </table>`
        : "";

      // Failed APIs table
      const failedApiRows = r.failed_apis.length > 0
        ? `<h4>💥 Failed APIs / Patterns</h4>
           <table class="retro-table">
             <thead><tr><th>API / Pattern</th><th>Why Used</th><th>Reason Failed</th><th>Discovery</th><th>Alternative</th></tr></thead>
             <tbody>${r.failed_apis.map(a => `<tr>
               <td><code>${escapeHtml(a.api)}</code></td>
               <td>${escapeHtml(a.origin || "—")}</td>
               <td>${escapeHtml(a.reason)}</td>
               <td>${escapeHtml(a.discovery)}</td>
               <td>${a.alternative ? `<code>${escapeHtml(a.alternative)}</code>` : "—"}</td>
             </tr>`).join("")}</tbody>
           </table>`
        : "";

      return `
      <div class="retro-card">
        <div class="retro-card-header" onclick="this.parentElement.classList.toggle('expanded')">
          <div class="retro-card-title">
            <span class="retro-score" style="color:${scoreColor(t.score)}">${t.score}</span>
            <span>${escapeHtml(t.condition)} · ${escapeHtml(t.model)}</span>
            <span class="retro-meta">${escapeHtml(t.sessionTime)} · ${escapeHtml(t.codeChanges || "—")} · confidence: ${r.confidence_score}/10 · build cycles: ${r.build_fix_cycles}${r.research_queries.length ? ` · ${r.research_queries.length} searches` : ""}${r.failed_apis.length ? ` · ${r.failed_apis.length} failed APIs` : ""}</span>
          </div>
          <span class="retro-expand">▸</span>
        </div>
        <div class="retro-card-body">
          <div class="retro-summary">${escapeHtml(r.summary)}</div>
          <div class="retro-columns">
            <div class="retro-col">
              ${wrongItems ? `<h4>❌ What went wrong</h4><ul>${wrongItems}</ul>` : ""}
              ${sinkItems ? `<h4>⏱ Time sinks</h4><ul>${sinkItems}</ul>` : ""}
              ${issueItems ? `<h4>⚠️ Known issues</h4><ul>${issueItems}</ul>` : ""}
            </div>
            <div class="retro-col">
              ${wellItems ? `<h4>✅ What went well</h4><ul>${wellItems}</ul>` : ""}
              ${missingItems ? `<h4>🔧 Missing tools/knowledge</h4><ul>${missingItems}</ul>` : ""}
            </div>
          </div>
          ${researchRows}
          ${failedApiRows}
        </div>
      </div>`;
    }).filter(c => c.length > 0).join("\n");

    // Cross-run pattern analysis
    const trialsWithRetro = trials.filter(t => t.retro);

    // Fuzzy bucketing: normalize strings and group similar items
    const normalizeForBucket = (s: string): string => {
      return s
        .toLowerCase()
        .replace(/[—–\-]/g, " ")
        .replace(/\s+/g, " ")
        .replace(/[^a-z0-9 ]/g, "")
        .trim()
        .split(" ")
        .filter(w => w.length > 2)
        .sort()
        .join(" ");
    };

    const bucketItems = (items: Array<{ text: string; trial: string }>) => {
      const buckets = new Map<string, { texts: string[]; trials: Set<string> }>();
      for (const item of items) {
        const key = normalizeForBucket(item.text);
        if (!key) continue;
        // Find existing bucket with >50% word overlap
        let matched = false;
        for (const [bKey, bucket] of buckets) {
          const keyWords = new Set(key.split(" "));
          const bWords = new Set(bKey.split(" "));
          const overlap = [...keyWords].filter(w => bWords.has(w)).length;
          const similarity = overlap / Math.max(keyWords.size, bWords.size);
          if (similarity > 0.5) {
            bucket.texts.push(item.text);
            bucket.trials.add(item.trial);
            matched = true;
            break;
          }
        }
        if (!matched) {
          buckets.set(key, { texts: [item.text], trials: new Set([item.trial]) });
        }
      }
      return Array.from(buckets.values())
        .filter(b => b.trials.size > 1) // Only show items common across 2+ runs
        .sort((a, b) => b.trials.size - a.trials.size)
        .slice(0, 10)
        .map(b => ({
          text: b.texts[0], // Use first occurrence as representative
          count: b.trials.size,
          trials: Array.from(b.trials),
        }));
    };

    const allWrong = trialsWithRetro.flatMap(t => t.retro!.what_went_wrong.map(text => ({ text, trial: t.trialName })));
    const allSinks = trialsWithRetro.flatMap(t => t.retro!.time_sinks.map(text => ({ text, trial: t.trialName })));
    const allMissing = trialsWithRetro.flatMap(t => t.retro!.missing_tools_or_knowledge.map(text => ({ text, trial: t.trialName })));
    const allIssues = trialsWithRetro.flatMap(t => t.retro!.known_issues.map(text => ({ text, trial: t.trialName })));
    const allResearch = trialsWithRetro.flatMap(t => t.retro!.research_queries.map(q => ({ ...q, trial: t.trialName })));
    const allFailedApis = trialsWithRetro.flatMap(t => t.retro!.failed_apis.map(a => ({ ...a, trial: t.trialName })));

    const topWrong = bucketItems(allWrong);
    const topSinks = bucketItems(allSinks);
    const topMissing = bucketItems(allMissing);

    // Research effectiveness summary
    const researchBySource = new Map<string, { total: number; useful: number; partial: number; notUseful: number }>();
    for (const q of allResearch) {
      const src = q.source || "unknown";
      const entry = researchBySource.get(src) || { total: 0, useful: 0, partial: 0, notUseful: 0 };
      entry.total++;
      if (q.useful === "yes") entry.useful++;
      else if (q.useful === "partially") entry.partial++;
      else entry.notUseful++;
      researchBySource.set(src, entry);
    }

    // Research issues breakdown
    const researchIssues = new Map<string, number>();
    for (const q of allResearch) {
      if (q.issue) {
        researchIssues.set(q.issue, (researchIssues.get(q.issue) || 0) + 1);
      }
    }

    // Failed API patterns
    const failedApiReasons = new Map<string, number>();
    for (const a of allFailedApis) {
      failedApiReasons.set(a.reason, (failedApiReasons.get(a.reason) || 0) + 1);
    }

    const avgBuildCycles = trialsWithRetro.length > 0
      ? (trialsWithRetro.reduce((s, t) => s + t.retro!.build_fix_cycles, 0) / trialsWithRetro.length).toFixed(1)
      : "—";
    const avgConfidence = trialsWithRetro.length > 0
      ? (trialsWithRetro.reduce((s, t) => s + t.retro!.confidence_score, 0) / trialsWithRetro.length).toFixed(1)
      : "—";

    const patternRows = (items: Array<{text: string; count: number; trials: string[]}>) =>
      items.map(i => {
        // Extract agent name from trial name (e.g., "tte08_base-DA_o46_i1" → "base-DA")
        // Reverse-map any short alias produced by makeTrialName back to the full name.
        const agents = [...new Set(i.trials.map(t => {
          const m = t.match(/^[^_]+_(.+?)_[a-z]\d+_i\d+$/);
          return m ? expandAgentName(m[1]) : t;
        }))];
        const agentTags = agents.map(a => `<span class="pattern-agent">${escapeHtml(a)}</span>`).join(" ");
        return `<tr><td class="pattern-text">${escapeHtml(i.text)}<div class="pattern-agents">${agentTags}</div></td><td class="pattern-count">${i.count}/${trialsWithRetro.length}</td></tr>`;
      }).join("");

    let patternsHtml = "";
    if (trialsWithRetro.length > 0) {
      // Research effectiveness table
      let researchHtml = "";
      if (researchBySource.size > 0 || allResearch.length > 0) {
        const srcRows = Array.from(researchBySource.entries())
          .sort((a, b) => b[1].total - a[1].total)
          .map(([src, s]) => {
            const pct = s.total > 0 ? Math.round(100 * s.useful / s.total) : 0;
            return `<tr><td><code>${escapeHtml(src)}</code></td><td>${s.total}</td><td style="color:#3fb950">${s.useful}</td><td style="color:#d29922">${s.partial}</td><td style="color:#f85149">${s.notUseful}</td><td>${pct}%</td></tr>`;
          }).join("");
        const issueRows = Array.from(researchIssues.entries())
          .sort((a, b) => b[1] - a[1])
          .map(([issue, count]) => `<tr><td>${escapeHtml(issue)}</td><td>${count}</td></tr>`).join("");

        // Detailed query list
        const queryRows = allResearch.map(q =>
          `<tr>
            <td style="max-width:250px;word-break:break-word">${escapeHtml(q.query)}</td>
            <td><code>${escapeHtml(q.source || "—")}</code></td>
            <td style="max-width:300px;word-break:break-word;color:#8b949e">${escapeHtml(q.found || "—")}</td>
            <td>${q.useful === "yes" ? '<span style="color:#3fb950">✓ yes</span>' : q.useful === "partially" ? '<span style="color:#d29922">~ partial</span>' : '<span style="color:#f85149">✗ no</span>'}</td>
            <td style="color:#8b949e">${escapeHtml(q.trial)}</td>
          </tr>`
        ).join("");

        researchHtml = `
      <div class="pattern-card" style="grid-column: 1 / -1">
        <h3>🔍 Research Effectiveness</h3>
        <table>
          <thead><tr><th>Source</th><th>Total</th><th>Useful</th><th>Partial</th><th>Not Useful</th><th>Hit Rate</th></tr></thead>
          <tbody>${srcRows}</tbody>
        </table>
        ${issueRows ? `<h4 style="margin-top:12px;color:#d29922;font-size:0.85em">Documentation Issues</h4><table>${issueRows}</table>` : ""}
        ${queryRows ? `<h4 style="margin-top:16px;color:#79c0ff;font-size:0.85em">All Queries (${allResearch.length})</h4>
        <table>
          <thead><tr><th>Query</th><th>Source</th><th>Found</th><th>Useful?</th><th>Run</th></tr></thead>
          <tbody>${queryRows}</tbody>
        </table>` : ""}
      </div>`;
      }

      // Failed APIs table
      let failedApisHtml = "";
      if (allFailedApis.length > 0) {
        // Group failed APIs by normalized name and count unique runs
        const apiGroups = new Map<string, { api: FailedApi & { trial: string }; trials: Set<string> }>();
        for (const a of allFailedApis) {
          const key = a.api.toLowerCase().trim();
          if (!apiGroups.has(key)) {
            apiGroups.set(key, { api: a, trials: new Set([a.trial]) });
          } else {
            apiGroups.get(key)!.trials.add(a.trial);
          }
        }

        const apiRows = Array.from(apiGroups.values())
          .sort((a, b) => b.trials.size - a.trials.size)
          .slice(0, 15)
          .map(g => {
            const a = g.api;
            const runLabel = g.trials.size === trialsWithRetro.length
              ? `<strong>all ${g.trials.size}</strong>`
              : `${g.trials.size}/${trialsWithRetro.length}`;
            return `<tr>
              <td><code>${escapeHtml(a.api)}</code></td>
              <td>${escapeHtml(a.origin || "—")}</td>
              <td>${escapeHtml(a.reason)}</td>
              <td>${a.alternative ? `<code>${escapeHtml(a.alternative)}</code>` : "—"}</td>
              <td style="text-align:center">${runLabel}</td>
            </tr>`;
          }).join("");
        const reasonRows = Array.from(failedApiReasons.entries())
          .sort((a, b) => b[1] - a[1])
          .map(([reason, count]) => `<tr><td>${escapeHtml(reason)}</td><td>${count}</td></tr>`).join("");

        // Origin breakdown
        const originCounts = new Map<string, number>();
        for (const a of allFailedApis) {
          const o = a.origin || "unknown";
          originCounts.set(o, (originCounts.get(o) || 0) + 1);
        }
        const originRows = Array.from(originCounts.entries())
          .sort((a, b) => b[1] - a[1])
          .map(([origin, count]) => `<tr><td>${escapeHtml(origin)}</td><td>${count}</td></tr>`).join("");

        failedApisHtml = `
      <div class="pattern-card" style="grid-column: 1 / -1">
        <h3>💥 Failed APIs (${allFailedApis.length} total across ${apiGroups.size} unique)</h3>
        <table>
          <thead><tr><th>API / Pattern</th><th>Why Used</th><th>Failure Reason</th><th>Alternative</th><th>Runs Hit</th></tr></thead>
          <tbody>${apiRows}</tbody>
        </table>
        <div style="display:flex;gap:24px;margin-top:12px">
          ${reasonRows ? `<div><h4 style="color:#f85149;font-size:0.85em">Failure Reasons</h4><table>${reasonRows}</table></div>` : ""}
          ${originRows ? `<div><h4 style="color:#d29922;font-size:0.85em">Why They Were Tried</h4><table>${originRows}</table></div>` : ""}
        </div>
      </div>`;
      }

      patternsHtml = `
    <h2>🔍 Cross-Run Patterns</h2>
    <div class="pattern-stats">
      <span>Avg build cycles: <strong>${avgBuildCycles}</strong></span>
      <span>Avg confidence: <strong>${avgConfidence}/10</strong></span>
      <span>Trials with retro: <strong>${trialsWithRetro.length}/${trials.length}</strong></span>
      ${allResearch.length ? `<span>Total searches: <strong>${allResearch.length}</strong></span>` : ""}
      ${allFailedApis.length ? `<span>Failed APIs: <strong>${allFailedApis.length}</strong></span>` : ""}
    </div>
    <div class="pattern-grid">
      ${topWrong.length ? `<div class="pattern-card"><h3>❌ Common Failures</h3><table>${patternRows(topWrong)}</table></div>` : ""}
      ${topSinks.length ? `<div class="pattern-card"><h3>⏱ Common Time Sinks</h3><table>${patternRows(topSinks)}</table></div>` : ""}
      ${topMissing.length ? `<div class="pattern-card"><h3>🔧 Commonly Missing</h3><table>${patternRows(topMissing)}</table></div>` : ""}
      ${researchHtml}
      ${failedApisHtml}
    </div>`;
    }

    // Comparison table with screenshots and all stats
    const comparisonRows = trials.map((t, ti) => {
      const r = t.retro;
      const thumb = t.screenshotSrc
        ? `<img src="${t.screenshotSrc}" class="comp-thumb" onclick="openModal(${si},${ti})" />`
        : `<span class="comp-no-img">—</span>`;
      const statusBadge = t.timedOut ? `<span class="status-badge warn">Timed out</span>`
        : !t.builds ? `<span class="status-badge fail">Build failed</span>`
        : !t.runs ? `<span class="status-badge warn">No launch</span>`
        : `<span class="status-badge pass">OK</span>`;
      return `<tr>
        <td class="comp-thumb-cell">${thumb}</td>
        <td class="comp-trial">${escapeHtml(t.trialName)}</td>
        <td>${escapeHtml(t.condition)}</td>
        <td>${statusBadge}</td>
        <td style="color:${scoreColor(t.score)};font-weight:600">${t.score}</td>
        <td>${r ? r.build_fix_cycles : "—"}</td>
        <td>${r ? r.confidence_score + "/10" : "—"}</td>
        <td>${escapeHtml(t.sessionTime)}</td>
        <td>${escapeHtml(t.inputTokens)}</td>
        <td>${escapeHtml(t.outputTokens)}</td>
        <td>${escapeHtml(t.codeChanges || "—")}</td>
        <td>${r ? r.what_went_wrong.length : "—"}</td>
        <td>${r ? r.known_issues.length : "—"}</td>
      </tr>`;
    }).join("\n");

    return `
${contextHtml}

<!-- Analysis Charts -->
<div class="charts-section">
<h2>📊 Analysis</h2>

<div class="filter-bar" id="filter-bar${sfx}">
  <div class="filter-group">
    <span class="filter-group-label">Conditions:</span>
    ${sc.uniqueConditions.map(c => `<span class="filter-chip active" data-type="condition" data-value="${escapeHtml(c)}" data-scenario="${si}" onclick="toggleFilter(this)">${escapeHtml(c)}</span>`).join("")}
  </div>
  <div class="filter-sep"></div>
  <div class="filter-group">
    <span class="filter-group-label">Models:</span>
    ${sc.uniqueModels.map(m => `<span class="filter-chip active" data-type="model" data-value="${escapeHtml(m)}" data-scenario="${si}" onclick="toggleFilter(this)">${escapeHtml(m)}</span>`).join("")}
  </div>
</div>

<div class="chart-grid">
  <div class="chart-card">
    <h3>Score by Condition</h3>
    <canvas id="chart-scores${sfx}"></canvas>
  </div>
  <div class="chart-card">
    <h3>Score vs Tokens</h3>
    <canvas id="chart-cost-quality${sfx}"></canvas>
  </div>
  <div class="chart-card">
    <h3>Efficiency (Score per M Tokens)</h3>
    <canvas id="chart-efficiency${sfx}"></canvas>
  </div>
  <div class="chart-card">
    <h3>Subscore Breakdown</h3>
    <canvas id="chart-subscores${sfx}"></canvas>
  </div>
</div>
</div>

<h2>Comparison</h2>
<div class="table-wrap">
  <table class="subscores">
    <thead><tr>
      <th>Screenshot</th><th>Trial</th><th>Condition</th><th>Status</th>
      <th>Score</th><th>Build Cycles</th><th>Confidence</th>
      <th>Time</th><th>Input Tokens</th><th>Output Tokens</th><th>Code Changes</th>
      <th>Issues</th><th>Known Bugs</th>
    </tr></thead>
    <tbody>${comparisonRows}</tbody>
  </table>
</div>

${heatmapHtml}

${patternsHtml}

${retroCards.length > 0 ? `
<h2>📝 Trial Retrospectives</h2>
<p class="hint">Click a card to expand details</p>
${retroCards}
` : ""}`;
  }

  // ── Assemble body content ──
  let bodyContent = "";
  if (scenarios.length === 0) {
    bodyContent = `<div class="scenario-context"><h2>No trial data found</h2><p>No completed trials were found in this run directory.</p></div>`;
  } else if (multiScenario) {
    bodyContent += `\n<div class="scenario-tabs">\n`;
    for (let si = 0; si < scenarios.length; si++) {
      const active = si === 0 ? " active" : "";
      bodyContent += `  <button class="scenario-tab${active}" onclick="switchTab(${si})">${escapeHtml(scenarios[si].name)}</button>\n`;
    }
    bodyContent += `</div>\n`;
    for (let si = 0; si < scenarios.length; si++) {
      const active = si === 0 ? " active" : "";
      bodyContent += `<div class="scenario-panel${active}" id="scenario-${si}">\n`;
      bodyContent += buildPanelHtml(scenarios[si], si);
      bodyContent += `\n</div>\n`;
    }
  } else {
    bodyContent = buildPanelHtml(scenarios[0], 0);
  }

  // Build JS data payload — array of per-scenario objects
  const jsScenarioData = scenarios.map(sc => ({
    detailData: sc.detailData,
    chartTrials: sc.chartTrials,
    reqPassRates: sc.reqPassRates,
    uniqueConditions: sc.uniqueConditions,
    uniqueModels: sc.uniqueModels,
    modalData: sc.modalData,
  }));

  // Tab CSS (only included when multiple scenarios)
  const tabCss = multiScenario ? `
  /* Scenario tabs */
  .scenario-tabs {
    display: flex; gap: 0; margin-bottom: 24px;
    border-bottom: 2px solid #21262d;
  }
  .scenario-tab {
    padding: 8px 20px; cursor: pointer;
    color: #8b949e; border: none; background: none;
    font-size: 0.9em; border-bottom: 2px solid transparent;
    margin-bottom: -2px; transition: all 0.15s;
  }
  .scenario-tab:hover { color: #e6edf3; }
  .scenario-tab.active {
    color: #58a6ff; border-bottom-color: #58a6ff; font-weight: 600;
  }
  .scenario-panel { display: none; }
  .scenario-panel.active { display: block; }` : "";

  const html = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1.0"/>
<title>Benchmark Report — ${escapeHtml(runName)}</title>
<script src="https://cdn.jsdelivr.net/npm/chart.js@4"></script>
<style>
  *, *::before, *::after { box-sizing: border-box; }
  body {
    margin: 0; padding: 24px;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
    background: #0d1117; color: #e6edf3;
    line-height: 1.5;
  }
  h1 { color: #58a6ff; margin: 0 0 4px; font-size: 1.6em; }
  h2 { color: #79c0ff; margin: 32px 0 12px; font-size: 1.25em; border-bottom: 1px solid #21262d; padding-bottom: 6px; }
  .subtitle { color: #8b949e; font-size: 0.9em; margin-bottom: 24px; }

  /* Gallery */
  .gallery { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 16px; }
  .gallery-card {
    background: #161b22; border: 1px solid #30363d; border-radius: 8px;
    overflow: hidden; transition: border-color 0.15s;
  }
  .gallery-card:hover { border-color: #58a6ff; }
  .gallery-card img {
    width: 100%; height: 150px; object-fit: cover; cursor: pointer;
    display: block; background: #21262d;
  }
  .no-screenshot {
    width: 100%; height: 150px; display: flex; align-items: center; justify-content: center;
    color: #484f58; background: #21262d; font-size: 0.85em;
  }
  .gallery-label { padding: 8px 10px 2px; font-size: 0.8em; color: #8b949e; word-break: break-word; }
  .gallery-score { padding: 2px 10px 8px; font-size: 1.1em; font-weight: 700; }

  /* Tables */
  .table-wrap { overflow-x: auto; }
  table { border-collapse: collapse; width: 100%; font-size: 0.85em; }
  th, td { padding: 6px 10px; border: 1px solid #21262d; text-align: center; }
  th { background: #161b22; color: #8b949e; font-weight: 600; position: sticky; top: 0; }
  td { background: #0d1117; }
  tr:hover td { background: #161b22; }

  /* Heatmap */
  .heatmap th { white-space: nowrap; }
  .heatmap-header { display: flex; flex-direction: column; gap: 2px; align-items: center; min-width: 80px; }
  .hm-model { font-size: 0.75em; color: #6e7681; }
  .hm-score { font-weight: 700; font-size: 0.9em; }
  .req-label { text-align: left; max-width: 350px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 0.8em; }
  .cell-pass { background: #0b2e13 !important; cursor: pointer; }
  .cell-fail { background: #3d1118 !important; cursor: pointer; }
  .cell-na { color: #484f58; }
  .hint { color: #6e7681; font-size: 0.8em; margin: 4px 0 8px; }

  /* Scenario context */
  .scenario-context {
    background: #161b22; border: 1px solid #30363d; border-radius: 8px;
    padding: 20px 24px; margin-bottom: 24px;
  }
  .scenario-context h2 { margin: 0 0 4px; border: none; padding: 0; }
  .scenario-desc { color: #c9d1d9; font-size: 0.9em; margin: 0 0 4px; }
  .scenario-section { margin-top: 16px; padding-top: 12px; border-top: 1px solid #21262d; }
  .scenario-section h3 { color: #79c0ff; font-size: 0.9em; margin: 0 0 4px; }
  .scenario-section-note { color: #6e7681; font-size: 0.8em; margin: 2px 0 8px; font-style: italic; }
  .scenario-prompt {
    background: #0d1117; border: 1px solid #21262d; border-radius: 6px;
    padding: 12px 16px; font-size: 0.8em;
    white-space: pre-wrap; word-break: break-word; color: #c9d1d9;
    max-height: 400px; overflow-y: auto; line-height: 1.6;
  }
  .scenario-list { padding-left: 20px; font-size: 0.82em; color: #c9d1d9; line-height: 1.7; margin: 4px 0 8px; }
  .asset-badge {
    display: inline-block; padding: 1px 7px; border-radius: 10px;
    font-size: 0.78em; font-weight: 500; vertical-align: middle; margin-left: 4px;
  }
  .asset-badge.shared { background: #0b2e13; color: #3fb950; border: 1px solid #3fb95044; }
  .asset-badge.validation { background: #21262d; color: #8b949e; border: 1px solid #30363d; }
  .scenario-test-notes { font-size: 0.82em; color: #c9d1d9; margin: 8px 0 0; }
  .scoring-table { width: 100%; font-size: 0.82em; margin-top: 6px; }
  .scoring-table td { padding: 6px 10px; border: 1px solid #21262d; vertical-align: top; }
  .scoring-cat { color: #79c0ff; font-weight: 600; white-space: nowrap; width: 180px; }
  .scoring-sub { color: #d29922; font-weight: 500; }
  .scenario-meta {
    display: flex; gap: 16px; margin-top: 8px;
    font-size: 0.8em; color: #8b949e;
  }

  /* Detail panel */
  .detail-panel {
    margin-top: 12px; background: #161b22; border: 1px solid #30363d;
    border-radius: 8px; overflow: hidden;
  }
  .detail-header {
    display: flex; justify-content: space-between; align-items: center;
    padding: 10px 16px; background: #21262d; border-bottom: 1px solid #30363d;
  }
  .detail-header span { color: #79c0ff; font-weight: 600; font-size: 0.9em; }
  .detail-close {
    background: none; border: none; color: #8b949e; cursor: pointer;
    font-size: 1.1em; padding: 4px 8px; border-radius: 4px;
  }
  .detail-close:hover { background: #30363d; color: #e6edf3; }
  .detail-body-inner { padding: 12px 16px; }
  .detail-item {
    padding: 8px 12px; margin-bottom: 6px; border-radius: 6px;
    font-size: 0.85em; line-height: 1.6;
  }
  .detail-item.pass { background: #0b2e13; border-left: 3px solid #3fb950; }
  .detail-item.fail { background: #3d1118; border-left: 3px solid #f85149; }
  .detail-item .detail-trial { color: #8b949e; font-size: 0.8em; }
  .detail-item .detail-status { font-weight: 600; }
  .detail-item .detail-reason { color: #e6edf3; margin-top: 4px; white-space: pre-wrap; word-break: break-word; }

  /* Subscores */
  .subscores td:first-child { text-align: left; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 0.8em; }
  .subscores td:nth-child(2), .subscores td:nth-child(3) { text-align: left; }

  /* Charts section */
  .charts-section { margin-top: 32px; }
  .filter-bar {
    display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 16px;
    padding: 12px 16px; background: #161b22; border: 1px solid #30363d; border-radius: 8px;
  }
  .filter-group { display: flex; align-items: center; gap: 6px; }
  .filter-group-label { color: #8b949e; font-size: 0.8em; font-weight: 600; margin-right: 4px; }
  .filter-chip {
    display: inline-flex; align-items: center; gap: 4px;
    padding: 3px 10px; border-radius: 12px; font-size: 0.78em;
    border: 1px solid #30363d; background: #21262d; color: #e6edf3;
    cursor: pointer; user-select: none; transition: all 0.15s;
  }
  .filter-chip.active { border-color: #58a6ff; background: #1f3a5f; }
  .filter-chip:hover { border-color: #58a6ff; }
  .filter-sep { width: 1px; height: 24px; background: #30363d; margin: 0 4px; }
  .chart-grid {
    display: grid; grid-template-columns: 1fr 1fr; gap: 20px;
  }
  .chart-card {
    background: #161b22; border: 1px solid #30363d; border-radius: 8px;
    padding: 16px; min-height: 300px;
  }
  .chart-card.full-width { grid-column: 1 / -1; }
  .chart-card h3 { color: #79c0ff; font-size: 0.95em; margin: 0 0 12px; }
  .chart-card canvas { max-height: 350px; }
  @media (max-width: 900px) { .chart-grid { grid-template-columns: 1fr; } }

  /* Retrospective cards */
  .retro-card {
    background: #161b22; border: 1px solid #30363d; border-radius: 8px;
    margin-bottom: 8px; overflow: hidden;
  }
  .retro-card-header {
    display: flex; justify-content: space-between; align-items: center;
    padding: 10px 16px; cursor: pointer; transition: background 0.15s;
  }
  .retro-card-header:hover { background: #21262d; }
  .retro-card-title { display: flex; align-items: center; gap: 12px; font-size: 0.85em; }
  .retro-score { font-weight: 700; font-size: 1.2em; min-width: 30px; }
  .retro-meta { color: #6e7681; font-size: 0.85em; }
  .retro-expand { color: #6e7681; transition: transform 0.2s; }
  .retro-card.expanded .retro-expand { transform: rotate(90deg); }
  .retro-card-body { display: none; padding: 0 16px 16px; }
  .retro-card.expanded .retro-card-body { display: block; }
  .retro-summary {
    padding: 10px 12px; margin-bottom: 12px; background: #21262d;
    border-radius: 6px; font-size: 0.85em; line-height: 1.6; color: #e6edf3;
  }
  .retro-columns { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
  @media (max-width: 800px) { .retro-columns { grid-template-columns: 1fr; } }
  .retro-col h4 { color: #79c0ff; font-size: 0.85em; margin: 0 0 6px; }
  .retro-col ul { margin: 0 0 12px; padding-left: 18px; font-size: 0.8em; line-height: 1.6; color: #c9d1d9; }
  .retro-col li { margin-bottom: 4px; }
  .retro-table { width: 100%; font-size: 0.8em; margin-top: 8px; margin-bottom: 16px; }
  .retro-table th { text-align: left; background: #21262d; color: #8b949e; font-weight: 600; padding: 4px 8px; }
  .retro-table td { padding: 4px 8px; border: 1px solid #21262d; text-align: left; }
  .retro-table code { color: #79c0ff; font-size: 0.95em; }

  /* Pattern analysis */
  .pattern-stats {
    display: flex; gap: 24px; margin-bottom: 16px; font-size: 0.85em; color: #8b949e;
  }
  .pattern-stats strong { color: #e6edf3; }
  .pattern-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(400px, 1fr)); gap: 16px; }
  .pattern-card {
    background: #161b22; border: 1px solid #30363d; border-radius: 8px;
    padding: 16px; overflow: hidden;
  }
  .pattern-card h3 { color: #79c0ff; font-size: 0.9em; margin: 0 0 10px; }
  .pattern-card table { width: 100%; font-size: 0.8em; }
  .pattern-card td { padding: 4px 8px; border: none; text-align: left; }
  .pattern-text { color: #c9d1d9; word-break: break-word; white-space: normal; }
  .pattern-count { color: #8b949e; text-align: right !important; white-space: nowrap; min-width: 50px; vertical-align: top; }
  .pattern-agents { margin-top: 4px; display: flex; flex-wrap: wrap; gap: 3px; }
  .pattern-agent {
    display: inline-block; padding: 1px 6px; border-radius: 8px;
    font-size: 0.72em; background: #21262d; color: #79c0ff; border: 1px solid #30363d;
  }
  .pass-rate { font-weight: 600; text-align: center !important; white-space: nowrap; min-width: 70px; }

  /* Comparison table */
  .comp-trial { font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 0.78em; text-align: left !important; }
  .comp-thumb-cell { padding: 4px !important; width: 80px; }
  .comp-thumb {
    width: 72px; height: 48px; object-fit: cover; border-radius: 4px;
    cursor: pointer; display: block; background: #21262d;
    transition: transform 0.15s;
  }
  .comp-thumb:hover { transform: scale(1.5); z-index: 10; position: relative; box-shadow: 0 4px 16px rgba(0,0,0,0.6); }
  .comp-no-img { color: #484f58; font-size: 0.8em; }
  .status-badge {
    display: inline-block; padding: 2px 8px; border-radius: 10px;
    font-size: 0.75em; font-weight: 500;
  }
  .status-badge.pass { background: #0b2e13; color: #3fb950; }
  .status-badge.warn { background: #3d2e00; color: #d29922; }
  .status-badge.fail { background: #3d1118; color: #f85149; }
  .retro-fail-reason {
    padding: 8px 12px; margin-bottom: 8px; background: #3d1118;
    border-left: 3px solid #f85149; border-radius: 4px;
    color: #f85149; font-weight: 600; font-size: 0.85em;
  }
  .retro-build-errors h4 { color: #f85149; font-size: 0.85em; margin: 8px 0 4px; }
  .retro-build-errors pre {
    background: #0d1117; border: 1px solid #3d1118; border-radius: 4px;
    padding: 10px 12px; font-size: 0.75em; color: #f85149;
    white-space: pre-wrap; word-break: break-word; max-height: 300px; overflow-y: auto;
  }

  /* Modal */
  .modal-overlay {
    display: none; position: fixed; inset: 0; background: rgba(0,0,0,0.85);
    z-index: 1000; align-items: center; justify-content: center; cursor: zoom-out;
  }
  .modal-overlay.active { display: flex; }
  .modal-content {
    max-width: 95vw; max-height: 90vh; position: relative;
  }
  .modal-content img { max-width: 95vw; max-height: 85vh; border-radius: 6px; box-shadow: 0 8px 32px rgba(0,0,0,0.6); }
  .modal-label {
    text-align: center; padding: 10px; color: #e6edf3; font-size: 0.95em;
  }
  .modal-nav {
    position: absolute; top: 50%; transform: translateY(-50%); font-size: 2em;
    background: rgba(255,255,255,0.1); border: none; color: #e6edf3;
    cursor: pointer; padding: 12px 16px; border-radius: 6px;
  }
  .modal-nav:hover { background: rgba(255,255,255,0.2); }
  .modal-prev { left: 16px; }
  .modal-next { right: 16px; }
${tabCss}
</style>
</head>
<body>
<h1>📊 Benchmark Report — ${escapeHtml(runName)}</h1>
<div class="subtitle">${escapeHtml(displayTime)} · ${allTrials.length} trials</div>

${bodyContent}

<!-- Modal -->
<div class="modal-overlay" id="modal" onclick="closeModal(event)">
  <button class="modal-nav modal-prev" onclick="navModal(-1, event)">‹</button>
  <div class="modal-content">
    <img id="modal-img" src="" alt=""/>
    <div class="modal-label" id="modal-label"></div>
  </div>
  <button class="modal-nav modal-next" onclick="navModal(1, event)">›</button>
</div>

<script>
// ═══════════════════════════════════════════════════
// Scenario-aware report logic
// ═══════════════════════════════════════════════════
const S = ${JSON.stringify(jsScenarioData)};
const multiScenario = ${multiScenario};
let activeScenario = 0;
let currentModalScenario = 0;
let currentModalIdx = 0;

// Per-scenario runtime state
const scenarioState = S.map(s => ({
  activeFilters: {
    conditions: new Set(s.uniqueConditions),
    models: new Set(s.uniqueModels),
  },
  charts: {},
}));

function sfx(si) { return multiScenario ? '-' + si : ''; }

function escHtml(s) {
  const d = document.createElement('div');
  d.textContent = s;
  return d.innerHTML;
}

// ── Tab switching ──
function switchTab(idx) {
  document.querySelectorAll('.scenario-tab').forEach((t, i) => t.classList.toggle('active', i === idx));
  document.querySelectorAll('.scenario-panel').forEach((p, i) => p.classList.toggle('active', i === idx));
  activeScenario = idx;
  setTimeout(() => renderCharts(idx), 50);
}

// ── Modal ──
function openModal(scenarioIdx, idx) {
  currentModalScenario = scenarioIdx;
  currentModalIdx = idx;
  const d = S[scenarioIdx].modalData[idx];
  if (!d || !d.src) return;
  document.getElementById('modal-img').src = d.src;
  document.getElementById('modal-label').textContent = d.label;
  document.getElementById('modal').classList.add('active');
}
function closeModal(e) {
  if (e.target === document.getElementById('modal') || e.target.classList.contains('modal-overlay'))
    document.getElementById('modal').classList.remove('active');
}
function navModal(dir, e) {
  e.stopPropagation();
  const mdata = S[currentModalScenario].modalData;
  currentModalIdx = (currentModalIdx + dir + mdata.length) % mdata.length;
  openModal(currentModalScenario, currentModalIdx);
}
document.addEventListener('keydown', e => {
  const m = document.getElementById('modal');
  if (m.classList.contains('active')) {
    if (e.key === 'Escape') m.classList.remove('active');
    else if (e.key === 'ArrowLeft') navModal(-1, e);
    else if (e.key === 'ArrowRight') navModal(1, e);
    return;
  }
  if (e.key === 'Escape') hideDetail(activeScenario);
});

// ── Detail panel ──
function showDetail(scenarioIdx, reqIdx, trialIdx) {
  const req = S[scenarioIdx].detailData[reqIdx];
  if (!req) return;
  const trial = req.trials[trialIdx];
  if (!trial) return;
  const s = sfx(scenarioIdx);
  const panel = document.getElementById('detail-panel' + s);
  const title = document.getElementById('detail-title' + s);
  const body = document.getElementById('detail-body' + s);
  title.textContent = req.reqLabel;
  const statusLabel = trial.status === 'pass' ? '✅ Passed' : '❌ Failed';
  const cls = trial.status === 'pass' ? 'pass' : 'fail';
  let html = '<div class="detail-body-inner">';
  html += '<div class="detail-item ' + cls + '">';
  html += '<div class="detail-trial">' + escHtml(trial.condition) + ' · ' + escHtml(trial.model) + '</div>';
  html += '<div class="detail-status">' + statusLabel + '</div>';
  if (trial.reason) {
    html += '<div class="detail-reason">' + escHtml(trial.reason) + '</div>';
  }
  html += '</div></div>';
  body.innerHTML = html;
  panel.style.display = 'block';
  panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

function showReqDetail(scenarioIdx, reqIdx) {
  const req = S[scenarioIdx].detailData[reqIdx];
  if (!req) return;
  const s = sfx(scenarioIdx);
  const panel = document.getElementById('detail-panel' + s);
  const title = document.getElementById('detail-title' + s);
  const body = document.getElementById('detail-body' + s);
  title.textContent = req.reqLabel;
  let html = '<div class="detail-body-inner">';
  for (const trial of req.trials) {
    if (!trial) continue;
    const statusLabel = trial.status === 'pass' ? '✅ Passed' : '❌ Failed';
    const cls = trial.status === 'pass' ? 'pass' : 'fail';
    html += '<div class="detail-item ' + cls + '">';
    html += '<div class="detail-trial">' + escHtml(trial.condition) + ' · ' + escHtml(trial.model) + '</div>';
    html += '<div class="detail-status">' + statusLabel + '</div>';
    if (trial.reason) {
      html += '<div class="detail-reason">' + escHtml(trial.reason) + '</div>';
    }
    html += '</div>';
  }
  html += '</div>';
  body.innerHTML = html;
  panel.style.display = 'block';
  panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

function hideDetail(scenarioIdx) {
  document.getElementById('detail-panel' + sfx(scenarioIdx)).style.display = 'none';
}

// ── Filters ──
function toggleFilter(el) {
  el.classList.toggle('active');
  const si = parseInt(el.dataset.scenario);
  const type = el.dataset.type;
  const val = el.dataset.value;
  const set = type === 'condition' ? scenarioState[si].activeFilters.conditions : scenarioState[si].activeFilters.models;
  if (set.has(val)) set.delete(val); else set.add(val);
  renderCharts(si);
}

function getFilteredTrials(si) {
  const st = scenarioState[si];
  return S[si].chartTrials.filter(t => st.activeFilters.conditions.has(t.condition) && st.activeFilters.models.has(t.model));
}

// ═══════════════════════════════════════════════════
// Chart.js Analysis Charts
// ═══════════════════════════════════════════════════
const CONDITION_COLORS = [
  '#58a6ff', '#3fb950', '#d29922', '#f85149', '#bc8cff',
  '#f0883e', '#79c0ff', '#56d364', '#e3b341', '#ff7b72',
];

const PRICING = {
  'claude-opus-4.6':   { input: 5, cached: 0.5, output: 25 },
  'claude-opus-4.5':   { input: 5, cached: 0.5, output: 25 },
  'claude-sonnet-4.5': { input: 3, cached: 0.3, output: 15 },
  'claude-sonnet-4.6': { input: 3, cached: 0.3, output: 15 },
  'claude-sonnet-4':   { input: 3, cached: 0.3, output: 15 },
  'claude-haiku-4.5':  { input: 1, cached: 0.1, output: 5 },
};

function parseTokenStr(s) {
  if (!s) return 0;
  const m = s.match(/^([\\d.]+)\\s*([mk])?$/i);
  if (!m) return 0;
  const n = parseFloat(m[1]);
  const u = (m[2] || '').toLowerCase();
  if (u === 'm') return n * 1000000;
  if (u === 'k') return n * 1000;
  return n;
}

function estimateCost(model, inputTok, outputTok, cachedTok) {
  const p = PRICING[model] || PRICING['claude-sonnet-4.5'];
  const inM = parseTokenStr(inputTok) / 1e6;
  const outM = parseTokenStr(outputTok) / 1e6;
  const cacheM = parseTokenStr(cachedTok) / 1e6;
  const uncached = Math.max(0, inM - cacheM);
  return uncached * p.input * 2 + cacheM * p.cached + outM * p.output;
}

function parseTimeStr(s) {
  if (!s) return 0;
  let mins = 0, secs = 0;
  const mm = s.match(/(\\d+)m/); if (mm) mins = parseInt(mm[1]);
  const ss = s.match(/(\\d+)s/); if (ss) secs = parseInt(ss[1]);
  return mins * 60 + secs;
}

function conditionColor(si, condition) {
  const all = S[si].uniqueConditions;
  return CONDITION_COLORS[all.indexOf(condition) % CONDITION_COLORS.length];
}

function renderCharts(si) {
  const s = sfx(si);
  const filtered = getFilteredTrials(si);
  const state = scenarioState[si];
  const detailData = S[si].detailData;
  const chartTrials = S[si].chartTrials;
  const reqPassRates = S[si].reqPassRates;

  Object.values(state.charts).forEach(c => c.destroy());
  state.charts = {};

  Chart.defaults.color = '#8b949e';
  Chart.defaults.borderColor = '#21262d';
  Chart.defaults.font.family = "-apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif";

  // 1. Score by Condition
  const condGroups = {};
  filtered.forEach(t => {
    if (!condGroups[t.condition]) condGroups[t.condition] = [];
    condGroups[t.condition].push(t);
  });
  const condLabels = Object.keys(condGroups);
  const maxIters = Math.max(1, ...condLabels.map(c => condGroups[c].length));
  const scoreDatasets = [];
  for (let i = 0; i < maxIters; i++) {
    scoreDatasets.push({
      label: 'Iter ' + (i + 1),
      data: condLabels.map(c => condGroups[c][i]?.score ?? null),
      backgroundColor: condLabels.map(c => conditionColor(si, c) + (i === 0 ? 'cc' : '66')),
      borderColor: condLabels.map(c => conditionColor(si, c)),
      borderWidth: 1,
    });
  }
  state.charts.scores = new Chart(document.getElementById('chart-scores' + s), {
    type: 'bar',
    data: { labels: condLabels, datasets: scoreDatasets },
    options: {
      responsive: true, maintainAspectRatio: false,
      scales: { y: { beginAtZero: true, max: 100, title: { display: true, text: 'Score' } } },
      plugins: {
        legend: { display: maxIters > 1 },
        tooltip: {
          callbacks: {
            afterLabel: (ctx) => {
              const t = condGroups[condLabels[ctx.dataIndex]]?.[ctx.datasetIndex];
              return t ? t.name : '';
            }
          }
        }
      }
    }
  });

  // 2. Score vs Tokens
  const bubbleDatasets = [];
  Object.entries(condGroups).forEach(([cond, cTrials]) => {
    const color = conditionColor(si, cond);
    const pts = cTrials.map(t => ({
      x: t.score,
      y: (parseTokenStr(t.inputTokens) + parseTokenStr(t.outputTokens)) / 1e6,
      r: 4,
      label: t.name,
      condition: cond,
    }));
    bubbleDatasets.push({
      label: cond,
      data: pts,
      backgroundColor: color + '55',
      borderColor: color + '88',
      borderWidth: 1,
      hoverRadius: 6,
    });
    if (pts.length >= 1) {
      const avgX = pts.reduce((a, p) => a + p.x, 0) / pts.length;
      const avgY = pts.reduce((a, p) => a + p.y, 0) / pts.length;
      const n = pts.length;
      bubbleDatasets.push({
        label: '_mean_' + cond,
        data: [{ x: avgX, y: avgY, r: 14 + n * 3 }],
        backgroundColor: color + '33',
        borderColor: color,
        borderWidth: 2,
        hoverRadius: 0,
      });
    }
  });
  state.charts.costQuality = new Chart(document.getElementById('chart-cost-quality' + s), {
    type: 'bubble',
    data: { datasets: bubbleDatasets },
    options: {
      responsive: true, maintainAspectRatio: false,
      scales: {
        x: { title: { display: true, text: 'Score' }, beginAtZero: true, max: 100 },
        y: { title: { display: true, text: 'Total Tokens (millions)' }, beginAtZero: true },
      },
      plugins: {
        legend: {
          labels: { filter: (item) => !item.text.startsWith('_mean_') }
        },
        tooltip: {
          callbacks: {
            label: (ctx) => {
              const pt = ctx.raw;
              if (ctx.dataset.label.startsWith('_mean_')) {
                const cond = ctx.dataset.label.replace('_mean_', '');
                const trials = condGroups[cond];
                const scores = trials.map(t => t.score);
                const avg = (scores.reduce((a, b) => a + b, 0) / scores.length).toFixed(1);
                const std = Math.sqrt(scores.reduce((s, v) => s + (v - avg) ** 2, 0) / scores.length).toFixed(1);
                return cond + ' — avg: ' + avg + ' ± ' + std + ' (' + trials.length + ' trials, ' + pt.y.toFixed(1) + 'M tokens)';
              }
              return pt.condition + ': ' + pt.x + '/100 (' + pt.y.toFixed(1) + 'M) — ' + pt.label;
            }
          }
        }
      }
    }
  });

  // 3. Efficiency
  const effData = {};
  filtered.forEach(t => {
    if (!effData[t.condition]) effData[t.condition] = { scores: [], tokens: [], times: [] };
    const totalTokens = (parseTokenStr(t.inputTokens) + parseTokenStr(t.outputTokens)) / 1e6;
    effData[t.condition].scores.push(t.score);
    effData[t.condition].tokens.push(totalTokens);
    effData[t.condition].times.push(parseTimeStr(t.sessionTime));
  });
  const effLabels = Object.keys(effData);
  const effValues = effLabels.map(c => {
    const d = effData[c];
    const avgScore = d.scores.reduce((a, b) => a + b, 0) / d.scores.length;
    const avgTokens = d.tokens.reduce((a, b) => a + b, 0) / d.tokens.length;
    const avgTime = d.times.reduce((a, b) => a + b, 0) / d.times.length;
    const stddev = Math.sqrt(d.scores.reduce((sum, s) => sum + (s - avgScore) ** 2, 0) / d.scores.length);
    return { avgScore, avgTokens, avgTime, stddev, ratio: avgTokens > 0 ? avgScore / avgTokens : 0, count: d.scores.length };
  });
  const effSorted = effLabels.map((c, i) => ({ label: c, ...effValues[i] })).sort((a, b) => b.ratio - a.ratio);
  state.charts.efficiency = new Chart(document.getElementById('chart-efficiency' + s), {
    type: 'bar',
    data: {
      labels: effSorted.map(e => e.label),
      datasets: [{
        label: 'Score/M tokens',
        data: effSorted.map(e => Math.round(e.ratio * 10) / 10),
        backgroundColor: effSorted.map(e => conditionColor(si, e.label) + 'aa'),
        borderColor: effSorted.map(e => conditionColor(si, e.label)),
        borderWidth: 1,
      }]
    },
    options: {
      responsive: true, maintainAspectRatio: false, indexAxis: 'y',
      scales: { x: { title: { display: true, text: 'Score per Million Tokens' }, beginAtZero: true } },
      plugins: {
        legend: { display: false },
        tooltip: {
          callbacks: {
            afterLabel: (ctx) => {
              const e = effSorted[ctx.dataIndex];
              return 'Avg Score: ' + e.avgScore.toFixed(1) + ' ± ' + e.stddev.toFixed(1) +
                '\\nAvg Tokens: ' + e.avgTokens.toFixed(1) + 'M' +
                '\\nAvg Time: ' + Math.round(e.avgTime / 60) + 'm ' + Math.round(e.avgTime % 60) + 's' +
                '\\nTrials: ' + e.count;
            }
          }
        }
      }
    }
  });

  // 4. Subscore Breakdown
  const subGroups = {};
  filtered.forEach(t => {
    if (!subGroups[t.condition]) subGroups[t.condition] = { project: [], ui: [], visual: [], functionality: [] };
    subGroups[t.condition].project.push(t.project);
    subGroups[t.condition].ui.push(t.ui);
    subGroups[t.condition].visual.push(t.visual);
    subGroups[t.condition].functionality.push(t.functionality);
  });
  const subLabels = Object.keys(subGroups);
  const avg = arr => arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : 0;
  state.charts.subscores = new Chart(document.getElementById('chart-subscores' + s), {
    type: 'bar',
    data: {
      labels: subLabels,
      datasets: [
        ...(filtered.length && filtered.every(t => t.v2) ? [
          { label: 'Requirements', data: subLabels.map(c => avg(subGroups[c].project)), backgroundColor: '#58a6ffaa' },
          { label: 'Quality checks', data: subLabels.map(c => avg(subGroups[c].ui)), backgroundColor: '#3fb950aa' },
          { label: 'Design', data: subLabels.map(c => avg(subGroups[c].visual)), backgroundColor: '#d29922aa' },
        ] : [
          { label: 'Project', data: subLabels.map(c => avg(subGroups[c].project)), backgroundColor: '#58a6ffaa' },
          { label: 'UI', data: subLabels.map(c => avg(subGroups[c].ui)), backgroundColor: '#3fb950aa' },
          { label: 'Visual', data: subLabels.map(c => avg(subGroups[c].visual)), backgroundColor: '#d29922aa' },
          { label: 'Functionality', data: subLabels.map(c => avg(subGroups[c].functionality)), backgroundColor: '#bc8cffaa' },
        ]),
      ]
    },
    options: {
      responsive: true, maintainAspectRatio: false,
      scales: { y: { beginAtZero: true, max: 10, title: { display: true, text: 'Subscore (0-10)' } } },
      plugins: { legend: { position: 'bottom' } }
    }
  });
}

// Initial render
if (typeof Chart !== 'undefined') {
  renderCharts(0);
} else {
  document.querySelectorAll('.charts-section').forEach(el => {
    el.innerHTML = '<p style="color:#f85149">Chart.js failed to load. Charts require internet access.</p>';
  });
}
</script>
</body>
</html>`;

  const outPath = join(runDir, "index.html");
  writeFileSync(outPath, html);
  return outPath;
}
