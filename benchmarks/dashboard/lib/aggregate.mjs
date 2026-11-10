// Pure functions that turn trial result files (benchmarks/RESULTS-FORMAT.md) into the
// numbers the dashboard shows. No file system access here, so tests can feed objects directly.

// The three arms, then the tasks debugging configurations.
export const ARM_ORDER = ['barebones', 'cli-only', 'cli-plus-skills', 'winapp-skills', 'winui-skills', 'full-suite'];

export const MODEL_ORDER = ['claude-sonnet-5.5', 'claude-opus-5.5', 'gpt-6.1-sol'];

// A scenario that the `barebones` arm passes this often is a sanity check (#1253).
export const SANITY_PASS_RATE = 0.9;

// The "change" shown on the dashboard is SKILLS_ARM minus BASE_ARM.
export const BASE_ARM = 'barebones';
export const SKILLS_ARM = 'cli-plus-skills';

// Flags that tell a reader where to look closer. Thresholds are shown on the page.
// apps quality-check topics for the Skills tab: which check ids belong to each topic, and
// the skills that teach it. This is a setup-level view (barebones vs cli-plus-skills), not
// evidence that one skill helped.
export const QUALITY_TOPICS = [
  { topic: 'Packaging', checks: ['p2', 'p3'], skills: ['winapp-manifest', 'winui-packaging', 'winui-dev-workflow'] },
  { topic: 'Build warnings', checks: ['q4'], skills: ['winui-dev-workflow'] },
  { topic: 'Accessible names', checks: ['q0'], skills: ['winui-design', 'winapp-ui-automation'] },
  { topic: 'Theme colors', checks: ['v5'], skills: ['winui-design'] },
  { topic: 'Typography', checks: ['v3'], skills: ['winui-design'] },
  { topic: 'Backdrop', checks: ['v2'], skills: ['winui-design'] },
  { topic: 'x:Bind', checks: ['q3'], skills: ['winui-design'] },
  { topic: 'Empty, loading and error states', checks: ['q5'], skills: ['winui-design'] },
];

// Pass rate of each topic's checks per arm, over valid runs. Checks are matched by id,
// case-insensitively. Topics with no matching check in any run are left out.
export function qualityTopics(trials, arms) {
  const out = [];
  for (const q of QUALITY_TOPICS) {
    const byArm = {};
    for (const arm of arms) {
      let passed = 0, total = 0;
      for (const t of trials) {
        if (t.arm !== arm || t.outcome !== 'valid') continue;
        for (const c of t.checks ?? []) {
          if (!q.checks.includes(String(c.id).toLowerCase())) continue;
          total++;
          if (c.passed) passed++;
        }
      }
      byArm[arm] = { passed, total, rate: ratio(passed, total) };
    }
    if (!arms.some((a) => byArm[a].total)) continue;
    const w = byArm[SKILLS_ARM]?.rate, o = byArm[BASE_ARM]?.rate;
    out.push({ ...q, byArm, change: w == null || o == null ? null : w - o });
  }
  return out;
}

export const OUTLIER_RULES = {
  modelSplitPoints: 0.5, // a model's pass rate differs from the other models' by this much
  costMultiple: 1.5, // a model's mean credits on one arm are this many times its mean on the other arms
};

const RELEASE_RE = /^(dev|v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?)$/;

export function isReleaseName(name) {
  return RELEASE_RE.test(name);
}

function orderBy(values, order) {
  return [...new Set(values)].sort((a, b) => {
    const ia = order.indexOf(a), ib = order.indexOf(b);
    if (ia !== -1 || ib !== -1) return (ia === -1 ? Infinity : ia) - (ib === -1 ? Infinity : ib);
    return a.localeCompare(b);
  });
}

// Most recent run first, by the latest startedAt in the entry; ties fall back to release order.
export function compareEntries(a, b) {
  return (b.lastStartedAt ?? '').localeCompare(a.lastStartedAt ?? '') || compareReleases(a.id, b.id);
}

// Newest release first; `dev` last.
export function compareReleases(a, b) {
  if (a === b) return 0;
  if (a === 'dev') return 1;
  if (b === 'dev') return -1;
  const pa = a.slice(1).split(/[.-]/).map(Number), pb = b.slice(1).split(/[.-]/).map(Number);
  for (let i = 0; i < 3; i++) if (pa[i] !== pb[i]) return pb[i] - pa[i];
  return b.localeCompare(a);
}

// Returns a list of problems; an empty list means the trial can be used.
export function checkTrial(t) {
  const problems = [];
  if (!t || typeof t !== 'object' || Array.isArray(t)) return ['not a JSON object'];
  if (t.schemaVersion !== 1) problems.push(`schemaVersion is ${JSON.stringify(t.schemaVersion)}, expected 1`);
  if (t.benchmark !== 'apps' && t.benchmark !== 'tasks') problems.push(`unknown benchmark ${JSON.stringify(t.benchmark)}`);
  for (const f of ['scenario', 'arm', 'model', 'outcome', 'release']) {
    if (typeof t[f] !== 'string' || !t[f]) problems.push(`missing ${f}`);
  }
  if (!Number.isInteger(t.trial)) problems.push('missing trial number');
  if (typeof t.passed !== 'boolean') problems.push('missing passed');
  if (!Array.isArray(t.checks)) problems.push('missing checks');
  if (!t.usage || typeof t.usage !== 'object') problems.push('missing usage');
  return problems;
}

// 95% Wilson score interval for a pass rate.
export function wilson(successes, n, z = 1.96) {
  if (!n) return null;
  const p = successes / n, z2 = z * z;
  const center = (p + z2 / (2 * n)) / (1 + z2 / n);
  const half = (z * Math.sqrt((p * (1 - p)) / n + z2 / (4 * n * n))) / (1 + z2 / n);
  return [Math.max(0, center - half), Math.min(1, center + half)];
}

function mean(values) {
  const v = values.filter((x) => typeof x === 'number' && Number.isFinite(x));
  return v.length ? v.reduce((s, x) => s + x, 0) / v.length : null;
}

const ratio = (met, total) => (total ? met / total : null);

// apps: judge `requirement-N` checks are the scenario requirements. Every other improvement
// check (harness quality checks, and judge quality checks such as q5) is a quality check.
export function isRequirementCheck(c) {
  return /^requirement-\d+$/.test(c.id);
}

export function credits(t) {
  const n = t.usage?.nanoAiu;
  return typeof n === 'number' ? n / 1e9 : null;
}

export function minutes(t) {
  const ms = t.usage?.wallClockMs;
  return typeof ms === 'number' ? ms / 60000 : null;
}

export function tokens(t) {
  const tk = t.usage?.tokens;
  if (!tk) return null;
  return (tk.input ?? 0) + (tk.output ?? 0);
}

// Metrics for any group of trials. Rates and means use valid trials only; `total` and
// `outcomes` count every trial so nothing is hidden.
export function metrics(trials) {
  const valid = trials.filter((t) => t.outcome === 'valid');
  const passed = valid.filter((t) => t.passed).length;
  let qualityMet = 0, qualityTotal = 0, reqMet = 0, reqTotal = 0, impMet = 0, impTotal = 0;
  for (const t of valid) {
    impMet += t.improvements?.met ?? 0;
    impTotal += t.improvements?.total ?? 0;
    for (const c of t.checks ?? []) {
      if (isRequirementCheck(c)) {
        reqTotal++;
        if (c.passed) reqMet++;
      } else if (c.kind === 'improvement') {
        qualityTotal++;
        if (c.passed) qualityMet++;
      }
    }
  }
  const outcomes = {};
  for (const t of trials) outcomes[t.outcome] = (outcomes[t.outcome] ?? 0) + 1;
  // apps only: launchLevel is omitted when level 2 could not be measured.
  const launchLevels = {};
  for (const t of trials.filter((x) => x.benchmark === 'apps')) {
    const k = Number.isInteger(t.launchLevel) ? String(t.launchLevel) : 'not measured';
    launchLevels[k] = (launchLevels[k] ?? 0) + 1;
  }
  const design = valid.map((t) => t.judge?.designScore).filter((x) => typeof x === 'number');
  // apps only: "works" means the app built and launched. Every run is in the denominator,
  // including runs that are not valid, so a build failure counts against the arm.
  const apps = trials.filter((t) => t.benchmark === 'apps');
  const checkPassed = (t, id) => (t.checks ?? []).some((c) => c.id === id && c.passed);
  const worked = apps.filter((t) => checkPassed(t, 'builds') && checkPassed(t, 'launches')).length;
  return {
    total: trials.length,
    valid: valid.length,
    worked,
    worksRate: ratio(worked, apps.length),
    passed,
    passRate: ratio(passed, valid.length),
    passCi: wilson(passed, valid.length),
    improvementsMet: impMet,
    improvementsTotal: impTotal,
    improvementsRate: ratio(impMet, impTotal),
    qualityMet,
    qualityTotal,
    qualityRate: ratio(qualityMet, qualityTotal),
    reqMet,
    reqTotal,
    reqRate: ratio(reqMet, reqTotal),
    designMean: mean(design),
    designN: design.length,
    creditsMean: mean(valid.map(credits)),
    creditsSpent: trials.reduce((s, t) => s + (credits(t) ?? 0), 0),
    minutesMean: mean(valid.map(minutes)),
    tokensMean: mean(valid.map(tokens)),
    askedBeforeAdmin: outcomes['asked-before-admin'] ?? 0,
    launchLevels,
    outcomes,
  };
}

// Metrics for every arm, overall and per model.
function byArmModel(trials, arms, models) {
  const out = {};
  for (const arm of arms) {
    const at = trials.filter((t) => t.arm === arm);
    out[arm] = { all: metrics(at), byModel: {} };
    for (const model of models) out[arm].byModel[model] = metrics(at.filter((t) => t.model === model));
  }
  return out;
}

const pct = (x) => `${Math.round(x * 100)}%`;

function scenarioFlags(cells, arms, models) {
  const flags = [];
  for (const arm of arms) {
    const cell = cells[arm];
    if (!cell) continue;
    for (const m of models) {
      const mc = cell.byModel[m];
      if (!mc?.valid) continue;
      const others = models.filter((o) => o !== m).map((o) => cell.byModel[o]).filter((o) => o?.valid);
      const oValid = others.reduce((s, o) => s + o.valid, 0);
      const oPassed = others.reduce((s, o) => s + o.passed, 0);
      if (oValid && Math.abs(mc.passRate - oPassed / oValid) >= OUTLIER_RULES.modelSplitPoints) {
        flags.push({ kind: 'model-split', arm, model: m, text: `${m} on ${arm} passes ${mc.passed} of ${mc.valid}; the other models pass ${oPassed} of ${oValid}` });
      }
      // Compare a model with itself on the other arms: models differ in price, arms should not.
      const otherArms = mean(arms.filter((a) => a !== arm).map((a) => cells[a]?.byModel[m]?.creditsMean));
      if (otherArms && mc.creditsMean != null && mc.creditsMean > OUTLIER_RULES.costMultiple * otherArms) {
        flags.push({ kind: 'cost', arm, model: m, text: `${m} on ${arm} uses ${mc.creditsMean.toFixed(1)} credits per run, ${(mc.creditsMean / otherArms).toFixed(1)}x its ${otherArms.toFixed(1)} on the other arms` });
      }
    }
    const nonValid = cell.all.total - cell.all.valid;
    if (nonValid) flags.push({ kind: 'not-valid', arm, text: `${nonValid} of ${cell.all.total} runs on ${arm} are not valid` });
  }
  const base = cells.barebones?.all;
  if (base?.valid && base.passRate >= SANITY_PASS_RATE) {
    flags.push({ kind: 'sanity', arm: 'barebones', text: `barebones passes ${base.passed} of ${base.valid} valid runs (${pct(base.passRate)})` });
  }
  if (arms.some((a) => cells[a]?.all.valid) && arms.every((a) => !cells[a]?.all.passed)) {
    flags.push({ kind: 'no-pass', text: 'No setup passes this scenario. Check the checker for a bug' });
  }
  return flags;
}

function usageByArm(trials, arms, field, nameKey) {
  const out = {};
  for (const arm of arms) {
    const at = trials.filter((t) => t.arm === arm);
    const recorded = at.filter((t) => Array.isArray(t[field]));
    const names = {};
    for (const t of recorded) {
      for (const s of t[field]) {
        const name = s?.[nameKey];
        if (!name) continue;
        names[name] ??= { trials: 0, uses: 0 };
        names[name].trials++;
        names[name].uses += s.count ?? 1;
      }
    }
    out[arm] = { trials: at.length, recorded: recorded.length, names };
  }
  return out;
}

function diffMetrics(a, b) {
  const d = (k) => (a?.[k] == null || b?.[k] == null ? null : a[k] - b[k]);
  return {
    passRate: d('passRate'), worksRate: d('worksRate'), reqRate: d('reqRate'), qualityRate: d('qualityRate'),
    designMean: d('designMean'), creditsMean: d('creditsMean'), minutesMean: d('minutesMean'),
  };
}

// Effect: cli-plus-skills minus barebones, overall and per model. Null when either arm is missing.
export function armDiff(byArm) {
  const skill = byArm[SKILLS_ARM], base = byArm[BASE_ARM];
  if (!skill || !base) return null;
  const byModel = {};
  for (const m of Object.keys(skill.byModel)) byModel[m] = diffMetrics(skill.byModel[m], base.byModel[m]);
  return { arm: SKILLS_ARM, base: BASE_ARM, all: diffMetrics(skill.all, base.all), byModel };
}

export function aggregateBenchmark(trials) {
  const arms = orderBy(trials.map((t) => t.arm), ARM_ORDER);
  const models = orderBy(trials.map((t) => t.model), MODEL_ORDER);
  const scenarios = orderBy(trials.map((t) => t.scenario), []);
  const headline = byArmModel(trials, arms, models);

  const perScenario = scenarios.map((scenario) => {
    const st = trials.filter((t) => t.scenario === scenario);
    const cells = byArmModel(st, arms, models);
    const flags = scenarioFlags(cells, arms, models);
    return { scenario, set: st[0].set ?? null, cells, flags, sanity: flags.some((f) => f.kind === 'sanity'), effect: armDiff(cells) };
  });

  const sanityScenarios = perScenario.filter((s) => s.sanity).map((s) => s.scenario);
  const headlineExcludingSanity = byArmModel(trials.filter((t) => !sanityScenarios.includes(t.scenario)), arms, models);

  const notValid = trials
    .filter((t) => t.outcome !== 'valid')
    .map((t) => ({ file: t.file ?? null, scenario: t.scenario, arm: t.arm, model: t.model, trial: t.trial, outcome: t.outcome, error: t.error ?? null, credits: credits(t), minutes: minutes(t) }));

  return {
    arms,
    models,
    scenarios,
    outcomeNames: orderBy(trials.map((t) => t.outcome), ['valid', 'app-failed', 'build-failed', 'timed-out', 'harness-error', 'asked-before-admin']),
    launchLevelNames: orderBy(Object.keys(metrics(trials).launchLevels), ['0', '1', '2', '3', 'not measured']),
    trialCount: trials.length,
    headline,
    headlineExcludingSanity,
    effect: armDiff(headline),
    effectExcludingSanity: armDiff(headlineExcludingSanity),
    sanityScenarios,
    perScenario,
    notValid,
    skills: usageByArm(trials, arms, 'skills', 'name'),
    qualityTopics: qualityTopics(trials, arms),
    winappCommands: usageByArm(trials, arms, 'winappCommands', 'command'),
    scoringVersions: orderBy(trials.map((t) => (t.scoringVersion == null ? 'not recorded' : String(t.scoringVersion))), []),
    winappVersions: orderBy(trials.map((t) => t.winappVersion).filter(Boolean), []),
    // Run info per benchmark, so an entry that merges runs from different commits shows them.
    commits: orderBy(trials.map((t) => t.commit).filter(Boolean), []),
    // Optional: the commit the plugins came from, when the harness records it.
    pluginsCommits: orderBy(trials.map((t) => t.pluginsCommit).filter(Boolean), []),
    firstStartedAt: trials.map((t) => t.startedAt).filter(Boolean).sort()[0] ?? null,
    lastStartedAt: trials.map((t) => t.startedAt).filter(Boolean).sort().at(-1) ?? null,
  };
}

// Aggregates one dropdown entry. `id` is the --label when one was given, otherwise the
// release folder name. `release` is the release value the trials share, or null if mixed.
export function aggregateRelease(id, trials, { label = null } = {}) {
  const releaseValues = orderBy(trials.map((t) => t.release), []);
  const started = trials.map((t) => t.startedAt).filter(Boolean).sort();
  const benchmarks = {};
  for (const name of orderBy(trials.map((t) => t.benchmark), ['apps', 'tasks'])) {
    benchmarks[name] = aggregateBenchmark(trials.filter((t) => t.benchmark === name));
  }
  return {
    id,
    label,
    release: releaseValues.length === 1 ? releaseValues[0] : null,
    releases: releaseValues,
    commits: orderBy(trials.map((t) => t.commit).filter(Boolean), []),
    models: orderBy(trials.map((t) => t.model), MODEL_ORDER),
    firstStartedAt: started[0] ?? null,
    lastStartedAt: started.at(-1) ?? null,
    trialCount: trials.length,
    benchmarks,
  };
}
