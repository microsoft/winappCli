#!/usr/bin/env node
// Builds the static benchmark dashboard from trial result files.
//   node benchmarks/dashboard/build.mjs --data <dir> [--label <text>] [--data <dir> [--label <text>] ...] --out <dir>
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildTrends, CONTROL_SHIFT } from './lib/trends.mjs';
import { aggregateRelease, compareEntries, BASE_ARM, SKILLS_ARM, OUTLIER_RULES, SANITY_PASS_RATE } from './lib/aggregate.mjs';
import { loadData } from './lib/load.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const DEFAULT_RAW_DATA_URL = 'https://github.com/microsoft/winappCli/tree/benchmark-data';

// Method changes: method-changes.json next to this script, plus a method-changes.json at the
// top of any --data folder. Entries with the same id are replaced by the later file.
export function loadMethodChanges(dirs, warnings = []) {
  const byId = new Map();
  for (const file of [join(here, 'method-changes.json'), ...dirs.map((d) => join(d, 'method-changes.json'))]) {
    if (!existsSync(file)) continue;
    let list;
    try {
      list = JSON.parse(readFileSync(file, 'utf8'));
    } catch (e) {
      warnings.push(`${file}: not valid JSON (${e.message}); skipped`);
      continue;
    }
    if (!Array.isArray(list)) {
      warnings.push(`${file}: expected an array of method changes; skipped`);
      continue;
    }
    for (const c of list) {
      if (!c?.id || !/^\d{4}-\d{2}-\d{2}$/.test(c.date ?? '') || !c.title) {
        warnings.push(`${file}: a method change needs id, date (YYYY-MM-DD) and title; skipped ${JSON.stringify(c?.id ?? c)}`);
        continue;
      }
      byId.set(c.id, c);
    }
  }
  return [...byId.values()].sort((a, b) => a.date.localeCompare(b.date));
}

// sources: folder paths, or { dir, label } pairs. See loadData.
export function buildSiteData(sources, { rawDataUrl = DEFAULT_RAW_DATA_URL, now = new Date() } = {}) {
  const { releases, labels, warnings } = loadData(sources);
  const out = [...releases.keys()].map((k) => aggregateRelease(k, releases.get(k), { label: labels.has(k) ? k : null })).sort(compareEntries);
  const methodChanges = loadMethodChanges(sources.map((s) => (typeof s === 'string' ? s : s.dir)), warnings);
  return {
    generatedAt: now.toISOString(),
    rawDataUrl,
    rules: { sanityPassRate: SANITY_PASS_RATE, baseArm: BASE_ARM, skillsArm: SKILLS_ARM, controlShift: CONTROL_SHIFT, ...OUTLIER_RULES },
    warnings,
    releases: out,
    methodChanges,
    trends: buildTrends(out, methodChanges),
  };
}

const USAGE = 'Usage: node build.mjs --data <folder> [--label <text>] [--data <folder> [--label <text>] ...] --out <folder> [--raw-data-url <url>]';

// --label applies to the --data just before it, so the order of the arguments matters.
export function parseCli(argv) {
  const opts = { sources: [], out: null, rawDataUrl: null, help: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const value = () => {
      const v = argv[++i];
      if (v === undefined || v.startsWith('--')) throw new Error(`${a} needs a value`);
      return v;
    };
    if (a === '--data') opts.sources.push({ dir: value(), label: null });
    else if (a === '--label') {
      const last = opts.sources.at(-1);
      if (!last || last.label) throw new Error('--label must follow a --data folder');
      last.label = value();
    } else if (a === '--out') opts.out = value();
    else if (a === '--raw-data-url') opts.rawDataUrl = value();
    else if (a === '--help' || a === '-h') opts.help = true;
    else throw new Error(`Unknown argument: ${a}`);
  }
  return opts;
}

function main() {
  let opts;
  try {
    opts = parseCli(process.argv.slice(2));
  } catch (e) {
    console.error(`${e.message}\n${USAGE}`);
    process.exit(2);
  }
  if (opts.help || !opts.sources.length || !opts.out) {
    console.log(USAGE);
    process.exit(opts.help ? 0 : 2);
  }
  const sources = opts.sources.map((s) => ({ dir: resolve(s.dir), label: s.label }));
  const outDir = resolve(opts.out);
  const data = buildSiteData(sources, { rawDataUrl: opts.rawDataUrl ?? DEFAULT_RAW_DATA_URL });
  if (!data.releases.length) {
    console.error('No trial files found. Point --data at a folder that contains dev or vX.Y.Z release folders.');
    process.exit(1);
  }

  mkdirSync(outDir, { recursive: true });
  for (const f of readdirSync(join(here, 'site'))) copyFileSync(join(here, 'site', f), join(outDir, f));
  const json = JSON.stringify(data);
  writeFileSync(join(outDir, 'data.json'), json);
  // data.js lets index.html open straight from disk, where browsers block fetch().
  writeFileSync(join(outDir, 'data.js'), `window.BENCH_DATA = ${json};\n`);

  for (const r of data.releases) {
    const parts = Object.entries(r.benchmarks).map(([b, v]) => `${b} ${v.trialCount}`);
    console.log(`${r.id}: ${r.trialCount} trials (${parts.join(', ')})`);
  }
  for (const w of data.warnings) console.warn(`warning: ${w}`);
  console.log(`Wrote ${join(outDir, 'index.html')}`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
