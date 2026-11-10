#!/usr/bin/env node
// Builds the static benchmark dashboard from trial result files.
//   node benchmarks/dashboard/build.mjs --data <dir> [--data <dir> ...] --out <dir>
import { copyFileSync, mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { aggregateRelease, compareReleases, trend, OUTLIER_RULES, SANITY_PASS_RATE } from './lib/aggregate.mjs';
import { loadData } from './lib/load.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const DEFAULT_RAW_DATA_URL = 'https://github.com/microsoft/winappCli/tree/benchmark-data';

export function buildSiteData(roots, { rawDataUrl = DEFAULT_RAW_DATA_URL, now = new Date() } = {}) {
  const { releases, warnings } = loadData(roots);
  const names = [...releases.keys()].sort(compareReleases);
  const out = names.map((r) => aggregateRelease(r, releases.get(r)));
  return {
    generatedAt: now.toISOString(),
    rawDataUrl,
    rules: { sanityPassRate: SANITY_PASS_RATE, ...OUTLIER_RULES },
    warnings,
    releases: out,
    trend: { apps: trend(out, 'apps'), tasks: trend(out, 'tasks') },
  };
}

function main() {
  const { values } = parseArgs({
    options: {
      data: { type: 'string', multiple: true },
      out: { type: 'string' },
      'raw-data-url': { type: 'string' },
      help: { type: 'boolean', short: 'h' },
    },
  });
  if (values.help || !values.data?.length || !values.out) {
    console.log('Usage: node build.mjs --data <folder> [--data <folder> ...] --out <folder> [--raw-data-url <url>]');
    process.exit(values.help ? 0 : 2);
  }
  const roots = values.data.map((d) => resolve(d));
  const outDir = resolve(values.out);
  const data = buildSiteData(roots, { rawDataUrl: values['raw-data-url'] ?? DEFAULT_RAW_DATA_URL });
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
    console.log(`${r.release}: ${r.trialCount} trials (${parts.join(', ')})`);
  }
  for (const w of data.warnings) console.warn(`warning: ${w}`);
  console.log(`Wrote ${join(outDir, 'index.html')}`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
