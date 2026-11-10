import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import {
  aggregateBenchmark, aggregateRelease, canonicalArm, compareReleases, metrics, trend, wilson,
} from '../lib/aggregate.mjs';
import { loadData } from '../lib/load.mjs';
import { buildSiteData } from '../build.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const fixture = join(here, 'fixtures', 'data');

function t(o = {}) {
  const outcome = o.outcome ?? 'valid';
  return {
    schemaVersion: 1, release: 'dev', commit: 'a'.repeat(40), benchmark: o.benchmark ?? 'apps',
    scenario: o.scenario ?? 's1', set: 'tuning', arm: o.arm ?? 'none', model: o.model ?? 'm1', trial: o.trial ?? 1,
    startedAt: '2026-11-05T10:00:00Z', outcome, passed: outcome === 'valid' && o.passed !== false,
    improvements: o.improvements ?? { met: 0, total: 0 }, checks: o.checks ?? [],
    usage: { tokens: { input: 100, output: 10, cacheRead: 0 }, nanoAiu: o.nanoAiu ?? 2e9, wallClockMs: o.ms ?? 120000 },
    ...(o.extra ?? {}),
  };
}

test('metrics count rates and means over valid runs only, but totals over all runs', () => {
  const m = metrics([
    t({ checks: [
      { id: 'builds', kind: 'required', passed: true },
      { id: 'q0', kind: 'improvement', passed: true, source: 'harness' },
      { id: 'q5', kind: 'improvement', passed: false, source: 'judge' },
      { id: 'requirement-1', kind: 'improvement', passed: true, source: 'judge' },
      { id: 'requirement-2', kind: 'required', passed: true, source: 'judge' },
    ], improvements: { met: 2, total: 3 }, nanoAiu: 4e9, extra: { judge: { model: 'j', designScore: 7 } } }),
    t({ passed: false, nanoAiu: 2e9, extra: { judge: { model: 'j', designScore: null } } }),
    t({ outcome: 'build-failed', nanoAiu: 100e9 }),
  ]);
  assert.equal(m.total, 3);
  assert.equal(m.valid, 2);
  assert.equal(m.passed, 1);
  assert.equal(m.passRate, 0.5);
  assert.deepEqual([m.qualityMet, m.qualityTotal], [1, 2]);
  assert.deepEqual([m.reqMet, m.reqTotal], [2, 2]);
  assert.equal(m.improvementsRate, 2 / 3);
  assert.equal(m.designMean, 7);
  assert.equal(m.creditsMean, 3);
  assert.equal(m.creditsSpent, 106);
  assert.equal(m.minutesMean, 2);
  assert.equal(m.tokensMean, 110);
  assert.deepEqual(m.outcomes, { valid: 2, 'build-failed': 1 });
});

test('metrics on no valid runs give nulls, not zeros', () => {
  const m = metrics([t({ outcome: 'harness-error' })]);
  assert.equal(m.valid, 0);
  assert.equal(m.passRate, null);
  assert.equal(m.passCi, null);
  assert.equal(m.creditsMean, null);
  assert.equal(m.creditsSpent, 2);
});

test('missing nanoAiu is left out of the credit mean', () => {
  const a = t({ nanoAiu: 4e9 });
  const b = t();
  b.usage.nanoAiu = null;
  assert.equal(metrics([a, b]).creditsMean, 4);
});

test('wilson interval contains the observed rate', () => {
  const [lo, hi] = wilson(9, 10);
  assert.ok(lo < 0.9 && hi > 0.9 && hi <= 1);
  assert.equal(wilson(0, 0), null);
});

test('old arm names map to the published ones', () => {
  assert.equal(canonicalArm('barebones'), 'none');
  assert.equal(canonicalArm('cli-plus-skills'), 'cli-plus-plugins');
  assert.equal(canonicalArm('cli-only'), 'cli-only');
  const r = aggregateRelease('dev', [t({ arm: 'barebones' }), t({ arm: 'cli-plus-skills' })]);
  assert.deepEqual(r.benchmarks.apps.arms, ['none', 'cli-plus-plugins']);
  assert.deepEqual(r.armAliasesUsed, ['barebones = none', 'cli-plus-skills = cli-plus-plugins']);
});

test('arms and models follow the published order', () => {
  const b = aggregateBenchmark([
    t({ arm: 'cli-plus-plugins', model: 'gpt-6.1-sol' }),
    t({ arm: 'none', model: 'claude-opus-5.5' }),
    t({ arm: 'cli-only', model: 'claude-sonnet-5.5' }),
    t({ arm: 'none', model: 'zz-other' }),
  ]);
  assert.deepEqual(b.arms, ['none', 'cli-only', 'cli-plus-plugins']);
  assert.deepEqual(b.models, ['claude-sonnet-5.5', 'claude-opus-5.5', 'gpt-6.1-sol', 'zz-other']);
});

test('a scenario the none arm passes 90% or more is a sanity check and leaves the headline', () => {
  const trials = [];
  for (let i = 1; i <= 10; i++) trials.push(t({ scenario: 'easy', arm: 'none', trial: i }));
  for (let i = 1; i <= 10; i++) trials.push(t({ scenario: 'hard', arm: 'none', trial: i, passed: i <= 3 }));
  trials.push(t({ scenario: 'easy', arm: 'cli-only' }), t({ scenario: 'hard', arm: 'cli-only', passed: false }));
  const b = aggregateBenchmark(trials);
  assert.deepEqual(b.sanityScenarios, ['easy']);
  assert.equal(b.headline.none.all.passed, 13);
  assert.equal(b.headlineExcludingSanity.none.all.passed, 3);
  assert.equal(b.headlineExcludingSanity.none.all.valid, 10);
});

test('89% on none is not a sanity check', () => {
  const trials = [];
  for (let i = 1; i <= 9; i++) trials.push(t({ arm: 'none', trial: i, passed: i <= 8 }));
  assert.deepEqual(aggregateBenchmark(trials).sanityScenarios, []);
});

test('flags model splits, cost outliers, invalid runs and scenarios nobody passes', () => {
  const b = aggregateBenchmark([
    t({ arm: 'cli-only', model: 'a', trial: 1 }), t({ arm: 'cli-only', model: 'a', trial: 2 }),
    t({ arm: 'cli-only', model: 'b', trial: 1, passed: false }), t({ arm: 'cli-only', model: 'b', trial: 2, passed: false }),
    t({ arm: 'none', model: 'a', nanoAiu: 1e9 }), t({ arm: 'none', model: 'b', outcome: 'harness-error' }),
    t({ arm: 'cli-plus-plugins', model: 'a', nanoAiu: 5e9 }),
    t({ scenario: 's2', arm: 'none', passed: false }), t({ scenario: 's2', arm: 'cli-only', passed: false }),
  ]);
  const kinds = (s) => b.perScenario.find((x) => x.scenario === s).flags.map((f) => `${f.kind}:${f.arm ?? ''}:${f.model ?? ''}`);
  const s1 = kinds('s1');
  assert.ok(s1.includes('model-split:cli-only:a'));
  assert.ok(s1.includes('model-split:cli-only:b'));
  assert.ok(s1.includes('not-valid:none:'));
  // model a: 5 credits on cli-plus-plugins vs mean(1, 2) = 1.5 on the other arms.
  assert.ok(s1.includes('cost:cli-plus-plugins:a'));
  assert.ok(!s1.some((k) => k.startsWith('cost:none')));
  assert.ok(kinds('s2').includes('no-pass::'));
});

test('skills and commands count runs, and say how many runs recorded them', () => {
  const b = aggregateBenchmark([
    t({ arm: 'cli-plus-plugins', trial: 1, extra: { skills: [{ name: 'winui-design', count: 2 }, { name: 'winui-dev-workflow' }] } }),
    t({ arm: 'cli-plus-plugins', trial: 2, extra: { skills: [{ name: 'winui-dev-workflow', count: 1 }] } }),
    t({ arm: 'cli-plus-plugins', trial: 3 }),
    t({ arm: 'none', extra: { skills: [], winappCommands: [{ command: 'run', count: 3 }] } }),
  ]);
  const s = b.skills['cli-plus-plugins'];
  assert.equal(s.trials, 3);
  assert.equal(s.recorded, 2);
  assert.deepEqual(s.names['winui-dev-workflow'], { trials: 2, uses: 2 });
  assert.deepEqual(s.names['winui-design'], { trials: 1, uses: 2 });
  assert.equal(b.skills.none.recorded, 1);
  assert.deepEqual(b.winappCommands.none.names.run, { trials: 1, uses: 3 });
  assert.equal(b.winappCommands['cli-plus-plugins'].recorded, 0);
});

test('extra and missing optional fields are tolerated', () => {
  const b = aggregateBenchmark([
    t({ extra: { scoringVersion: 3, launchLevel: null, requirementsNotRun: [9], winappVersion: '0.7.1', somethingNew: { a: 1 } } }),
    t({ trial: 2 }),
  ]);
  assert.deepEqual(b.scoringVersions, ['3', 'not recorded']);
  assert.deepEqual(b.winappVersions, ['0.7.1']);
});

test('releases sort newest first with dev last, and the trend skips dev', () => {
  assert.deepEqual(['dev', 'v0.7.1', 'v0.10.0', 'v0.7.0'].sort(compareReleases), ['v0.10.0', 'v0.7.1', 'v0.7.0', 'dev']);
  const rel = (name, passed) => aggregateRelease(name, [t({ arm: 'cli-plus-plugins', passed }), t({ arm: 'cli-plus-plugins', trial: 2 })]);
  const points = trend([rel('v0.8.0', true), rel('dev', true), rel('v0.7.0', false)]);
  assert.deepEqual(points.map((p) => [p.release, p.all]), [['v0.7.0', 0.5], ['v0.8.0', 1]]);
});

test('loadData reads both folder layouts and reports bad files', () => {
  const { releases, warnings } = loadData([fixture]);
  assert.deepEqual([...releases.keys()].sort(), ['dev', 'v0.7.1']);
  assert.equal(releases.get('dev').length, 4);
  assert.equal(releases.get('v0.7.1').length, 2);
  assert.ok(warnings.some((w) => w.includes('broken.json') && w.includes('not valid JSON')));
  assert.ok(!warnings.some((w) => w.includes('index.json')), 'index.json trial count matches');
});

test('loadData skips duplicates when the same root is given twice', () => {
  const { releases, warnings } = loadData([fixture, fixture]);
  assert.equal(releases.get('dev').length, 4);
  assert.ok(warnings.filter((w) => w.includes('same trial')).length >= 4);
});

test('site data from the fixture has the expected counts', () => {
  const d = buildSiteData([fixture], { now: new Date('2026-11-10T00:00:00Z') });
  assert.deepEqual(d.releases.map((r) => r.release), ['v0.7.1', 'dev']);
  const apps = d.releases[1].benchmarks.apps;
  assert.equal(apps.trialCount, 4);
  assert.deepEqual([apps.headline.none.all.valid, apps.headline.none.all.total], [1, 2]);
  assert.equal(apps.headline.none.all.reqRate, 0);
  assert.equal(apps.headline['cli-plus-plugins'].all.passed, 2);
  assert.equal(apps.headline['cli-plus-plugins'].all.creditsMean, 20);
  assert.equal(apps.headline['cli-plus-plugins'].all.designMean, 8);
  assert.deepEqual(apps.notValid.map((n) => n.outcome), ['build-failed']);
  assert.equal(apps.skills['cli-plus-plugins'].names['winui-dev-workflow'].trials, 2);
  const tasks = d.releases[0].benchmarks.tasks;
  assert.equal(tasks.headline['cli-only'].all.askedBeforeAdmin, 1);
  assert.equal(tasks.headline['cli-only'].all.valid, 0);
  assert.deepEqual(d.trend.apps, []);
});

test('build.mjs writes a site that loads its data', () => {
  const out = mkdtempSync(join(tmpdir(), 'winapp-dash-test-'));
  try {
    execFileSync(process.execPath, [join(here, '..', 'build.mjs'), '--data', fixture, '--out', out], { stdio: 'pipe' });
    for (const f of ['index.html', 'app.js', 'styles.css', 'data.json', 'data.js']) assert.ok(existsSync(join(out, f)), f);
    const data = JSON.parse(readFileSync(join(out, 'data.json'), 'utf8'));
    assert.equal(data.releases.length, 2);
    assert.ok(readFileSync(join(out, 'data.js'), 'utf8').startsWith('window.BENCH_DATA = {'));
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
});

test('build.mjs fails clearly when no trial files are found', () => {
  const empty = mkdtempSync(join(tmpdir(), 'winapp-dash-empty-'));
  try {
    assert.throws(() => execFileSync(process.execPath, [join(here, '..', 'build.mjs'), '--data', empty, '--out', join(empty, 'out')], { stdio: 'pipe' }), /No trial files found/);
  } finally {
    rmSync(empty, { recursive: true, force: true });
  }
});
