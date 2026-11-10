/* Renders the benchmark dashboard from window.BENCH_DATA (written by build.mjs). */
(function () {
  'use strict';

  const D = window.BENCH_DATA;
  const $ = (id) => document.getElementById(id);
  const REPO = 'https://github.com/microsoft/winappCli';
  const BASE = D?.rules?.baseArm ?? 'barebones';
  const WITH = D?.rules?.skillsArm ?? 'cli-plus-skills';
  const CLI = 'cli-only';

  const ARM_INFO = {
    [BASE]: {
      label: 'Without our tools',
      desc: 'No winapp CLI and no plugin skills. The agent works from what the model already knows and the standard Windows and .NET tools on the machine.',
    },
    [CLI]: {
      label: 'winapp CLI only',
      desc: 'The winapp CLI is on PATH, with no plugin skills. Runs made after #1263 also tell the agent in the prompt that winapp is installed.',
    },
    [WITH]: {
      label: 'With winapp + skills',
      desc: 'The winapp CLI is on PATH and the winapp and WinUI plugins are installed, so the agent can run winapp and load the skills.',
    },
    'winapp-skills': { label: 'Debugging: winapp plugin only', desc: 'tasks debugging configuration: winapp on PATH and only the winapp plugin.' },
    'winui-skills': { label: 'Debugging: WinUI plugin only', desc: 'tasks debugging configuration: winapp on PATH and only the WinUI plugin.' },
    'full-suite': { label: 'Debugging: both plugins + winapp agent', desc: 'tasks debugging configuration: winapp on PATH, both plugins, started as the winapp custom agent.' },
  };

  // Definitions, shown as tooltips and listed in "How we measured".
  const DEFS = {
    quality: 'Quality checks met: share of quality checks that passed in valid runs, such as accessible names, x:Bind and no hard-coded font sizes. Checked by script, plus a few by the judge model.',
    requirements: "Requirements met: share of the scenario's requested features that the judge model found working, in valid runs.",
    works: 'Built and launched: share of all runs where the app built and its window opened. Every run counts, including runs that failed to build.',
    credits: 'Credits per app: mean AI credits Copilot CLI reported per valid run (1 credit = 1,000,000,000 nano-AIU).',
    minutes: 'Minutes per app: mean wall-clock time per valid run.',
    pass: 'Pass rate: share of valid runs where every required check passed (builds, launches, right framework).',
    design: "Design score: the judge model's 0-10 rating. Detail only; it never changes a pass.",
    claim: 'Each skill will get one automatic sentence (Helps, Helps but costs more, No measurable difference, Not enough data) once the rules in #1182 are agreed.',
    valid: 'Valid: the agent finished and the checks ran. Other outcomes (build-failed, harness-error, ...) are left out of rates and averages and listed under Details.',
  };

  const OUTCOME_INFO = {
    valid: 'The agent finished and the checks ran. Pass or fail is a real result.',
    'app-failed': 'The app built but crashed or did not run.',
    'build-failed': 'No app was produced.',
    'timed-out': 'The agent ran out of time.',
    'harness-error': 'Our tooling failed, not the agent. Excluded from pass rates and rerun.',
    'asked-before-admin': 'The agent safely stopped before an admin step. Not counted as a failure.',
  };

  const state = { release: null, tab: 'overview', appMetric: 'qualityRate' };

  // ---------- formatting ----------
  function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }
  const NA = '<span class="muted" title="No data">n/a</span>';
  const pctText = (x) => `${Math.round(x * 100)}%`;
  const pct = (x) => (x == null ? NA : pctText(x));
  const num = (x, d = 1) => (x == null ? NA : x.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d }));
  function big(x) {
    if (x == null) return NA;
    if (x >= 1e6) return `${(x / 1e6).toFixed(1)}M`;
    if (x >= 1e3) return `${(x / 1e3).toFixed(0)}k`;
    return String(Math.round(x));
  }
  const ci = (m) => (m.passCi ? `<span class="ci">95% CI ${Math.round(m.passCi[0] * 100)}–${Math.round(m.passCi[1] * 100)}%</span>` : '');
  const frac = (a, b) => (b ? `${a}/${b}` : NA);
  function signed(x, fmt) {
    if (x == null) return NA;
    const sign = x > 0 ? '+' : x < 0 ? '−' : '±';
    return `${sign}${fmt(Math.abs(x))}`;
  }
  const diffPts = (x) => signed(x, (v) => `${Math.round(v * 100)} pts`);
  const diffNum = (x, d = 1) => signed(x, (v) => v.toFixed(d));
  const relChange = (w, o) => (w == null || !o ? null : (w - o) / o);
  const diffRel = (w, o) => signed(relChange(w, o), (v) => `${Math.round(v * 100)}%`);
  function cap(s) { return s ? s[0].toUpperCase() + s.slice(1) : s; }
  function modelLabel(m) {
    if (m === 'all') return 'All models';
    const parts = m.replace(/^claude-/, '').split('-');
    if (parts[0] === 'gpt') return `GPT-${parts.slice(1).map(cap).join(' ')}`;
    return parts.map(cap).join(' ');
  }
  const armLabel = (a) => ARM_INFO[a]?.label ?? a;
  const armHtml = (a) => `${esc(armLabel(a))}<span class="arm-id">${esc(a)}</span>`;
  const tip = (text, key) => `<span class="tip" title="${esc(DEFS[key])}">${text}</span>`;
  function table(caption, head, body) {
    return `<div class="table-scroll"><table><caption>${caption}</caption><thead>${head}</thead><tbody>${body}</tbody></table></div>`;
  }

  const view = (b) => b;
  const hasBoth = (b) => b && b.arms.includes(BASE) && b.arms.includes(WITH);

  // ---------- chart: one bar per setup, grouped by model ----------
  let chartId = 0;
  const armClass = (a) => (a === BASE ? 'without' : a === WITH ? 'with' : 'cli');
  function groupedBars({ title, desc, arms, groups, max = 1, format = pctText }) {
    const id = `chart-${++chartId}`;
    const W = 640, H = 240, left = 44, bottom = 34, top = 18;
    const plotW = W - left - 10, plotH = H - top - bottom;
    const gw = plotW / groups.length, bw = Math.min(48, (gw - 24) / arms.length);
    let bars = '';
    groups.forEach((g, gi) => {
      const x0 = left + gi * gw + (gw - bw * arms.length) / 2;
      g.values.forEach((v, si) => {
        if (v == null) return;
        const h = (Math.min(v, max) / max) * plotH, x = x0 + si * bw, y = top + plotH - h;
        bars += `<rect class="${armClass(arms[si])}" x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${(bw - 4).toFixed(1)}" height="${h.toFixed(1)}"><title>${esc(g.label)}, ${esc(armLabel(arms[si]))}: ${esc(format(v))}</title></rect>`;
        bars += `<text x="${(x + (bw - 4) / 2).toFixed(1)}" y="${(y - 4).toFixed(1)}" text-anchor="middle" class="val">${esc(format(v))}</text>`;
      });
      bars += `<text x="${(left + gi * gw + gw / 2).toFixed(1)}" y="${H - bottom + 18}" text-anchor="middle">${esc(g.label)}</text>`;
    });
    const ticks = [0, 0.5, 1].map((f) => {
      const y = top + plotH - f * plotH;
      return `<line class="axis" x1="${left}" x2="${W - 10}" y1="${y}" y2="${y}"/><text x="${left - 6}" y="${y + 4}" text-anchor="end">${esc(format(f * max))}</text>`;
    }).join('');
    return `<figure class="chart"><figcaption id="${id}-t">${esc(title)}</figcaption>
      <svg viewBox="0 0 ${W} ${H}" width="100%" role="img" aria-labelledby="${id}-t ${id}-d"><desc id="${id}-d">${esc(desc)}</desc>${ticks}${bars}</svg>
      <div class="legend" aria-hidden="true">${arms.map((a) => `<span class="${armClass(a)}">${esc(armLabel(a))}</span>`).join('')}</div></figure>`;
  }

  // ---------- shared tables ----------
  // One row group per arm: "All models", then one row per model.
  function armModelRows(b, byArm, cols) {
    let body = '';
    for (const arm of b.arms) {
      const rows = [['all', byArm[arm].all], ...b.models.map((m) => [m, byArm[arm].byModel[m]])];
      rows.forEach(([m, v], i) => {
        const armCell = i === 0 ? `<th scope="rowgroup" rowspan="${rows.length}" class="text">${armHtml(arm)}</th>` : '';
        body += `<tr class="${m === 'all' ? 'all-models' : ''}">${armCell}<th scope="row" class="text">${esc(modelLabel(m))}</th>${cols.map((c) => `<td>${c.cell(v)}</td>`).join('')}</tr>`;
      });
    }
    return body;
  }
  const armModelHead = (cols) => `<tr><th scope="col" class="text">Setup</th><th scope="col" class="text">Model</th>${cols.map((c) => `<th scope="col">${c.h}</th>`).join('')}</tr>`;

  function flagList(s, sanityText) {
    const items = s.flags.map((f) => (f.kind === 'sanity' ? `<li><span class="tag">${esc(sanityText)}</span> ${esc(f.text)}</li>` : `<li>${esc(f.text)}</li>`));
    return items.length ? `<ul>${items.join('')}</ul>` : '<span class="muted">None</span>';
  }

  // Scenario x (arm, model) matrix of one metric, for the collapsed per-model breakdowns.
  function perModelMatrix(b, cell, caption, rowHeader, sanityText) {
    const cols = ['all', ...b.models];
    const head = `<tr><th scope="col" rowspan="2" class="text">${rowHeader}</th>${b.arms.map((a) => `<th scope="colgroup" colspan="${cols.length}">${armHtml(a)}</th>`).join('')}<th scope="col" rowspan="2" class="text">Flags</th></tr>
      <tr>${b.arms.map(() => cols.map((m) => `<th scope="col">${esc(m === 'all' ? 'All' : modelLabel(m))}</th>`).join('')).join('')}</tr>`;
    const body = b.perScenario.map((s) => {
      const flagged = new Set(s.flags.filter((f) => f.model).map((f) => `${f.arm}|${f.model}`));
      const cells = b.arms.map((a) => cols.map((m) => {
        const v = m === 'all' ? s.cells[a].all : s.cells[a].byModel[m];
        const mark = flagged.has(`${a}|${m}`) ? ' <span class="flag-mark" aria-label="flagged">!</span>' : '';
        return `<td>${cell(v)}${mark}</td>`;
      }).join('')).join('');
      return `<tr><th scope="row" class="text">${esc(s.scenario)}</th>${cells}<td class="flags">${flagList(s, sanityText)}</td></tr>`;
    }).join('');
    return table(caption, head, body);
  }

  // ---------- Overview ----------
  const RATE_METRICS = [
    { key: 'qualityRate', def: 'quality', phrase: (w, o) => `apps met ${w} of quality checks instead of ${o}` },
    { key: 'reqRate', def: 'requirements', phrase: (w, o) => `apps met ${w} of the requested features instead of ${o}` },
    { key: 'worksRate', def: 'works', phrase: (w, o) => `apps built and launched in ${w} of runs instead of ${o}` },
  ];

  function verdict(b) {
    const e = b.effect;
    const o = b.headline[BASE].all, w = b.headline[WITH].all;
    const ranked = RATE_METRICS.filter((m) => e.all[m.key] != null).sort((x, y) => Math.abs(e.all[y.key]) - Math.abs(e.all[x.key]));
    if (!ranked.length) return '';
    const top = ranked[0];
    const sign = Math.sign(e.all[top.key]);
    const models = b.models.filter((m) => e.byModel[m]?.[top.key] != null);
    const agree = models.filter((m) => Math.sign(e.byModel[m][top.key]) === sign).length;
    const scope = !models.length ? '' : agree === models.length ? `, on all ${models.length} models` : `, on ${agree} of ${models.length} models`;
    let text = `With winapp and the skills, ${top.phrase(pctText(w[top.key]), pctText(o[top.key]))}${sign ? scope : ''}.`;
    const rc = relChange(w.creditsMean, o.creditsMean);
    if (rc != null && Math.round(rc * 100) !== 0) {
      text += ` They used ${Math.round(Math.abs(rc) * 100)}% ${rc > 0 ? 'more' : 'fewer'} credits per app (${w.creditsMean.toFixed(0)} instead of ${o.creditsMean.toFixed(0)}).`;
    }
    return text;
  }

  function tile(label, defKey, without, withV, change, sub = '', cliV = null) {
    return `<div class="tile" role="group" aria-label="${esc(label)}">
      <div class="tile-label">${tip(esc(label), defKey)}</div>
      <div class="tile-value"><span class="from" title="${esc(armLabel(BASE))}">${without}</span> <span aria-hidden="true">→</span><span class="sr-only"> to </span> <span class="to" title="${esc(armLabel(WITH))}">${withV}</span></div>
      <div class="tile-change">${change}</div>${cliV != null ? `<div class="tile-sub">${esc(armLabel(CLI))}: ${cliV}</div>` : ''}${sub ? `<div class="tile-sub">${sub}</div>` : ''}</div>`;
  }

  function renderOverview(r) {
    const b = view(r.benchmarks.apps);
    if (!hasBoth(b)) return `<h2>Overview</h2><p>This release has no <code>apps</code> runs for both setups, so there is nothing to compare.</p>`;
    const o = b.headline[BASE].all, w = b.headline[WITH].all, e = b.effect.all;
    const c = b.headline[CLI]?.all;
    const mid = (f) => (c ? f(c) : null);
    const tiles = [
      tile('Quality checks met', 'quality', pct(o.qualityRate), pct(w.qualityRate), diffPts(e.qualityRate), '', mid((x) => pct(x.qualityRate))),
      tile('Requirements met', 'requirements', pct(o.reqRate), pct(w.reqRate), diffPts(e.reqRate), '', mid((x) => pct(x.reqRate))),
      tile('Built and launched', 'works', pct(o.worksRate), pct(w.worksRate), diffPts(e.worksRate), `${o.worked} of ${o.total} → ${w.worked} of ${w.total} runs`, mid((x) => pct(x.worksRate))),
      tile('Credits per app', 'credits', num(o.creditsMean, 0), num(w.creditsMean, 0), diffRel(w.creditsMean, o.creditsMean),
        `${tip('Minutes per app', 'minutes')}: ${num(o.minutesMean)} → ${num(w.minutesMean)}`, mid((x) => num(x.creditsMean, 0))),
    ].join('');

    const arms = b.arms.filter((a) => [BASE, CLI, WITH].includes(a));
    const chart = groupedBars({
      title: 'Quality checks met, by model',
      desc: `For each model, the share of quality checks met in each setup: ${arms.map(armLabel).join(', ')}. The numbers are also in the details table.`,
      arms,
      groups: [{ label: 'All models', values: arms.map((a) => b.headline[a].all.qualityRate) }, ...b.models.map((m) => ({ label: modelLabel(m), values: arms.map((a) => b.headline[a].byModel[m].qualityRate) }))],
    });

    const cols = [
      { h: tip('Valid of total', 'valid'), cell: (m) => `${m.valid} of ${m.total}` },
      { h: tip('Pass rate', 'pass'), cell: (m) => `${pct(m.passRate)}${ci(m)}` },
      { h: tip('Built and launched', 'works'), cell: (m) => pct(m.worksRate) },
      { h: tip('Quality checks', 'quality'), cell: (m) => `<span title="${m.qualityMet} of ${m.qualityTotal}">${pct(m.qualityRate)}</span>` },
      { h: tip('Requirements', 'requirements'), cell: (m) => `<span title="${m.reqMet} of ${m.reqTotal}">${pct(m.reqRate)}</span>` },
      { h: tip('Design score', 'design'), cell: (m) => num(m.designMean) },
      { h: tip('Credits per app', 'credits'), cell: (m) => num(m.creditsMean) },
      { h: tip('Minutes per app', 'minutes'), cell: (m) => num(m.minutesMean) },
      { h: 'Tokens per app', cell: (m) => big(m.tokensMean) },
    ];
    const effectRows = [['all', b.effect.all], ...b.models.map((m) => [m, b.effect.byModel[m]])].map(([m, d]) =>
      `<tr class="${m === 'all' ? 'all-models' : ''}"><th scope="row" class="text">${esc(modelLabel(m))}</th><td>${diffPts(d.passRate)}</td><td>${diffPts(d.worksRate)}</td><td>${diffPts(d.qualityRate)}</td><td>${diffPts(d.reqRate)}</td><td>${diffNum(d.designMean)}</td><td>${diffNum(d.creditsMean)}</td><td>${diffNum(d.minutesMean)}</td></tr>`).join('');
    const effectHead = '<tr><th scope="col" class="text">Model</th><th scope="col">Pass rate</th><th scope="col">Built and launched</th><th scope="col">Quality checks</th><th scope="col">Requirements</th><th scope="col">Design score</th><th scope="col">Credits per app</th><th scope="col">Minutes per app</th></tr>';
    const t = D.trend.apps;
    const trend = t.length >= 2
      ? `<p>Pass rate ${esc(armLabel(WITH).toLowerCase())}, by release: ${t.map((p) => `${esc(p.release)} ${pct(p.all)}`).join(', ')}.</p>`
      : '<p class="muted">A release-by-release trend appears once two releases are published.</p>';

    return `<h2 class="sr-only">Overview</h2>
      <p class="verdict">${esc(verdict(b))}</p>
      <p class="muted context">${b.scenarios.length} app scenarios, ${b.models.length} models (${b.models.map(modelLabel).join(', ')}), ${b.arms.reduce((s, a) => s + b.headline[a].all.total, 0)} runs. Hover a label for its definition.</p>
      <div class="tiles">${tiles}</div>
      ${chart}
      <details class="more"><summary>Show details</summary>
        <p>Pass rate is ${arms.map((a) => `${pct(b.headline[a].all.passRate)} ${esc(armLabel(a).toLowerCase())}`).join(', ')}, so it does not separate the setups; the tiles above use the measures that do.</p>
        ${table('Results by setup and model (valid runs, except Built and launched)', armModelHead(cols), armModelRows(b, b.headline, cols))}
        ${table(`Change: ${esc(armLabel(WITH))} minus ${esc(armLabel(BASE))}`, effectHead, effectRows)}
        <h3>Trend</h3>${trend}
      </details>`;
  }

  // ---------- By app ----------
  const APP_METRICS = {
    qualityRate: { label: 'Quality checks met', def: 'quality', cell: pct, diff: diffPts },
    reqRate: { label: 'Requirements met', def: 'requirements', cell: pct, diff: diffPts },
    worksRate: { label: 'Built and launched', def: 'works', cell: pct, diff: diffPts },
    designMean: { label: 'Design score', def: 'design', cell: (x) => num(x), diff: (x) => diffNum(x) },
    creditsMean: { label: 'Credits per app', def: 'credits', cell: (x) => num(x, 0), diff: (x) => diffNum(x, 0) },
    minutesMean: { label: 'Minutes per app', def: 'minutes', cell: (x) => num(x), diff: (x) => diffNum(x) },
  };

  function renderApps(r) {
    const b = view(r.benchmarks.apps);
    if (!hasBoth(b)) return `<h2>By app</h2><p>This release has no <code>apps</code> runs for both setups.</p>`;
    const k = state.appMetric, M = APP_METRICS[k];
    const rows = b.perScenario
      .map((s) => ({ s, o: s.cells[BASE].all[k], c: s.cells[CLI]?.all[k], w: s.cells[WITH].all[k], d: s.effect?.all[k] }))
      .sort((x, y) => (y.d ?? -Infinity) - (x.d ?? -Infinity));
    const hasCli = b.arms.includes(CLI);
    const body = rows.map(({ s, o, c, w, d }) => {
      const notes = [];
      if (s.flags.some((f) => f.kind !== 'sanity')) notes.push(`<span class="flag-mark" title="${esc(s.flags.filter((f) => f.kind !== 'sanity').map((f) => f.text).join('\n'))}">!</span><span class="sr-only"> has flags, see the per-model breakdown</span>`);
      return `<tr><th scope="row" class="text">${esc(s.scenario)} ${notes.join('')}</th><td>${M.cell(o)}</td>${hasCli ? `<td class="cli-col">${M.cell(c)}</td>` : ''}<td>${M.cell(w)}</td><td class="change">${M.diff(d)}</td></tr>`;
    }).join('');
    const head = `<tr><th scope="col" class="text">App</th><th scope="col">${armHtml(BASE)}</th>${hasCli ? `<th scope="col">${armHtml(CLI)}</th>` : ''}<th scope="col">${armHtml(WITH)}</th><th scope="col" title="${esc(armLabel(WITH))} minus ${esc(armLabel(BASE))}">Change</th></tr>`;
    const options = Object.entries(APP_METRICS).map(([key, v]) => `<option value="${key}"${key === k ? ' selected' : ''}>${esc(v.label)}</option>`).join('');
    return `<h2>By app</h2>
      <div class="inline-control"><label for="app-metric">Compare</label><select id="app-metric">${options}</select><span class="muted">${esc(DEFS[M.def])}</span></div>
      ${table(`${esc(M.label)} by app, largest change first (all models)`, head, body)}
      <details class="more"><summary>Per-model breakdown and flags</summary>
        ${perModelMatrix(b, (v) => M.cell(v[k]), `${esc(M.label)} by app, setup and model`, 'App', 'Sanity check')}
      </details>`;
  }

  // ---------- Skills ----------
  function renderSkills(r) {
    const benches = Object.entries(r.benchmarks).map(([bn, b]) => [bn, view(b)]).filter(([, b]) => b.arms.includes(WITH));
    const names = new Set();
    for (const [, b] of benches) Object.keys(b.skills[WITH].names).forEach((n) => names.add(n));
    if (!names.size) return `<h2>Skills</h2><p>No skills were recorded in this release.</p>`;
    const share = (b, n) => {
      const u = b.skills[WITH];
      return u.recorded ? (u.names[n]?.trials ?? 0) / u.recorded : null;
    };
    const first = benches[0][1];
    const sorted = [...names].sort((x, y) => (share(first, y) ?? 0) - (share(first, x) ?? 0) || x.localeCompare(y));
    const cell = (b, n) => {
      const u = b.skills[WITH];
      if (!u.recorded) return '<td><span class="muted">not recorded</span></td>';
      const v = u.names[n]?.trials ?? 0, p = v / u.recorded;
      return `<td class="text"><span class="bar" aria-hidden="true"><span style="width:${Math.round(p * 100)}%"></span></span> ${v} of ${u.recorded} <span class="muted">(${pctText(p)})</span></td>`;
    };
    const head = `<tr><th scope="col" class="text">Skill</th>${benches.map(([bn]) => `<th scope="col"><code>${esc(bn)}</code> runs that loaded it</th>`).join('')}<th scope="col" class="text">${tip('Claim', 'claim')}</th></tr>`;
    const body = sorted.map((n) => `<tr><th scope="row" class="text"><code>${esc(n)}</code></th>${benches.map(([, b]) => cell(b, n)).join('')}<td class="text"><span class="tag info">Not enough data</span></td></tr>`).join('');
    const partial = benches.filter(([, b]) => b.skills[WITH].recorded && b.skills[WITH].recorded < b.skills[WITH].trials)
      .map(([bn, b]) => `<code>${esc(bn)}</code> recorded skills in ${b.skills[WITH].recorded} of ${b.skills[WITH].trials} runs`);
    return `<h2>Skills</h2>
      <p>Which skills the agent loaded ${esc(armLabel(WITH).toLowerCase())}, and in how many runs. The other setups have no skills installed, so they are not shown here.</p>
      ${table(`Skills loaded, ${esc(armLabel(WITH).toLowerCase())}`, head, body)}
      ${partial.length ? `<p class="muted">${partial.join('; ')}.</p>` : ''}`;
  }

  // ---------- Details ----------
  function outcomesSection(r) {
    let html = '<h3>Outcomes</h3>';
    for (const [name, full] of Object.entries(r.benchmarks)) {
      const b = view(full);
      if (!b.arms.length) continue;
      const outs = b.outcomeNames.filter((o) => b.arms.some((a) => b.headline[a].all.outcomes[o]));
      const cols = [...outs.map((o) => ({ h: `<code title="${esc(OUTCOME_INFO[o] ?? '')}">${esc(o)}</code>`, cell: (m) => m.outcomes[o] ?? 0 })), { h: 'Total', cell: (m) => m.total }, { h: 'Credits spent', cell: (m) => num(m.creditsSpent, 0) }];
      html += table(`<code>${esc(name)}</code> outcomes by setup and model`, armModelHead(cols), armModelRows(b, b.headline, cols));
      if (b.notValid.length) {
        const rows = b.notValid.map((n) => `<tr><th scope="row" class="text">${esc(n.scenario)}</th><td class="text">${esc(armLabel(n.arm))}</td><td class="text">${esc(modelLabel(n.model))}</td><td>${n.trial}</td><td class="text"><code>${esc(n.outcome)}</code></td><td class="text">${n.error ? esc(n.error) : '<span class="muted">None recorded</span>'}</td><td>${num(n.credits)}</td></tr>`).join('');
        html += `<details class="more"><summary>${b.notValid.length} <code>${esc(name)}</code> run(s) that are not valid</summary>${table(`Runs that are not valid`, '<tr><th scope="col" class="text">Scenario</th><th scope="col" class="text">Setup</th><th scope="col" class="text">Model</th><th scope="col">Run</th><th scope="col" class="text">Outcome</th><th scope="col" class="text">Error</th><th scope="col">Credits</th></tr>', rows)}</details>`;
      }
      if (name === 'apps' && b.launchLevelNames.length) {
        const lv = b.launchLevelNames;
        const lcols = lv.map((l) => ({ h: l === 'not measured' ? 'Not measured' : `Level ${esc(l)}`, cell: (m) => m.launchLevels[l] ?? 0 }));
        html += `<details class="more"><summary>How far the apps launched</summary><p>0 did not run, 1 a window exists, 2 controls respond to input, 3 the scenario's main screen appeared. "Not measured": the app ran but level 2 could not be checked.</p>${table('Launch level by setup and model (all runs)', armModelHead(lcols), armModelRows(b, b.headline, lcols))}</details>`;
      }
    }
    return html;
  }

  function tasksSection(r) {
    const b = view(r.benchmarks.tasks);
    if (!b || !b.arms.length) return '<h3>Tasks</h3><p>This release has no <code>tasks</code> runs.</p>';
    const sanity = b.sanityScenarios;
    const note = sanity.length
      ? `<p><strong>Tasks don't separate the setups yet:</strong> ${sanity.length} of ${b.scenarios.length} tasks pass in ${Math.round(D.rules.sanityPassRate * 100)}% or more of runs even ${esc(armLabel(BASE).toLowerCase())} (<a href="${REPO}/issues/1253">#1253</a>).</p>`
      : '';
    const excl = (arm, m) => (m.byModelKey === 'all' ? b.headlineExcludingSanity[arm].all : b.headlineExcludingSanity[arm].byModel[m.byModelKey]);
    let body = '';
    for (const arm of b.arms) {
      const rows = [['all', b.headline[arm].all], ...b.models.map((m) => [m, b.headline[arm].byModel[m]])];
      rows.forEach(([m, v], i) => {
        const armCell = i === 0 ? `<th scope="rowgroup" rowspan="${rows.length}" class="text">${armHtml(arm)}</th>` : '';
        const x = excl(arm, { byModelKey: m });
        body += `<tr class="${m === 'all' ? 'all-models' : ''}">${armCell}<th scope="row" class="text">${esc(modelLabel(m))}</th><td>${v.valid} of ${v.total}</td><td>${frac(v.passed, v.valid)}</td><td>${pct(v.passRate)}${ci(v)}</td><td>${x.valid ? `${pct(x.passRate)} <span class="ci">${x.passed}/${x.valid}</span>` : NA}</td><td>${v.askedBeforeAdmin}</td><td>${num(v.creditsMean)}</td><td>${num(v.minutesMean, 2)}</td></tr>`;
      });
    }
    const head = `<tr><th scope="col" class="text">Setup</th><th scope="col" class="text">Model</th><th scope="col">Valid of total</th><th scope="col">Passed</th><th scope="col">Pass rate, all tasks</th><th scope="col">Pass rate without sanity checks</th><th scope="col">Asked before admin</th><th scope="col">Credits per run</th><th scope="col">Minutes per run</th></tr>`;
    const eff = hasBoth(b)
      ? `<p>Change ${esc(armLabel(WITH).toLowerCase())} minus ${esc(armLabel(BASE).toLowerCase())}: pass rate ${diffPts(b.effect.all.passRate)} on all tasks, ${diffPts(b.effectExcludingSanity.all.passRate)} without sanity checks; credits per run ${diffNum(b.effect.all.creditsMean)}.</p>`
      : '';
    return `<h3>Tasks</h3>
      <p>Small scripted jobs (packaging, signing, UI) in Windows Sandbox, ${b.scenarios.length} tasks.</p>
      ${note}${eff}
      ${table('Tasks results by setup and model', head, body)}
      <details class="more"><summary>Per task</summary>
        ${perModelMatrix(b, (v) => (v.valid ? `<span title="${v.passed} of ${v.valid}">${pct(v.passRate)}</span>` : NA), 'Pass rate by task, setup and model', 'Task', 'Sanity check, not in headline')}
      </details>`;
  }

  function commandsSection(r) {
    const benches = Object.entries(r.benchmarks).map(([bn, b]) => [bn, view(b)]);
    const cols = benches.flatMap(([bn, b]) => b.arms.map((a) => ({ bn, b, a })));
    const names = new Set();
    for (const c of cols) Object.keys(c.b.winappCommands[c.a].names).forEach((n) => names.add(n));
    if (!names.size) return '';
    const total = (n) => cols.reduce((s, c) => s + (c.b.winappCommands[c.a].names[n]?.trials ?? 0), 0);
    const sorted = [...names].sort((x, y) => total(y) - total(x) || x.localeCompare(y));
    const head = `<tr><th scope="col" rowspan="2" class="text">Command</th>${benches.map(([bn, b]) => `<th scope="colgroup" colspan="${b.arms.length}"><code>${esc(bn)}</code></th>`).join('')}</tr><tr>${cols.map((c) => `<th scope="col">${armHtml(c.a)}</th>`).join('')}</tr>`;
    const body = sorted.map((n) => `<tr><th scope="row" class="text"><code>${esc(n)}</code></th>${cols.map((c) => {
      const u = c.b.winappCommands[c.a];
      if (!u.recorded) return '<td><span class="muted">not recorded</span></td>';
      return `<td>${u.names[n]?.trials ?? 0} of ${u.recorded}</td>`;
    }).join('')}</tr>`).join('');
    return `<h3>winapp commands</h3><details class="more"><summary>Which winapp commands the agent ran, in how many runs</summary>${table('winapp commands by benchmark and setup', head, body)}</details>`;
  }

  function methodSection(r) {
    const commits = r.commits.map((c) => `<a href="${REPO}/commit/${esc(c)}"><code>${esc(c.slice(0, 8))}</code></a>`).join(', ') || NA;
    const benches = Object.entries(r.benchmarks);
    const scoring = benches.map(([n, b]) => `<code>${esc(n)}</code>: ${b.scoringVersions.map(esc).join(', ')}`).join('; ');
    const winappVersions = [...new Set(benches.flatMap(([, b]) => b.winappVersions))];
    const rawUrl = `${D.rawDataUrl}/${encodeURIComponent(r.release)}`;
    const warnings = D.warnings.length
      ? `<details><summary>${D.warnings.length} data note(s) from the build</summary><ul>${D.warnings.map((w) => `<li>${esc(w)}</li>`).join('')}</ul></details>`
      : '<p>The build found no problems in the data files.</p>';
    return `<h3>How we measured</h3>
      <dl class="facts">
        <dt>Release</dt><dd><code>${esc(r.release)}</code></dd>
        <dt>Commit(s) tested</dt><dd>${commits}</dd>
        <dt>Runs started</dt><dd>${esc(r.firstStartedAt?.slice(0, 10) ?? 'n/a')} to ${esc(r.lastStartedAt?.slice(0, 10) ?? 'n/a')}</dd>
        <dt>Models</dt><dd>${r.models.map(modelLabel).map(esc).join(', ')}</dd>
        <dt>Scoring version</dt><dd>${scoring}</dd>
        <dt>winapp version</dt><dd>${winappVersions.length ? winappVersions.map(esc).join(', ') : 'not recorded'}</dd>
      </dl>
      <ul>
        <li>Three setups: ${[BASE, CLI, WITH].map((a) => `<strong>${esc(armLabel(a))}</strong> (<code>${esc(a)}</code>): ${esc(ARM_INFO[a].desc)}`).join(' ')} Each "change" is ${esc(armLabel(WITH).toLowerCase())} minus ${esc(armLabel(BASE).toLowerCase())}. The Skills tab shows only ${esc(armLabel(WITH).toLowerCase())}, because the other setups have no skills installed.</li>
        <li>In the <code>apps</code> benchmark an agent builds a complete WinUI app from a written brief. Each run is one agent attempt at one scenario, with one model and one setup.</li>
        <li>${esc(DEFS.valid)}</li>
        <li>${esc(DEFS.works)}</li>
        <li>${esc(DEFS.quality)}</li>
        <li>${esc(DEFS.requirements)}</li>
        <li>${esc(DEFS.pass)} Pass rates show a 95% confidence interval (Wilson).</li>
        <li>${esc(DEFS.design)}</li>
        <li>${esc(DEFS.credits)} No dollar prices are used.</li>
        <li>A scenario passed in ${Math.round(D.rules.sanityPassRate * 100)}% or more of valid runs ${esc(armLabel(BASE).toLowerCase())} is a sanity check: it can't show whether the tools help (<a href="${REPO}/issues/1253">#1253</a>). Sanity-check tasks are left out of the tasks headline; apps scenarios are flagged but kept.</li>
        <li>Flags: a model is flagged when its pass rate on a setup differs from the other models' by ${Math.round(D.rules.modelSplitPoints * 100)} points or more, or when it uses more than ${D.rules.costMultiple}x the credits it used on the other setups. Flags point to where to look; they are not results.</li>
        <li>Skill claims (Helps, Helps but costs more, No measurable difference, Not enough data) will come from fixed rules in <a href="${REPO}/issues/1182">#1182</a>. Until then every skill shows "Not enough data".</li>
      </ul>
      <p>Raw run files: ${r.release === 'dev'
        ? 'development (<code>dev</code>) runs are not published.'
        : `<a href="${esc(rawUrl)}">${esc(rawUrl)}</a> (works once the <code>benchmark-data</code> branch exists).`} Format: <a href="${REPO}/blob/main/benchmarks/RESULTS-FORMAT.md">RESULTS-FORMAT.md</a>. Every number on this page: <a href="data.json">data.json</a>. To rerun: <a href="${REPO}/blob/main/benchmarks/apps/README.md">apps</a>, <a href="${REPO}/blob/main/benchmarks/tasks/README.md">tasks</a>.</p>
      ${warnings}`;
  }

  function renderDetails(r) {
    return `<h2>Details</h2>${outcomesSection(r)}${tasksSection(r)}${commandsSection(r)}${methodSection(r)}`;
  }

  // ---------- page ----------
  function render() {
    const r = D.releases.find((x) => x.release === state.release) ?? D.releases[0];
    state.release = r.release;
    $('release-meta').textContent = `${r.firstStartedAt?.slice(0, 10) ?? ''} to ${r.lastStartedAt?.slice(0, 10) ?? ''}`;
    $('banner').innerHTML = r.release === 'dev'
      ? '<p class="banner">Development runs, not a release. Numbers may change.</p>'
      : '';
    $('panel-overview').innerHTML = renderOverview(r);
    $('panel-apps').innerHTML = renderApps(r);
    $('panel-skills').innerHTML = renderSkills(r);
    $('panel-details').innerHTML = renderDetails(r);
    $('footer').innerHTML = `Built ${esc(D.generatedAt.slice(0, 16).replace('T', ' '))} UTC. All numbers: <a href="data.json">data.json</a>.`;
    bindAppMetric();
  }

  // The selector is redrawn with its panel, so bind it after each draw.
  function bindAppMetric() {
    const sel = $('app-metric');
    if (!sel) return;
    sel.addEventListener('change', () => {
      state.appMetric = sel.value;
      $('panel-apps').innerHTML = renderApps(D.releases.find((x) => x.release === state.release));
      bindAppMetric();
      $('app-metric').focus();
    });
  }

  function selectTab(name, focus) {
    const tabs = [...document.querySelectorAll('[role="tab"]')];
    if (!tabs.some((t) => t.dataset.tab === name)) name = 'overview';
    state.tab = name;
    for (const t of tabs) {
      const on = t.dataset.tab === name;
      t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1;
      $(t.getAttribute('aria-controls')).hidden = !on;
      if (on && focus) t.focus();
    }
  }

  function updateHash() {
    history.replaceState(null, '', `#${new URLSearchParams({ release: state.release, tab: state.tab })}`);
  }

  function init() {
    if (!D || !D.releases?.length) {
      $('main').innerHTML = '<p>No benchmark data was found. Build the site with <code>build.mjs --data &lt;folder&gt;</code>.</p>';
      return;
    }
    const params = new URLSearchParams(location.hash.slice(1));
    state.release = D.releases.some((r) => r.release === params.get('release')) ? params.get('release') : D.releases[0].release;
    const sel = $('release');
    sel.innerHTML = D.releases.map((r) => `<option value="${esc(r.release)}">${esc(r.release === 'dev' ? 'dev (development runs)' : r.release)}</option>`).join('');
    sel.value = state.release;
    sel.addEventListener('change', () => { state.release = sel.value; render(); updateHash(); });

    const tabs = [...document.querySelectorAll('[role="tab"]')];
    tabs.forEach((t, i) => {
      t.addEventListener('click', () => { selectTab(t.dataset.tab); updateHash(); });
      t.addEventListener('keydown', (e) => {
        const j = { ArrowRight: (i + 1) % tabs.length, ArrowLeft: (i - 1 + tabs.length) % tabs.length, Home: 0, End: tabs.length - 1 }[e.key];
        if (j === undefined) return;
        e.preventDefault();
        selectTab(tabs[j].dataset.tab, true);
        updateHash();
      });
    });
    render();
    selectTab(params.get('tab') ?? 'overview');
  }

  init();
})();
