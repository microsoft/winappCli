// Trends across runs: one point per dropdown entry (run or release), ordered by date, with
// method changes marked between points. Pure functions over aggregateRelease() output.
import { BASE_ARM, SKILLS_ARM } from './aggregate.mjs';

// If barebones (the control) moves more than this between comparable runs, something other
// than the skills changed. Also used to flag apps that get easier for barebones over time.
export const CONTROL_SHIFT = 0.05;

const RATE_KEYS = ['qualityRate', 'packagingRate', 'reqRate', 'passRate'];
const day = (iso) => (iso ? iso.slice(0, 10) : '');

function pick(m) {
  const out = { valid: m.valid, total: m.total };
  for (const k of RATE_KEYS) out[k] = m[k];
  return out;
}

// `comparable` is true/false, or an object of arm -> true/false (missing arms are false).
export function comparableFor(change, arm) {
  const c = change.comparable;
  if (c && typeof c === 'object') return c[arm] === true;
  return c === true;
}

function maxScoring(b) {
  const v = b.scoringVersions.map(Number).filter(Number.isFinite);
  return v.length ? Math.max(...v) : null;
}

// Method changes that fall between two points of one benchmark. A change with a
// scoringVersion applies where that version is first reached; otherwise its date must be
// after the previous run ended and no later than the day the next run started.
export function changesBetween(prev, cur, benchmark, methodChanges) {
  const pb = prev.benchmarks[benchmark], cb = cur.benchmarks[benchmark];
  const out = [];
  for (const c of methodChanges) {
    if (c.affects && !c.affects.includes(benchmark)) continue;
    if (c.scoringVersion != null) {
      const a = maxScoring(pb), b = maxScoring(cb);
      if (a != null && b != null && a < c.scoringVersion && b >= c.scoringVersion) out.push(c);
    } else if (c.date > day(pb.lastStartedAt) && c.date <= day(cb.firstStartedAt)) {
      out.push(c);
    }
  }
  // A scoring version change with no changelog entry still breaks comparability.
  const a = maxScoring(pb), b = maxScoring(cb);
  if (a != null && b != null && a !== b && !out.some((c) => c.scoringVersion != null)) {
    out.push({ id: `scoring-${a}-${b}`, date: day(cb.firstStartedAt), title: `Scoring version changed (${a} to ${b})`, affects: [benchmark], comparable: false, detail: 'No changelog entry describes this change.' });
  }
  return out;
}

function point(entry, b) {
  const arms = {};
  for (const arm of b.arms) {
    const h = b.headline[arm];
    const byModel = {};
    for (const [m, v] of Object.entries(h.byModel)) byModel[m] = pick(v);
    arms[arm] = { all: pick(h.all), byModel };
  }
  const gapOf = (w, o) => {
    const out = {};
    for (const k of RATE_KEYS) out[k] = w?.[k] == null || o?.[k] == null ? null : w[k] - o[k];
    return out;
  };
  const gap = { all: gapOf(arms[SKILLS_ARM]?.all, arms[BASE_ARM]?.all), byModel: {} };
  for (const m of b.models) gap.byModel[m] = gapOf(arms[SKILLS_ARM]?.byModel[m], arms[BASE_ARM]?.byModel[m]);
  const scenarios = {};
  for (const s of b.perScenario) if (s.cells[BASE_ARM]) scenarios[s.scenario] = pick(s.cells[BASE_ARM].all);
  return {
    id: entry.id,
    name: entry.label ?? entry.id,
    firstStartedAt: b.firstStartedAt,
    lastStartedAt: b.lastStartedAt,
    info: {
      commits: b.commits, pluginsCommits: b.pluginsCommits ?? [], winappVersions: b.winappVersions,
      scoringVersions: b.scoringVersions, models: b.models,
    },
    arms,
    gap,
    scenarios,
  };
}

// One benchmark's trend over entries (already filtered to one series).
export function trendSeries(entries, benchmark, methodChanges = []) {
  const withB = entries.filter((e) => e.benchmarks[benchmark])
    .sort((a, b) => (a.benchmarks[benchmark].firstStartedAt ?? '').localeCompare(b.benchmarks[benchmark].firstStartedAt ?? ''));
  const points = withB.map((e) => point(e, e.benchmarks[benchmark]));
  const arms = [...new Set(points.flatMap((p) => Object.keys(p.arms)))];
  const steps = [];
  for (let i = 1; i < withB.length; i++) {
    const changes = changesBetween(withB[i - 1], withB[i], benchmark, methodChanges);
    const comparable = {};
    for (const arm of arms) comparable[arm] = changes.every((c) => comparableFor(c, arm));
    comparable.gap = comparable[BASE_ARM] !== false && comparable[SKILLS_ARM] !== false;
    steps.push({ from: points[i - 1].id, to: points[i].id, changes: changes.map(({ id, title, date, detail }) => ({ id, title, date, detail: detail ?? '' })), comparable });
  }

  // barebones is the control: it should not move between comparable runs.
  const notes = [];
  steps.forEach((s, i) => {
    if (!s.comparable[BASE_ARM]) return;
    const a = points[i].arms[BASE_ARM]?.all, b = points[i + 1].arms[BASE_ARM]?.all;
    if (!a || !b) return;
    const moved = RATE_KEYS.filter((k) => a[k] != null && b[k] != null && Math.abs(b[k] - a[k]) > CONTROL_SHIFT)
      .map((k) => ({ metric: k, from: a[k], to: b[k] }));
    if (moved.length) notes.push({ kind: 'control-moved', from: s.from, to: s.to, moved });
  });

  // Decay: apps (or tasks) that get easier for barebones over comparable steps.
  const names = [...new Set(points.flatMap((p) => Object.keys(p.scenarios)))].sort();
  const decayKey = benchmark === 'apps' ? 'qualityRate' : 'passRate';
  const decay = names.map((name) => {
    let rise = 0;
    steps.forEach((s, i) => {
      if (!s.comparable[BASE_ARM]) return;
      const a = points[i].scenarios[name]?.[decayKey], b = points[i + 1].scenarios[name]?.[decayKey];
      if (a != null && b != null) rise += b - a;
    });
    return { scenario: name, metric: decayKey, values: points.map((p) => p.scenarios[name] ?? null), rise, rising: rise > CONTROL_SHIFT };
  });

  return { benchmark, points, steps, notes, decay };
}

// Dev runs and releases are separate series (#1182: the release trend uses releases only).
export function buildTrends(entries, methodChanges = []) {
  const isRelease = (e) => e.releases.length > 0 && e.releases.every((r) => r !== 'dev');
  const series = { dev: entries.filter((e) => !isRelease(e)), release: entries.filter(isRelease) };
  const out = {};
  for (const [name, list] of Object.entries(series)) {
    out[name] = {};
    for (const b of ['apps', 'tasks']) out[name][b] = trendSeries(list, b, methodChanges);
  }
  return out;
}
