import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { aggregateRelease } from '../lib/aggregate.mjs';
import { buildTrends, changesBetween, comparableFor, trendSeries } from '../lib/trends.mjs';
import { buildSiteData, loadMethodChanges } from '../build.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const fixture = join(here, 'fixtures', 'data');

const RUN3 = { id: 'run-3-fixes', date: '2026-11-10', title: 'Run 3 fixes', affects: ['apps', 'tasks'], comparable: { barebones: true, 'cli-only': false, 'cli-plus-skills': false } };
const V4 = { id: 'scoring-v4', date: '2026-11-10', title: 'Scoring version 4', affects: ['apps'], scoringVersion: 4, comparable: false };

// One apps run: barebones and cli-plus-skills on two scenarios, quality checks passing at the given rates.
function run(label, { day, scoring = 3, base = 0.5, withSkills = 0.8, release = 'dev', scenarioBase = {}, extra = {} }) {
  const trials = [];
  const add = (arm, scenario, rate) => {
    for (let i = 1; i <= 10; i++) {
      trials.push({
        schemaVersion: 1, release, commit: (label.length % 10).toString().repeat(40), benchmark: 'apps', scenario, set: 'tuning', arm,
        model: 'claude-sonnet-5.5', trial: i, startedAt: `${day}T0${i % 10}:00:00Z`, outcome: 'valid', passed: true,
        improvements: { met: 0, total: 1 }, checks: [{ id: 'q0', kind: 'improvement', passed: i <= Math.round(rate * 10) }],
        usage: { tokens: { input: 1, output: 1, cacheRead: 0 }, nanoAiu: 1e9, wallClockMs: 60000 }, scoringVersion: scoring, ...extra,
      });
    }
  };
  for (const s of ['calc', 'notes']) {
    add('barebones', s, scenarioBase[s] ?? base);
    add('cli-plus-skills', s, withSkills);
  }
  return aggregateRelease(label, trials, { label });
}

test('a single run gives one point and no steps', () => {
  const t = trendSeries([run('Run 2', { day: '2026-11-05' })], 'apps', [RUN3, V4]);
  assert.equal(t.points.length, 1);
  assert.equal(t.steps.length, 0);
  assert.equal(t.points[0].gap.all.qualityRate.toFixed(2), '0.30');
});

test('a scoring change between runs breaks every line and marks the step', () => {
  const t = trendSeries([
    run('Run 3', { day: '2026-11-11', scoring: 4, base: 0.6, withSkills: 0.9 }),
    run('Run 2', { day: '2026-11-05' }),
  ], 'apps', [RUN3, V4]);
  assert.deepEqual(t.points.map((p) => p.name), ['Run 2', 'Run 3'], 'ordered by date');
  assert.deepEqual(t.steps[0].changes.map((c) => c.id).sort(), ['run-3-fixes', 'scoring-v4']);
  assert.equal(t.steps[0].comparable.barebones, false);
  assert.equal(t.steps[0].comparable['cli-plus-skills'], false);
  assert.equal(t.steps[0].comparable.gap, false);
  assert.deepEqual(t.notes, [], 'no control note across a non-comparable step');
});

test('a change that keeps barebones comparable breaks only the other setups and the gap', () => {
  const t = trendSeries([
    run('Run 2', { day: '2026-11-05' }),
    run('Run 3', { day: '2026-11-11', base: 0.7, scenarioBase: { notes: 0.9 } }),
  ], 'apps', [RUN3, V4]);
  assert.deepEqual(t.steps[0].changes.map((c) => c.id), ['run-3-fixes']);
  assert.deepEqual(t.steps[0].comparable, { barebones: true, 'cli-plus-skills': false, gap: false });
  // barebones went from 50% to 80% between comparable runs: the control moved.
  assert.equal(t.notes.length, 1);
  assert.equal(t.notes[0].kind, 'control-moved');
  assert.deepEqual(t.notes[0].moved.map((m) => m.metric), ['qualityRate']);
  const notes = t.decay.find((d) => d.scenario === 'notes'), calc = t.decay.find((d) => d.scenario === 'calc');
  assert.equal(notes.rising, true);
  assert.equal(calc.rising, true);
  assert.equal(notes.values.length, 2);
});

test('small barebones moves between comparable runs are not flagged', () => {
  const t = trendSeries([run('A', { day: '2026-11-01' }), run('B', { day: '2026-11-03' })], 'apps', [RUN3]);
  assert.equal(t.steps[0].changes.length, 0);
  assert.equal(t.steps[0].comparable.gap, true);
  assert.deepEqual(t.notes, []);
  assert.ok(t.decay.every((d) => !d.rising));
});

test('a scoring version change with no changelog entry still breaks the line', () => {
  const t = trendSeries([run('A', { day: '2026-11-01' }), run('B', { day: '2026-11-03', scoring: 4 })], 'apps', []);
  assert.equal(t.steps[0].changes[0].id, 'scoring-3-4');
  assert.equal(t.steps[0].comparable.gap, false);
});

test('changes only apply to the benchmarks they affect', () => {
  const a = run('A', { day: '2026-11-01' }), b = run('B', { day: '2026-11-12' });
  assert.equal(changesBetween(a, b, 'apps', [{ ...RUN3, affects: ['tasks'] }]).length, 0);
});

test('comparable is a boolean or per setup', () => {
  assert.equal(comparableFor(RUN3, 'barebones'), true);
  assert.equal(comparableFor(RUN3, 'cli-only'), false);
  assert.equal(comparableFor(RUN3, 'unknown-arm'), false);
  assert.equal(comparableFor(V4, 'barebones'), false);
  assert.equal(comparableFor({ comparable: true }, 'barebones'), true);
});

test('development runs and releases are separate trends, and run info is kept per point', () => {
  const trends = buildTrends([
    run('Run 2', { day: '2026-11-05', extra: { pluginsCommit: 'b'.repeat(40), winappVersion: '0.7.1+abc' } }),
    run('v0.8.0', { day: '2026-11-06', release: 'v0.8.0' }),
  ]);
  assert.deepEqual(trends.dev.apps.points.map((p) => p.name), ['Run 2']);
  assert.deepEqual(trends.release.apps.points.map((p) => p.name), ['v0.8.0']);
  const info = trends.dev.apps.points[0].info;
  assert.deepEqual(info.pluginsCommits, ['b'.repeat(40)]);
  assert.deepEqual(info.winappVersions, ['0.7.1+abc']);
  assert.deepEqual(info.scoringVersions, ['3']);
  assert.deepEqual(info.models, ['claude-sonnet-5.5']);
  assert.deepEqual(trends.release.apps.points[0].info.pluginsCommits, []);
});

test('method changes load from the dashboard folder and a data folder, and the data folder wins', () => {
  const dir = mkdtempSync(join(tmpdir(), 'winapp-dash-mc-'));
  try {
    writeFileSync(join(dir, 'method-changes.json'), JSON.stringify([
      { id: 'scoring-v4', date: '2026-11-11', title: 'Scoring 4 (data folder)', comparable: false },
      { id: 'bad' },
    ]));
    const warnings = [];
    const list = loadMethodChanges([dir], warnings);
    assert.ok(list.some((c) => c.id === 'run-3-fixes'), 'seeded entry from benchmarks/dashboard/method-changes.json');
    assert.equal(list.find((c) => c.id === 'scoring-v4').title, 'Scoring 4 (data folder)');
    assert.ok(warnings.some((w) => w.includes('"bad"')));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('end to end: a synthetic second run with scoring version 4 is marked and broken', () => {
  const root = mkdtempSync(join(tmpdir(), 'winapp-dash-run3-'));
  try {
    const run3 = join(root, 'run3', 'apps', 'dev');
    cpSync(join(fixture, 'apps', 'dev'), run3, { recursive: true });
    for (const f of readdirSync(run3)) {
      const t = JSON.parse(readFileSync(join(run3, f), 'utf8'));
      t.startedAt = '2026-11-11T09:00:00Z';
      t.scoringVersion = 4;
      writeFileSync(join(run3, f), JSON.stringify(t));
    }
    const d = buildSiteData([
      { dir: join(fixture, 'apps'), label: 'Run 2' },
      { dir: join(root, 'run3'), label: 'Run 3' },
    ]);
    assert.equal(d.releases[0].id, 'Run 3', 'most recent run first');
    const t = d.trends.dev.apps;
    assert.deepEqual(t.points.map((p) => p.name), ['Run 2', 'Run 3']);
    assert.ok(t.steps[0].changes.some((c) => c.id === 'scoring-v4'));
    assert.equal(t.steps[0].comparable.gap, false);
    assert.ok(d.methodChanges.length >= 2);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
