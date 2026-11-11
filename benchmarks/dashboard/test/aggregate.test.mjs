import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import {
  aggregateBenchmark, aggregateRelease, compareEntries, checkPassed, compareReleases, metrics, pairedGap, skillActivation, wilson,
} from '../lib/aggregate.mjs';
import { loadData } from '../lib/load.mjs';
import { buildSiteData, loadTargetSkills, parseCli } from '../build.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const fixture = join(here, 'fixtures', 'data');

function t(o = {}) {
  const outcome = o.outcome ?? 'valid';
  return {
    schemaVersion: 1, release: 'dev', commit: 'a'.repeat(40), benchmark: o.benchmark ?? 'apps',
    scenario: o.scenario ?? 's1', set: 'tuning', arm: o.arm ?? 'barebones', model: o.model ?? 'm1', trial: o.trial ?? 1,
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

test('launch level is counted for apps, and a missing one is "not measured"', () => {
  const m = metrics([t({ extra: { launchLevel: 2 } }), t({ extra: { launchLevel: 0 } }), t(), t({ benchmark: 'tasks' })]);
  assert.deepEqual(m.launchLevels, { 2: 1, 0: 1, 'not measured': 1 });
  assert.deepEqual(aggregateBenchmark([t({ extra: { launchLevel: 3 } }), t({ trial: 2 })]).launchLevelNames, ['3', 'not measured']);
});

test('harness-error runs are counted in the total but not as valid', () => {
  const m = metrics([t(), t({ outcome: 'harness-error' })]);
  assert.deepEqual([m.valid, m.total, m.passed, m.passRate], [1, 2, 1, 1]);
});

test('built-and-launched rate counts valid apps runs only', () => {
  const ok = [{ id: 'builds', kind: 'required', passed: true }, { id: 'launches', kind: 'required', passed: true }];
  const m = metrics([
    t({ checks: ok }),
    t({ checks: [{ id: 'builds', kind: 'required', passed: true }, { id: 'launches', kind: 'required', passed: false }], passed: false }),
    t({ outcome: 'build-failed', checks: [{ id: 'builds', kind: 'required', passed: false }] }),
    t({ outcome: 'harness-error', checks: ok }),
  ]);
  assert.deepEqual([m.worked, m.worksTotal, m.worksRate], [1, 2, 0.5]);
  assert.equal(metrics([t({ benchmark: 'tasks', checks: ok })]).worksRate, null);
});

test('the change is cli-plus-skills minus barebones, overall and per model', () => {
  const q = (passed) => [{ id: 'q0', kind: 'improvement', passed }];
  const b = aggregateBenchmark([
    t({ arm: 'barebones', model: 'a', checks: q(false), nanoAiu: 10e9 }),
    t({ arm: 'barebones', model: 'b', checks: q(true), nanoAiu: 10e9 }),
    t({ arm: 'cli-only', model: 'a', checks: q(true), nanoAiu: 1e9 }),
    t({ arm: 'cli-plus-skills', model: 'a', checks: q(true), nanoAiu: 12e9 }),
    t({ arm: 'cli-plus-skills', model: 'b', checks: q(true), nanoAiu: 14e9 }),
  ]);
  assert.equal(b.effect.all.qualityRate, 0.5);
  assert.equal(b.effect.all.creditsMean, 3);
  assert.equal(b.effect.byModel.a.qualityRate, 1);
  assert.equal(b.effect.byModel.b.qualityRate, 0);
  assert.equal(b.perScenario[0].effect.all.qualityRate, 0.5);
  assert.deepEqual(b.arms, ['barebones', 'cli-only', 'cli-plus-skills']);
  assert.equal(aggregateBenchmark([t({ arm: 'barebones' }), t({ arm: 'cli-only' })]).effect, null);
});

test('arms and models follow the published order', () => {
  const b = aggregateBenchmark([
    t({ arm: 'cli-plus-skills', model: 'gpt-6.1-sol' }),
    t({ arm: 'barebones', model: 'claude-opus-5.5' }),
    t({ arm: 'cli-only', model: 'claude-sonnet-5.5' }),
    t({ arm: 'barebones', model: 'zz-other' }),
    t({ arm: 'full-suite', model: 'gpt-6.1-sol' }),
  ]);
  assert.deepEqual(b.arms, ['barebones', 'cli-only', 'cli-plus-skills', 'full-suite']);
  assert.deepEqual(b.models, ['claude-sonnet-5.5', 'claude-opus-5.5', 'gpt-6.1-sol', 'zz-other']);
});

test('a scenario the barebones arm passes 90% or more is a sanity check and leaves the headline', () => {
  const trials = [];
  for (let i = 1; i <= 10; i++) trials.push(t({ scenario: 'easy', arm: 'barebones', trial: i }));
  for (let i = 1; i <= 10; i++) trials.push(t({ scenario: 'hard', arm: 'barebones', trial: i, passed: i <= 3 }));
  trials.push(t({ scenario: 'easy', arm: 'cli-only' }), t({ scenario: 'hard', arm: 'cli-only', passed: false }));
  const b = aggregateBenchmark(trials);
  assert.deepEqual(b.sanityScenarios, ['easy']);
  assert.equal(b.headline.barebones.all.passed, 13);
  assert.equal(b.headlineExcludingSanity.barebones.all.passed, 3);
  assert.equal(b.headlineExcludingSanity.barebones.all.valid, 10);
});

test('89% on barebones is not a sanity check', () => {
  const trials = [];
  for (let i = 1; i <= 9; i++) trials.push(t({ arm: 'barebones', trial: i, passed: i <= 8 }));
  assert.deepEqual(aggregateBenchmark(trials).sanityScenarios, []);
});

test('flags model splits, cost outliers, invalid runs and scenarios nobody passes', () => {
  const b = aggregateBenchmark([
    t({ arm: 'cli-only', model: 'a', trial: 1 }), t({ arm: 'cli-only', model: 'a', trial: 2 }),
    t({ arm: 'cli-only', model: 'b', trial: 1, passed: false }), t({ arm: 'cli-only', model: 'b', trial: 2, passed: false }),
    t({ arm: 'barebones', model: 'a', nanoAiu: 1e9 }), t({ arm: 'barebones', model: 'b', outcome: 'harness-error' }),
    t({ arm: 'cli-plus-skills', model: 'a', nanoAiu: 5e9 }),
    t({ scenario: 's2', arm: 'barebones', passed: false }), t({ scenario: 's2', arm: 'cli-only', passed: false }),
  ]);
  const kinds = (s) => b.perScenario.find((x) => x.scenario === s).flags.map((f) => `${f.kind}:${f.arm ?? ''}:${f.model ?? ''}`);
  const s1 = kinds('s1');
  assert.ok(s1.includes('model-split:cli-only:a'));
  assert.ok(s1.includes('model-split:cli-only:b'));
  assert.ok(s1.includes('not-valid:barebones:'));
  // model a: 5 credits on cli-plus-skills vs mean(1, 2) = 1.5 on the other arms.
  assert.ok(s1.includes('cost:cli-plus-skills:a'));
  assert.ok(!s1.some((k) => k.startsWith('cost:barebones')));
  assert.ok(kinds('s2').includes('no-pass::'));
});

test('skills and commands count runs, and say how many runs recorded them', () => {
  const b = aggregateBenchmark([
    t({ arm: 'cli-plus-skills', trial: 1, extra: { skills: [{ name: 'winui-design', count: 2 }, { name: 'winui-dev-workflow' }] } }),
    t({ arm: 'cli-plus-skills', trial: 2, extra: { skills: [{ name: 'winui-dev-workflow', count: 1 }] } }),
    t({ arm: 'cli-plus-skills', trial: 3 }),
    t({ arm: 'barebones', extra: { skills: [], winappCommands: [{ command: 'run', count: 3 }] } }),
  ]);
  const s = b.skills['cli-plus-skills'];
  assert.equal(s.trials, 3);
  assert.equal(s.recorded, 2);
  assert.deepEqual(s.names['winui-dev-workflow'], { trials: 2, uses: 2 });
  assert.deepEqual(s.names['winui-design'], { trials: 1, uses: 2 });
  assert.equal(b.skills.barebones.recorded, 1);
  assert.deepEqual(b.winappCommands.barebones.names.run, { trials: 1, uses: 3 });
  assert.equal(b.winappCommands['cli-plus-skills'].recorded, 0);
});

test('extra and missing optional fields are tolerated', () => {
  const b = aggregateBenchmark([
    t({ extra: { scoringVersion: 3, launchLevel: null, requirementsNotRun: [9], winappVersion: '0.7.1', somethingNew: { a: 1 } } }),
    t({ trial: 2 }),
  ]);
  assert.deepEqual(b.scoringVersions, ['3', 'not recorded']);
  assert.deepEqual(b.winappVersions, ['0.7.1']);
});

test('releases sort newest first with dev last', () => {
  assert.deepEqual(['dev', 'v0.7.1', 'v0.10.0', 'v0.7.0'].sort(compareReleases), ['v0.10.0', 'v0.7.1', 'v0.7.0', 'dev']);
});

test('loadData reads both folder layouts and reports bad files', () => {
  const { releases, warnings } = loadData([fixture]);
  assert.deepEqual([...releases.keys()].sort(), ['dev', 'v0.7.1']);
  assert.equal(releases.get('dev').length, 5);
  assert.equal(releases.get('v0.7.1').length, 3);
  assert.ok(warnings.some((w) => w.includes('broken.json') && w.includes('not valid JSON')));
  assert.ok(!warnings.some((w) => w.includes('index.json')), 'index.json trial count matches');
});

test('loadData skips duplicates when the same root is given twice', () => {
  const { releases, warnings } = loadData([fixture, fixture]);
  assert.equal(releases.get('dev').length, 5);
  assert.ok(warnings.filter((w) => w.includes('same trial')).length >= 5);
});

test('site data from the fixture has the expected counts', () => {
  const d = buildSiteData([fixture], { now: new Date('2026-11-10T00:00:00Z') });
  assert.deepEqual(d.releases.map((r) => r.release), ['v0.7.1', 'dev']);
  const apps = d.releases[1].benchmarks.apps;
  assert.equal(apps.trialCount, 5);
  assert.deepEqual([apps.headline['cli-only'].all.valid, apps.headline['cli-only'].all.total], [0, 1]);
  assert.deepEqual(apps.notValid.map((n) => n.outcome).sort(), ['build-failed', 'harness-error']);
  assert.deepEqual(apps.headline.barebones.all.launchLevels, { 2: 1, 'not measured': 1 });
  assert.deepEqual([apps.headline.barebones.all.valid, apps.headline.barebones.all.total], [1, 2]);
  assert.equal(apps.headline.barebones.all.reqRate, 0);
  assert.equal(apps.headline['cli-plus-skills'].all.passed, 2);
  assert.equal(apps.headline['cli-plus-skills'].all.creditsMean, 20);
  assert.equal(apps.headline['cli-plus-skills'].all.designMean, 8);
  assert.equal(apps.skills['cli-plus-skills'].names['winui-dev-workflow'].trials, 2);
  const tasks = d.releases[0].benchmarks.tasks;
  assert.equal(tasks.headline['cli-only'].all.askedBeforeAdmin, 1);
  assert.equal(tasks.headline['cli-only'].all.valid, 0);
  assert.deepEqual(tasks.arms, ['barebones', 'cli-only', 'full-suite']);
  assert.deepEqual(tasks.launchLevelNames, []);
  assert.equal(d.trends.dev.apps.points.length, 1);
  assert.equal(d.trends.release.tasks.points.length, 1);
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

test('--label applies to the --data before it, and folders with the same label merge into one entry', () => {
  const opts = parseCli(['--data', 'a', '--label', 'Run 3', '--data', 'b', '--data', 'c', '--label', 'Run 3', '--out', 'o']);
  assert.deepEqual(opts.sources, [{ dir: 'a', label: 'Run 3' }, { dir: 'b', label: null }, { dir: 'c', label: 'Run 3' }]);
  assert.throws(() => parseCli(['--label', 'x', '--data', 'a']), /must follow a --data/);
  assert.throws(() => parseCli(['--data', 'a', '--label', 'x', '--label', 'y']), /must follow a --data/);

  const d = buildSiteData([
    { dir: join(fixture, 'apps'), label: 'Run 3' },
    { dir: join(fixture, 'v0.7.1'), label: 'Run 3' },
    { dir: join(fixture, 'apps'), label: 'Run 2' },
  ]);
  const run3 = d.releases.find((r) => r.id === 'Run 3');
  assert.equal(d.releases.length, 2);
  assert.equal(run3.label, 'Run 3');
  assert.deepEqual(Object.keys(run3.benchmarks), ['apps', 'tasks']);
  assert.deepEqual([run3.benchmarks.apps.trialCount, run3.benchmarks.tasks.trialCount], [5, 3]);
  assert.deepEqual(run3.releases, ['dev', 'v0.7.1']);
  assert.equal(run3.release, null);
  assert.equal(d.releases.find((r) => r.id === 'Run 2').benchmarks.apps.trialCount, 5);
  assert.ok(!d.warnings.some((w) => w.includes('same trial')), 'the same files under two labels are two entries, not duplicates');
});

test('entries are listed most recent run first, and each benchmark keeps its own commits and dates', () => {
  const at = (iso, commit, benchmark = 'apps') => t({ benchmark, extra: { startedAt: iso, commit: commit.repeat(40), winappVersion: `0.7.${commit}` } });
  const old = aggregateRelease('Run 2', [at('2026-11-05T00:00:00Z', '1')], { label: 'Run 2' });
  const recent = aggregateRelease('Run 3', [at('2026-11-10T00:00:00Z', '2'), at('2026-11-12T00:00:00Z', '3', 'tasks')], { label: 'Run 3' });
  assert.deepEqual([old, recent].sort(compareEntries).map((r) => r.id), ['Run 3', 'Run 2']);
  assert.deepEqual(recent.benchmarks.apps.commits, ['2'.repeat(40)]);
  assert.deepEqual(recent.benchmarks.tasks.commits, ['3'.repeat(40)]);
  assert.deepEqual(recent.benchmarks.tasks.winappVersions, ['0.7.3']);
  assert.equal(recent.benchmarks.tasks.firstStartedAt, '2026-11-12T00:00:00Z');
});

test('quality topics pool their check ids per setup over valid runs, and report the change', () => {
  const c = (id, passed) => ({ id, kind: 'improvement', passed, source: 'harness' });
  const b = aggregateBenchmark([
    t({ arm: 'barebones', checks: [c('p2', false), c('P3', true), c('q4', true)] }),
    t({ arm: 'barebones', trial: 2, outcome: 'build-failed', checks: [c('p2', true)] }),
    t({ arm: 'cli-plus-skills', checks: [c('p2', true), c('p3', true), c('q4', false)] }),
    t({ arm: 'cli-only', checks: [c('p2', true)] }),
  ]);
  const pack = b.qualityTopics.find((q) => q.topic === 'Packaging');
  assert.deepEqual(pack.byArm.barebones, { passed: 1, total: 2, rate: 0.5 });
  assert.deepEqual(pack.byArm['cli-plus-skills'], { passed: 2, total: 2, rate: 1 });
  assert.equal(pack.change, 0.5);
  assert.ok(pack.skills.includes('winapp-manifest'));
  assert.equal(b.qualityTopics.find((q) => q.checks.includes('q4')).change, -1);
  assert.ok(!b.qualityTopics.some((q) => q.topic === 'Typography'), 'topics with no checks are left out');
});

test('packaging checks are counted on their own and left out of quality checks met', () => {
  const c = (id, passed) => ({ id, kind: 'improvement', passed, source: 'harness' });
  const m = metrics([
    t({ checks: [c('p2', true), c('P3', false), c('q0', true), c('q5', false), { id: 'requirement-1', kind: 'improvement', passed: true }] }),
    t({ outcome: 'build-failed', checks: [c('p2', true), c('q0', true)] }),
  ]);
  assert.deepEqual([m.packagingMet, m.packagingTotal, m.packagingRate], [1, 2, 0.5]);
  assert.deepEqual([m.qualityMet, m.qualityTotal, m.qualityRate], [1, 2, 0.5]);
  assert.deepEqual([m.qualityAllMet, m.qualityAllTotal, m.qualityAllRate], [2, 4, 0.5]);
  assert.deepEqual([m.reqMet, m.reqTotal], [1, 1]);
});

test('per-model change is the mean of paired per-app changes, with a repeatable bootstrap interval', () => {
  const c = (passed) => [{ id: 'q0', kind: 'improvement', passed }];
  const trials = [];
  // Three apps: skills gain +100, 0 and +100 points on quality for model a.
  for (const [s, base, withSkills] of [['x', false, true], ['y', true, true], ['z', false, true]]) {
    trials.push(t({ scenario: s, arm: 'barebones', model: 'a', checks: c(base) }), t({ scenario: s, arm: 'cli-plus-skills', model: 'a', checks: c(withSkills) }));
  }
  const b = aggregateBenchmark(trials);
  const g = b.pairedQualityGap.a;
  assert.equal(g.apps, 3);
  assert.ok(Math.abs(g.mean - 2 / 3) < 1e-9);
  assert.ok(g.ci[0] >= 0 && g.ci[1] <= 1 && g.ci[0] <= g.mean && g.mean <= g.ci[1]);
  assert.deepEqual(pairedGap(b.perScenario, ['a']).a.ci, g.ci, 'same seed, same interval');
});

test('the build-warnings check counts C# warnings only', () => {
  const q4 = (passed, detail) => ({ id: 'q4', kind: 'improvement', passed, source: 'harness', detail });
  assert.equal(checkPassed(q4(false, 'WUI: 9')), true, 'WinUI analyzer warnings only');
  assert.equal(checkPassed(q4(false, 'CS: 1, WUI: 9')), false);
  assert.equal(checkPassed(q4(false, 'CS: 1')), false);
  assert.equal(checkPassed(q4(false, 'CS: 0, WUI: 3')), true);
  assert.equal(checkPassed(q4(false, 'something went wrong')), false, 'unparsed detail keeps the recorded result');
  assert.equal(checkPassed(q4(false, undefined)), false);
  assert.equal(checkPassed(q4(true, 'No C#/XAML/trim/analyzer warnings')), true);
  assert.equal(checkPassed({ id: 'q0', kind: 'improvement', passed: false, detail: 'WUI: 2' }), false, 'other checks unchanged');
  const m = metrics([t({ checks: [q4(false, 'WUI: 4'), { id: 'q0', kind: 'improvement', passed: true }] })]);
  assert.deepEqual([m.qualityMet, m.qualityTotal], [2, 2]);
  const b = aggregateBenchmark([t({ arm: 'cli-plus-skills', checks: [q4(false, 'WUI: 4')] }), t({ arm: 'barebones', checks: [q4(false, 'CS: 2')] })]);
  const topic = b.qualityTopics.find((q) => q.checks.includes('q4'));
  assert.equal(topic.topic, 'Build warnings (C# only)');
  assert.deepEqual([topic.byArm['cli-plus-skills'].passed, topic.byArm.barebones.passed], [1, 0]);
});

// Optional check against the real run 2 files. Rates and per-model means match slide-numbers.py
// except where q4 is counted by C# warnings only (quality checks met and its per-model change).
// Set WINAPP_DASH_RUN2 to the trial-files folder to run it.
test('run 2 numbers match the reference script', { skip: !process.env.WINAPP_DASH_RUN2 }, () => {
  const d = buildSiteData([{ dir: process.env.WINAPP_DASH_RUN2, label: 'Run 2' }]);
  const b = d.releases[0].benchmarks.apps;
  const o = b.headline.barebones.all, w = b.headline['cli-plus-skills'].all;
  const near = (x, want) => assert.ok(Math.abs(x * 100 - want) < 0.05 + 1e-9, `${(x * 100).toFixed(2)} vs ${want}`);
  near(o.qualityAllRate, 65.9); near(w.qualityAllRate, 86.1);
  near(o.qualityRate, 76.9); near(w.qualityRate, 83.6);
  near(o.packagingRate, 11.7); near(w.packagingRate, 98.8);
  const q4 = b.qualityTopics.find((q) => q.checks.includes('q4'));
  near(q4.byArm.barebones.rate, 100.0); near(q4.byArm['cli-plus-skills'].rate, 98.8);
  near(b.pairedQualityGap['claude-sonnet-5.5'].mean, 6.9);
  near(b.pairedQualityGap['gpt-6.1-sol'].mean, 8.7);
  near(b.pairedQualityGap['claude-opus-5.5'].mean, 7.1);
  // Target-skill activation from target-skills.json (run 2 trial files carry no targetSkill).
  const act = (bench, skill) => d.releases[0].benchmarks[bench].activation.perSkill.find((s) => s.skill === skill);
  assert.deepEqual(['loaded', 'runs', 'activationFailed'].map((k) => act('apps', 'winapp-find-api')[k]), [3, 9, true]);
  assert.deepEqual(['loaded', 'runs', 'activationFailed'].map((k) => act('tasks', 'winapp-signing')[k]), [13, 24, false]);
});

test('skillActivation counts target-skill loads in the skills setup and flags a skill under half', () => {
  const sk = (...names) => ({ skills: names.map((name) => ({ name })) });
  const a = skillActivation([
    t({ scenario: 'sign', arm: 'cli-plus-skills', extra: { targetSkill: 'winapp-signing', ...sk('winapp-signing') } }),
    t({ scenario: 'sign', arm: 'cli-plus-skills', extra: { targetSkill: 'winapp-signing', ...sk() } }),
    t({ scenario: 'api', arm: 'cli-plus-skills', extra: sk('winapp-setup') }),
    t({ scenario: 'api', arm: 'cli-plus-skills', extra: sk() }),
    t({ scenario: 'api', arm: 'cli-plus-skills', extra: sk('winapp-find-api') }),
    t({ scenario: 'api', arm: 'cli-plus-skills', outcome: 'harness-error', extra: sk('winapp-find-api') }),
    t({ scenario: 'api', arm: 'cli-plus-skills' }),
    t({ scenario: 'api', arm: 'cli-only', extra: sk() }),
    t({ scenario: 'calc', arm: 'cli-plus-skills', extra: sk('winui-dev-workflow') }),
  ], { api: 'winapp-find-api' });
  assert.deepEqual(a.perScenario.map((r) => `${r.scenario}:${r.skill}:${r.loaded}/${r.runs}`), ['api:winapp-find-api:1/3', 'sign:winapp-signing:1/2']);
  assert.deepEqual(a.perSkill.map((s) => `${s.skill}:${s.activationFailed}`), ['winapp-find-api:true', 'winapp-signing:false']);
});

test('skillActivation reads a tasks file without skills as none loaded', () => {
  const a = skillActivation([t({ benchmark: 'tasks', scenario: 'x', arm: 'cli-plus-skills' })], { x: 'winapp-package' });
  assert.deepEqual(a.perSkill.map((s) => [s.loaded, s.runs, s.activationFailed]), [[0, 1, true]]);
});

test('target-skills.json names only skills that exist in the plugins', () => {
  const map = loadTargetSkills();
  const plugins = join(here, '..', '..', '..', 'plugins');
  const exists = (s) => existsSync(join(plugins, 'winapp', 'skills', s)) || existsSync(join(plugins, 'winui', 'agent-plugin', 'skills', s));
  assert.ok(Object.keys(map.apps ?? {}).length && Object.keys(map.tasks ?? {}).length);
  for (const [bench, scenarios] of Object.entries(map)) {
    for (const [scenario, skill] of Object.entries(scenarios)) assert.ok(exists(skill), `${bench}/${scenario}: ${skill}`);
  }
});

test('aggregateRelease uses the target-skill map for trials without targetSkill', () => {
  const r = aggregateRelease('dev', [t({ scenario: 'ai-journal', arm: 'cli-plus-skills', extra: { skills: [] } })], { targetSkills: { apps: { 'ai-journal': 'winapp-find-api' } } });
  assert.equal(r.benchmarks.apps.activation.perSkill[0].activationFailed, true);
});