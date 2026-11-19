// Offline UI tests for the PR board. See test/fixtures/world.mjs for what each PR and issue is for.
import assert from "node:assert/strict";
import * as fs from "node:fs";
import { HEAD, REPO } from "./fixtures/world.mjs";

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const J = JSON.stringify;

// ---- Page helpers -----------------------------------------------------------------------------------

// Every row in the Overview sections: section, kind, number, text, unread dot, spinner.
const rows = (page) => page.eval(`[...document.querySelectorAll('#overview .ov-sec')].flatMap((s) => [...s.querySelectorAll('.ov-row')].map((r) => { const t = r.querySelector('.ov-title'); return { sec: s.id.replace('sec-', ''), kind: t.dataset.kind, n: Number(t.dataset.pr), text: r.innerText.replace(/\\s+/g, ' '), unread: !!r.querySelector('.unread-dot'), spin: !!r.querySelector('svg.spin') }; }))`);
const ymCards = (page) => page.eval(`[...document.querySelectorAll('#overview .ym-card')].map((c) => ({ n: Number(c.querySelector('.ov-title').dataset.pr), kind: c.querySelector('.ov-title').dataset.kind, text: c.innerText.replace(/\\s+/g, ' ') }))`);
const rowText = async (page, n, kind = "pr") => (await rows(page)).find((r) => r.n === n && r.kind === kind)?.text || null;
const flyOpen = (page) => page.eval(`!!document.querySelector('#flyHost .fly')`);
const sheetOpen = (page) => page.eval(`document.body.classList.contains('sheet')`);
const text = (page, sel) => page.eval(`(document.querySelector(${J(sel)})?.innerText || '').replace(/\\s+/g, ' ').trim()`);
const exists = (page, sel) => page.eval(`!!document.querySelector(${J(sel)})`);

async function openRow(page, n, kind = "pr") {
    await page.click(`#overview .ov-sec .ov-title[data-kind="${kind}"][data-pr="${n}"]`);
    await page.waitFor(`!!document.querySelector('#flyHost .fly') || document.body.classList.contains('sheet')`, 3000, `#${n} to open`);
    await sleep(400); // the backdrop ignores clicks for 350ms after opening
}
// A board update from the server, like the agent replying somewhere else.
const poke = (ctx, n = 103) => ctx.board.tool("pr_board_set_summary", { repo: REPO, pr: n, summary: `Summary ${Date.now()}`, nextSteps: [], risk: "low" });
const state = (ctx) => JSON.parse(fs.readFileSync(ctx.board.stateFile, "utf8"));
const backdrop = async (page) => {
    // A point on the dimmed list: the left edge for the drawer, the top margin for the sheet.
    const sheet = await sheetOpen(page);
    return sheet ? { x: 520, y: 12 } : { x: 60, y: 500 };
};

export function register(test) {
    // ---- Overview, in both themes and both widths ----------------------------------------------------
    for (const theme of ["light", "dark"]) for (const width of [1040, 600]) {
        test(`overview sections, membership and type tags (${theme}, ${width}px)`, async (ctx) => {
            const { page } = ctx;
            const r = await rows(page);
            const keys = r.map((x) => `${x.kind}${x.n}`);
            assert.deepEqual(keys.filter((k, i) => keys.indexOf(k) !== i), [], "an item appears in two sections");
            const bySec = (s) => r.filter((x) => x.sec === s).map((x) => `${x.kind}${x.n}`).sort();
            assert.deepEqual(bySec("yours"), ["issue203", "pr101", "pr102", "pr107", "pr108"]);
            assert.deepEqual(bySec("reviewing"), ["pr104", "pr105"]);
            assert.deepEqual(bySec("other"), ["pr103", "pr106"]);
            assert.deepEqual(bySec("issues"), ["issue202"]);
            assert.ok(!keys.includes("issue201"), "Issue #201 is covered by PR #108, so it has no row");
            assert.match(await rowText(page, 108), /Draft PR #108/);
            assert.match(await rowText(page, 108), /Fixes Issue #201/);
            assert.match(await rowText(page, 101), /^.*\bPR #101\b/);
            assert.match(await rowText(page, 202, "issue"), /Issue #202/);
            assert.equal(await page.eval(`document.documentElement.scrollWidth <= innerWidth + 1`), true, "the page scrolls sideways");
            await ctx.shot(`overview-${theme}-${width}`);
        }, { theme, width });

        test(`peek and expand render cleanly (${theme}, ${width}px)`, async (ctx) => {
            const { page } = ctx;
            await openRow(page, 102);
            await ctx.shot(`peek-${theme}-${width}`);
            await page.key("e");
            await page.waitFor(`document.body.classList.contains('sheet')`, 2000, "the sheet");
            await ctx.shot(`sheet-${theme}-${width}`);
            assert.equal(await page.eval(`document.documentElement.scrollWidth <= innerWidth + 1`), true, "the sheet scrolls sideways");
            if (width < 700) assert.equal(await page.eval(`getComputedStyle(document.querySelector('#wsHead .sheet-bar .back')).display !== 'none'`), true, "narrow sheet shows Back");
        }, { theme, width });
    }

    // ---- Ball states, chips, counts ------------------------------------------------------------------
    test("each row shows whose move it is", async ({ page }) => {
        assert.match(await rowText(page, 102), /Preparing it for review · 2\/4 ready · Then: request a reviewer/);
        assert.match(await rowText(page, 105), /Needs your decision: Approve now, or wait for the changelog\?/);
        assert.match(await rowText(page, 106), /merge queue/i);
        assert.match(await rowText(page, 107), /Approved: merge it/);
        assert.match(await rowText(page, 101), /Watching.*5\/5 ready/);
        assert.match(await rowText(page, 103), /Review it/);
        assert.match(await rowText(page, 202, "issue"), /Assigned to you/);
    });

    test("a row never says 'Needs your decision' while showing an agent spinner", async ({ page }) => {
        const bad = (await rows(page)).filter((r) => r.spin && /Needs your decision/.test(r.text));
        assert.deepEqual(bad, []);
        assert.equal((await rows(page)).find((r) => r.n === 102).spin, true, "#102's agent is busy");
    });

    test("regression: a requested reviewer shows as 'Waiting on zed', not 'Needs reviewer'", async ({ page }) => {
        await openRow(page, 101);
        const head = await text(page, "#flyHost .fly-head");
        assert.doesNotMatch(head, /Needs reviewer/);
        assert.match(head, /Waiting on zed/);
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`);
        assert.doesNotMatch(await text(page, "#wsHead"), /Needs reviewer/);
        assert.match(await text(page, "#wsHead"), /Waiting on zed/);
    });

    test("regression: the unread count matches the rows with an unread dot", async ({ page }) => {
        const n = Number(await text(page, "#overview .unread-chip .n"));
        const dotted = (await rows(page)).filter((r) => r.unread);
        assert.equal(n, dotted.length, `chip says ${n}, rows with a dot: ${dotted.map((r) => r.n).join(", ")}`);
        assert.ok(dotted.some((r) => r.n === 102), "#102 has an unread reply");
        await page.click("#overview .unread-chip");
        await page.waitFor(`document.querySelector('#overview .unread-chip')?.getAttribute('aria-pressed') === 'true'`);
        assert.deepEqual((await rows(page)).map((r) => r.n).sort(), dotted.map((r) => r.n).sort(), "the Unread filter shows exactly those rows");
        await page.click('#overview [data-act="mark-all-read"]');
        await page.waitFor(`!document.querySelector('#overview .unread-chip')`, 4000, "the unread chip to go away");
        assert.equal((await rows(page)).filter((r) => r.unread).length, 0);
    });

    test("filter chips: counts match the rows they show", async ({ page }) => {
        const chips = await page.eval(`[...document.querySelectorAll('#overview .state-chip')].map((b) => ({ g: b.dataset.g, n: Number(b.querySelector('.n').innerText) }))`);
        assert.ok(chips.length >= 3);
        for (const { g, n } of chips) {
            await page.click(`#overview .state-chip[data-g="${g}"]`);
            await page.waitFor(`document.querySelector('#overview .state-chip[data-g="${g}"]')?.getAttribute('aria-pressed') === 'true'`);
            const shown = (await rows(page)).length;
            assert.equal(shown, n, `${g}: chip says ${n}, ${shown} rows shown`);
            assert.equal(await exists(page, "#overview .ym-box, #overview .ym-card"), false, "Your move box hides while filtering");
            await page.click(`#overview .state-chip[data-g="${g}"]`);
            await page.waitFor(`document.querySelector('#overview .state-chip[data-g="${g}"]')?.getAttribute('aria-pressed') !== 'true'`);
        }
    });

    // ---- Your move box ---------------------------------------------------------------------------------
    test("Your move: Done takes the card out without asking the agent", async (ctx) => {
        const { page } = ctx;
        assert.ok((await ymCards(page)).some((c) => c.n === 103));
        await page.click('#overview .ym-card [data-act="ball-done"][data-pr="103"]');
        await page.waitFor(`![...document.querySelectorAll('#overview .ym-card .ov-title')].some((t) => t.dataset.pr === '103')`, 4000, "#103 to leave Your move");
        assert.ok(state(ctx).prs["103"].ballDone, "Done is saved");
        assert.equal(ctx.host.sends.length, 0);
    });

    test("Your move: Snooze hides the card and counts it as snoozed", async (ctx) => {
        const { page } = ctx;
        await page.click('#overview .ym-card [data-act="snooze-menu"][data-pr="103"]');
        await page.waitFor(`!!document.querySelector('#overview .ym-card [data-act="ball-snooze"][data-pr="103"]')`);
        await page.click('#overview .ym-card [data-act="ball-snooze"][data-pr="103"]');
        await page.waitFor(`![...document.querySelectorAll('#overview .ym-card .ov-title')].some((t) => t.dataset.pr === '103')`, 4000, "#103 to leave Your move");
        assert.ok(new Date(state(ctx).prs["103"].snoozeUntil) > new Date());
        assert.equal(await text(page, '#overview .state-chip[data-g="snoozed"] .n'), "1");
    });

    test("Your move: a choice goes to the agent and clears the decision", async (ctx) => {
        const { page } = ctx;
        await page.click('#overview .ym-card [data-act="choose"][data-pr="105"][data-choice="Approve now"]');
        await page.waitFor(`![...document.querySelectorAll('#overview .ym-card .ov-title')].some((t) => t.dataset.pr === '105')`, 4000, "#105 to leave Your move");
        assert.equal(ctx.host.sends.length, 1);
        assert.match(ctx.host.sends[0].prompt, /Approve now/);
        assert.match(ctx.host.sends[0].prompt, /#105/);
    });

    // ---- Peek and expand -------------------------------------------------------------------------------
    test("peek: opens over the list; Esc closes it and keeps scroll and focus", async ({ page }) => {
        await page.eval(`document.getElementById('overview').scrollTop = 300`);
        await sleep(100);
        await openRow(page, 104);
        assert.equal(await flyOpen(page), true);
        assert.match(await text(page, "#flyHost .pager"), /^‹ \d+\/\d+ ›$/);
        await page.key("Escape");
        await page.waitFor(`!document.querySelector('#flyHost .fly')`);
        assert.equal(await page.eval(`document.getElementById('overview').scrollTop`), 300);
        assert.equal(await page.eval(`document.activeElement?.dataset?.pr`), "104");
    });

    test("peek: a real click on the dimmed list closes it", async ({ page }) => {
        await openRow(page, 104);
        await page.click(null, { at: await backdrop(page) });
        await page.waitFor(`!document.querySelector('#flyHost .fly')`, 2000, "the panel to close");
    });

    test("peek: a drag or text selection that ends on the list doesn't close it", async ({ page }) => {
        await openRow(page, 102);
        const inside = await page.box("#flyHost .fly-body p");
        await page.drag({ x: inside.x - 60, y: inside.y }, await backdrop(page));
        await sleep(200);
        assert.equal(await flyOpen(page), true, "a drag from inside closed it");
        assert.ok((await page.eval(`String(getSelection())`)).length > 0, "the drag selected text");
        const grip = await page.box("#flyHost .fly-head");
        await page.drag(grip, await backdrop(page), 3);
        assert.equal(await flyOpen(page), true, "a drag from the header closed it");
    });

    test("peek: clicks on the backdrop right after opening are ignored", async ({ page }) => {
        await page.click(`#overview .ov-sec .ov-title[data-kind="pr"][data-pr="104"]`);
        await page.waitFor(`!!document.querySelector('#flyHost .fly')`);
        await page.click(null, { at: await backdrop(page) });
        assert.equal(await flyOpen(page), true, "a click within 350ms of opening closed it");
    });

    test("expand: E, the expand button and dragging the edge all open the sheet; collapse goes back", async ({ page }) => {
        await openRow(page, 104);
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`, 2000, "E to expand");
        await page.click('#wsHead [data-act="collapse"]');
        await page.waitFor(`!document.body.classList.contains('sheet') && !!document.querySelector('#flyHost .fly')`, 2000, "collapse to peek");
        await page.click('#flyHost [data-act="expand"]');
        await page.waitFor(`document.body.classList.contains('sheet')`, 2000, "the button to expand");
        await page.click('#wsHead [data-act="collapse"]');
        await page.waitFor(`!!document.querySelector('#flyHost .fly-grip')`);
        const grip = await page.box("#flyHost .fly-grip");
        await page.drag(grip, { x: 80, y: grip.y }, 10);
        await page.waitFor(`document.body.classList.contains('sheet')`, 2000, "dragging the edge to expand");
    });

    test("expand: the last mode is remembered for the next item", async ({ page }) => {
        await openRow(page, 104);
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await page.key("Escape");
        await page.waitFor(`!document.body.classList.contains('sheet')`);
        await openRow(page, 103);
        assert.equal(await sheetOpen(page), true, "opened as a sheet again");
        await page.click('#wsHead [data-act="collapse"]');
        await page.key("Escape");
        await page.waitFor(`!document.querySelector('#flyHost .fly')`);
        await openRow(page, 104);
        assert.equal(await flyOpen(page), true, "peek is remembered too");
    });

    test("pager: J and K walk the list, and the pager follows", async ({ page }) => {
        await openRow(page, 104);
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`);
        const order = await page.eval(`navList`);
        const at = (await page.eval(`selected`));
        const i = order.indexOf(`pr:${at}`) >= 0 ? order.indexOf(`pr:${at}`) : order.findIndex((k) => k.endsWith(`:${at}`));
        assert.ok(i >= 0 && i < order.length - 1, `#104 is in the list (${order.join(",")})`);
        await page.key("j");
        await page.waitFor(`document.querySelector('#wsHead .pager span')?.innerText.startsWith('${i + 2} of')`, 2000, "J to move on");
        assert.equal(await page.eval("selected"), Number(order[i + 1].split(":")[1]));
        await page.key("k");
        await page.waitFor(`document.querySelector('#wsHead .pager span')?.innerText.startsWith('${i + 1} of')`, 2000, "K to go back");
        assert.equal(await page.eval("selected"), 104);
    });

    test("narrow: the sheet fills the width and Back returns to the list", async ({ page }) => {
        await page.eval(`document.getElementById('overview').scrollTop = 200`);
        await openRow(page, 104);
        const before = await page.eval(`document.getElementById('overview').scrollTop`);
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`);
        assert.equal(await page.eval(`Math.round(document.querySelector('.layout').getBoundingClientRect().width)`), 600);
        await page.click("#wsHead .sheet-bar .back");
        await page.waitFor(`!document.body.classList.contains('sheet')`, 2000, "Back to close the sheet");
        assert.equal(await page.eval(`document.getElementById('overview').scrollTop`), before);
    }, { width: 600 });

    test("regression: peek opens at the top, not at the thread's New line", async ({ page }) => {
        await openRow(page, 102);
        await sleep(300);
        assert.equal(await page.eval(`document.querySelector('#flyHost .fly-body').scrollTop`), 0, "Summary opened scrolled down");
        assert.equal(await page.eval(`(() => { const b = document.querySelector('#flyHost .fly-body'); const r = b.querySelector('.gates')?.getBoundingClientRect(); return !!r && r.top < b.getBoundingClientRect().bottom; })()`), true, "the readiness checklist is in view");
    });

    test("the Thread tab still lands on the New line", async ({ page }) => {
        await page.eval(`localStorage.setItem('prb-open-mode', 'expanded'); localStorage.setItem('prb-tab', 'thread')`);
        await page.eval(`location.reload()`);
        await page.waitFor(`!!document.querySelector('.ov-title')`, 10000);
        await page.click(`#overview .ov-sec .ov-title[data-kind="pr"][data-pr="102"]`);
        await page.waitFor(`document.body.classList.contains('sheet') && !!document.querySelector('#wsScroll .new-div')`, 3000, "the sheet's thread");
        await sleep(400);
        const tabName = await text(page, '#wsHead [aria-selected="true"]');
        assert.match(tabName, /Thread/);
        assert.equal(await page.eval(`(() => { const s = document.getElementById('wsScroll'), d = s.querySelector('.new-div').getBoundingClientRect(), r = s.getBoundingClientRect(); return d.top >= r.top && d.bottom <= r.bottom; })()`), true, "the New line is in view");
    });

    // ---- Composer ------------------------------------------------------------------------------------
    test("composer: Send clears the box, shows Replying…, then the reply arrives unread and reads", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 104);
        await page.click("#flyInput");
        await page.type("What's left?");
        await page.click('#flyComposer button[type="submit"]');
        await page.waitFor(`document.getElementById('flyInput')?.value === ''`, 2000, "the box to clear");
        await page.waitFor(`(() => { const b = document.querySelector('#flyHost .fly-body'); return b.scrollHeight - b.scrollTop - b.clientHeight < 40; })()`, 2000, "the thread to scroll to the bottom");
        assert.match(ctx.host.sends.at(-1)?.prompt || "", /What's left\?/);
        await page.key("Escape");
        await page.waitFor(`/Replying/.test(document.querySelector('#overview .ov-sec .ov-title[data-pr="104"]').closest('.ov-row').innerText)`, 3000, "the row to say Replying…");
        await ctx.board.tool("pr_board_reply", { repo: REPO, pr: 104, text: "Only [[finding:f1]] is left.", from: "PR #104 review" });
        await page.waitFor(`/New reply/.test(document.querySelector('#overview .ov-sec .ov-title[data-pr="104"]').closest('.ov-row').innerText) && !!document.querySelector('#overview .ov-sec .ov-title[data-pr="104"]').closest('.ov-row').querySelector('.unread-dot')`, 4000, "the New reply chip and unread dot");
        await openRow(page, 104);
        await page.key("Escape");
        await page.waitFor(`!document.querySelector('#overview .ov-sec .ov-title[data-pr="104"]').closest('.ov-row').querySelector('.unread-dot')`, 4000, "the reply to be read");
    });

    test("composer: a failed send puts the text back", async (ctx) => {
        const { page } = ctx;
        ctx.host.sendError = new Error("agent offline");
        page.allowStatus.add("POST /api/message 502");
        await openRow(page, 104);
        await page.click("#flyInput");
        await page.type("Please re-check");
        await page.click('#flyComposer button[type="submit"]');
        await page.waitFor(`document.getElementById('flyInput')?.value === 'Please re-check'`, 3000, "the text to come back");
        assert.match(await text(page, "#toast"), /Couldn't send/);
    });

    test("composer: a draft survives live updates, closing, and switching items", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 104);
        await page.click("#flyInput");
        await page.type("half-written thought");
        for (let i = 0; i < 3; i++) await poke(ctx);
        await sleep(600);
        assert.equal(await page.eval(`document.getElementById('flyInput').value`), "half-written thought", "a live update wiped it");
        assert.equal(await page.eval(`document.activeElement?.id`), "flyInput", "a live update took focus away");
        await page.key("Escape");
        await openRow(page, 103);
        assert.equal(await page.eval(`document.getElementById('flyInput').value`), "", "another item shows the draft");
        await page.key("Escape");
        await openRow(page, 104);
        assert.equal(await page.eval(`document.getElementById('flyInput').value`), "half-written thought");
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`);
        assert.equal(await page.eval(`document.getElementById('input').value`), "half-written thought", "the draft didn't follow into the sheet");
    });

    // ---- Readiness -------------------------------------------------------------------------------------
    test("readiness: core gates, recommended pr-review, and the label as a muted hint", async ({ page }) => {
        await openRow(page, 102);
        const gates = await page.eval(`[...document.querySelectorAll('#flyHost .gates li')].map((l) => ({ id: l.dataset.gate || 'label', cls: l.className, text: l.innerText.replace(/\\s+/g, ' ') }))`);
        const g = (id) => gates.find((x) => x.id === id);
        assert.match(g("copilot").cls, /gs-fail/);
        assert.match(g("copilot").text, /Reviewed an older commit/);
        assert.match(g("ci").cls, /gs-fail/);
        assert.match(g("discussion").cls, /gs-pass/);
        assert.match(g("uptodate").cls, /gs-pass/);
        assert.match(g("review").text, /Recommended/);
        assert.match(g("label").cls, /gs-info/);
        assert.match(await text(page, "#flyHost .fly-head"), /2\/4 ready/, "the meter counts only core gates");
    });

    test("readiness: keep-ready makes pr-review a core gate", async ({ page }) => {
        await openRow(page, 101);
        const ids = await page.eval(`[...document.querySelectorAll('#flyHost .gates li[data-gate]')].map((l) => l.dataset.gate)`);
        assert.ok(ids.includes("review"));
        assert.match(await text(page, "#flyHost .fly-head"), /5\/5 ready/);
        await page.key("Escape");
        await openRow(page, 107);
        assert.match(await text(page, "#flyHost .fly-head"), /4\/4 ready/);
    });

    test("readiness: a blocking finding offers Fix and Not needed", async (ctx) => {
        const { page } = ctx;
        await ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 107, review: { headSha: HEAD[107], verdict: "request-changes", risk: "medium", summary: "One real problem.", findings: [{ id: "f9", title: "Export drops the header row", category: "risk", severity: "high", what: "x", fix: "y", files: [{ path: "src/csv.js", line: 3 }] }] } });
        await openRow(page, 107);
        await page.waitFor(`!!document.querySelector('#flyHost .gates [data-act="finding-fix"][data-id="f9"]')`, 4000, "Fix on the finding");
        await page.click('#flyHost .gates [data-act="finding-fix"][data-id="f9"]');
        // Asking for a fix takes it off the blocking list: the item now counts it as being fixed.
        await page.waitFor(`!document.querySelector('#flyHost .gates [data-id="f9"]') && /1 being fixed/.test(document.querySelector('#flyHost .gates li[data-gate="review"]')?.innerText || '')`, 4000, "the finding to show as being fixed");
        assert.equal(state(ctx).prs["107"].triage.f9.fixRequestedAt !== undefined, true);
        assert.match(ctx.host.sends.at(-1)?.prompt || "", /Export drops the header row/);
    });

    test("readiness: Not needed records the decision and clears the gate", async (ctx) => {
        const { page } = ctx;
        await ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 107, review: { headSha: HEAD[107], verdict: "request-changes", risk: "medium", summary: "One real problem.", findings: [{ id: "f9", title: "Export drops the header row", category: "risk", severity: "high", what: "x", fix: "y", files: [{ path: "src/csv.js", line: 3 }] }] } });
        await openRow(page, 107);
        await page.waitFor(`!!document.querySelector('#flyHost .gates [data-act="finding-skip"][data-id="f9"]')`, 4000, "Not needed on the finding");
        await page.click('#flyHost .gates [data-act="finding-skip"][data-id="f9"]');
        await page.waitFor(`!document.querySelector('#flyHost .gates [data-id="f9"]') && document.querySelector('#flyHost .gates li[data-gate="review"]')?.classList.contains('gs-pass')`, 4000, "the pr-review item to pass");
        assert.equal(state(ctx).prs["107"].triage.f9.decision, "ignore");
        assert.equal(ctx.host.sends.length, 0);
    });

    test("readiness: a PR in the merge queue says so, even when it's yours", async (ctx) => {
        const { page, world } = ctx;
        world.queue.push({ position: 2, state: "QUEUED", enqueuedAt: new Date().toISOString(), pullRequest: { number: 107 } });
        await refresh(ctx);
        await page.waitFor(`/merge queue/i.test(document.querySelector('#overview .ov-sec .ov-title[data-pr="107"]').closest('.ov-row').innerText)`, 4000, "#107 to show the queue");
        assert.doesNotMatch(await rowText(page, 107), /merge it/);
        world.queue.pop();
        await refresh(ctx);
    });

    // ---- Review tab and posting --------------------------------------------------------------------
    test("review tab: verdict header and findings; posting preselects the verdict's event", async ({ page }) => {
        await openRow(page, 104);
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await page.click('#wsHead [data-act="tab"][data-tab="review"]');
        await page.waitFor(`!!document.querySelector('#wsScroll #finding-f1')`, 3000, "the findings");
        assert.match(await text(page, "#wsScroll"), /Recommends changes/);
        assert.match(await text(page, "#wsScroll"), /Two refreshes can race/);
        assert.equal(await page.eval(`document.querySelector('#wsScroll [data-act="rv-preview"]').dataset.event`), "request_changes");
    });

    test("review tab: 0 findings offers Approve", async ({ page }) => {
        await page.eval(`localStorage.setItem('prb-open-mode', 'expanded')`);
        await page.eval(`location.reload()`);
        await page.waitFor(`!!document.querySelector('.ov-title')`, 10000);
        await page.click(`#overview .ov-sec .ov-title[data-kind="pr"][data-pr="105"]`);
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await page.click('#wsHead [data-act="tab"][data-tab="review"]');
        await page.waitFor(`!!document.querySelector('#wsScroll [data-act="rv-preview"]')`, 3000, "the post button");
        assert.equal(await page.eval(`document.querySelector('#wsScroll [data-act="rv-preview"]').dataset.event`), "approve");
        assert.match(await text(page, '#wsScroll [data-act="rv-preview"]'), /Approve/);
    });

    test("review tab: the preview shows exactly what will post, and posting goes through the agent", async (ctx) => {
        const { page } = ctx;
        await page.eval(`localStorage.setItem('prb-open-mode', 'expanded')`);
        await page.eval(`location.reload()`);
        await page.waitFor(`!!document.querySelector('.ov-title')`, 10000);
        await page.click(`#overview .ov-sec .ov-title[data-kind="pr"][data-pr="104"]`);
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await page.click('#wsHead [data-act="tab"][data-tab="review"]');
        await page.waitFor(`!!document.querySelector('#wsScroll #finding-f1')`);
        await page.click('#wsScroll [data-act="triage"][data-id="f1"][data-decision="post"]');
        await page.waitFor(`document.querySelector('#wsScroll [data-act="triage"][data-id="f1"][data-decision="post"]')?.getAttribute('aria-pressed') === 'true'`, 3000, "f1 marked Post");
        await page.click('#wsScroll [data-act="rv-preview"]');
        await page.waitFor(`/Two refreshes can race/.test(document.querySelector('#wsScroll .rv-preview, #wsScroll [class*=preview]')?.innerText || '')`, 4000, "the preview");
        assert.equal(ctx.host.sends.length, 0, "previewing doesn't post");
        assert.equal(ctx.world.mutations.length, 0);
    });

    // ---- Branch review (issue with no PR yet) ------------------------------------------------------
    test("changes tab: comment on a line, then send it to the agent", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 203, "issue");
        await page.click('#flyHost [data-act="fly-tab"][data-tab="changes"]');
        await page.waitFor(`!!document.querySelector('#flyHost tr.dl td.ln')`, 4000, "the diff");
        await page.click(`#flyHost tr.dl.a td.ln:nth-child(2)`);
        await page.waitFor(`!!document.querySelector('#flyHost .br-comp textarea')`, 2000, "the comment box");
        await page.click("#flyHost .br-comp textarea");
        await page.type("Handle a corrupt file too");
        await page.key("Enter", { code: "Enter", keyCode: 13, text: "\r" }).catch(() => {});
        await page.click('#flyHost [data-act="br-save"]').catch(() => {});
        await page.waitFor(`/Handle a corrupt file too/.test(document.querySelector('#flyHost .fly-body').innerText) && !document.querySelector('#flyHost .br-comp textarea')`, 4000, "the saved comment");
        assert.match(await text(page, '#flyHost [data-act="br-send"]'), /Send 1 comment/);
        await page.click('#flyHost [data-act="br-send"]');
        await page.waitFor(() => true);
        await sleep(500);
        assert.match(ctx.host.sends.at(-1)?.prompt || "", /Handle a corrupt file too/);
    });

    test("changes tab: a comment on code that's gone shows as Outdated", async (ctx) => {
        const { page } = ctx;
        const st = state(ctx);
        st.issues["203"].branchReview = { comments: [{ id: "old1", file: "src/settings.js", side: "new", start: 2, end: 2, sha: "f".repeat(40), excerpt: "export const load = () => ({ cached: true });", text: "Why cache this?", status: "sent", at: new Date().toISOString() }] };
        fs.writeFileSync(ctx.board.stateFile, JSON.stringify(st));
        await page.goto(ctx.board.url);
        await openRow(page, 203, "issue");
        await page.click('#flyHost [data-act="fly-tab"][data-tab="changes"]');
        await page.waitFor(`/Why cache this\\?/.test(document.querySelector('#flyHost .fly-body').innerText)`, 4000, "the old comment");
        assert.match(await text(page, "#flyHost .fly-body"), /Outdated/);
    });

    // ---- GitHub tab --------------------------------------------------------------------------------
    test("GitHub tab: renders the timeline and never runs markup from GitHub", async ({ page }) => {
        await openRow(page, 102);
        await page.click('#flyHost [data-act="fly-tab"][data-tab="github"]');
        await page.waitFor(`/Why not default to/.test(document.querySelector('#flyHost .fly-body')?.innerText || '')`, 5000, "the GitHub view");
        const body = await text(page, "#flyHost .fly-body");
        assert.match(body, /src\/config\.js/);
        assert.match(body, /build-and-package/);
        assert.match(body, /Looks reasonable/);
        await sleep(300);
        assert.equal(await page.eval(`window.__xss`), undefined, "GitHub markup ran in the board");
        assert.equal(await page.eval(`document.querySelectorAll('#flyHost .fly-body script, #flyHost .fly-body [onerror], #flyHost .fly-body a[href^="javascript:"]').length`), 0);
    });

    test("GitHub tab: a failed or slow GitHub read shows an error, not a broken panel", async (ctx) => {
        const { page, world } = ctx;
        world.fault = { match: (a) => a.some((x) => x.includes("closingIssuesReferences")), delayMs: 800, error: "HTTP 502: Bad Gateway" };
        page.allowStatus.add("GET /api/github 502");
        await openRow(page, 103);
        await page.click('#flyHost [data-act="fly-tab"][data-tab="github"]');
        await page.waitFor(`/Bad Gateway|couldn't|Couldn't/.test(document.querySelector('#flyHost .fly-body')?.innerText || '')`, 6000, "an error message");
        assert.equal(await flyOpen(page), true);
        assert.equal(await exists(page, '#flyHost [data-act="gh-refresh"]'), true, "there's a way to retry");
    });

    // ---- Anchors -----------------------------------------------------------------------------------
    test("anchors: a finding chip opens the Review tab and highlights it", async ({ page }) => {
        await openRow(page, 104);
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await page.click('#wsHead [data-act="tab"][data-tab="thread"]');
        await page.waitFor(`!!document.querySelector('#wsScroll .anchor[data-a="finding:f1"]')`, 3000, "the chip");
        assert.match(await text(page, '#wsScroll .anchor[data-a="finding:f1"]'), /Two refreshes can race/);
        await page.click('#wsScroll .anchor[data-a="finding:f1"]');
        await page.waitFor(`document.querySelector('#finding-f1')?.classList.contains('anchor-hi')`, 3000, "f1 highlighted");
        assert.match(await text(page, '#wsHead [aria-selected="true"]'), /Review/);
    });

    test("anchors: a reply with focus jumps there while the item is open", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 104);
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await ctx.board.tool("pr_board_reply", { repo: REPO, pr: 104, text: "Look at [[finding:f2]].", focus: "finding:f2" });
        await page.waitFor(`document.querySelector('#finding-f2')?.classList.contains('anchor-hi')`, 4000, "f2 highlighted");
    });

    // ---- Robustness ----------------------------------------------------------------------------------
    test("robustness: the board reconnects after the live connection drops", async (ctx) => {
        const { page } = ctx;
        dropLiveConnections(ctx);
        await page.waitFor(`/Reconnecting/.test(document.getElementById('meta').innerText)`, 3000, "Reconnecting…");
        await page.waitFor(`!/Reconnecting/.test(document.getElementById('meta').innerText)`, 8000, "the reconnect");
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, pr: 103, owner: "author", action: "Waiting on alice after the reconnect" });
        await page.waitFor(`/after the reconnect/.test(document.querySelector('#overview .ov-sec .ov-title[data-pr="103"]').closest('.ov-row').innerText)`, 4000, "updates after the reconnect");
    });

    test("robustness: a GitHub error shows a banner and keeps the board usable", async (ctx) => {
        const { page, world } = ctx;
        world.fault = { match: (a) => a.includes("--state") && a.includes("open"), error: "HTTP 502: Bad Gateway" };
        await refresh(ctx);
        await page.waitFor(`!document.getElementById('banner').hidden`, 4000, "the banner");
        assert.match(await text(page, "#banner"), /Bad Gateway/);
        assert.ok((await rows(page)).length >= 8, "the rows stayed");
        await openRow(page, 104);
        await page.key("Escape");
        world.fault = null;
        await refresh(ctx);
        await page.waitFor(`document.getElementById('banner').hidden`, 4000, "the banner to clear");
    });

    test("robustness: live updates don't reset scroll or focus", async (ctx) => {
        const { page } = ctx;
        await page.eval(`document.getElementById('overview').scrollTop = 350`);
        await openRow(page, 104);
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await page.click('#wsHead [data-act="tab"][data-tab="review"]');
        await page.waitFor(`!!document.querySelector('#wsScroll #finding-f2')`);
        await page.eval(`document.getElementById('wsScroll').scrollTop = 80`);
        const scrolled = await page.eval(`document.getElementById('wsScroll').scrollTop`);
        assert.ok(scrolled > 0, "the Review tab is tall enough to scroll");
        await page.click("#input");
        await page.type("typing while it updates");
        for (let i = 0; i < 8; i++) await poke(ctx, i % 2 ? 103 : 106);
        await sleep(800);
        assert.equal(await page.eval(`document.activeElement?.id`), "input", "focus moved");
        assert.equal(await page.eval(`document.getElementById('input').value`), "typing while it updates");
        assert.equal(await page.eval(`document.getElementById('wsScroll').scrollTop`), scrolled, "the sheet scrolled");
        await page.key("Escape");
        await page.eval(`document.activeElement.blur()`);
        await page.key("Escape");
        await page.waitFor(`!document.body.classList.contains('sheet')`);
        assert.equal(await page.eval(`document.getElementById('overview').scrollTop`), 350, "the list scrolled");
    });

    test("safety: the board never writes to GitHub on its own while nothing is kept ready", async (ctx) => {
        await sleep(500);
        assert.equal(ctx.world.mutations.length, 0);
        assert.equal(ctx.host.sends.length, 0, "the board sent the agent a prompt by itself");
    });
}

// Force a re-read from GitHub. The board allows one forced read every 5 seconds, so retry until it happens.
async function refresh(ctx) {
    const act = ctx.board.canvas.actions.find((a) => a.name === "refresh");
    const before = ctx.world.calls;
    for (let i = 0; i < 30; i++) {
        await act.handler({ instanceId: "ui-test", input: {} });
        if (ctx.world.calls > before) return;
        await sleep(250);
    }
    throw new Error("The board never re-read GitHub");
}

// Drop the page's live (SSE) connection on the server side, like a sleep/wake or network blip.
function dropLiveConnections(ctx) {
    const port = Number(new URL(ctx.board.url).port);
    const servers = process._getActiveHandles().filter((h) => h?.constructor?.name === "Server" && h.address?.()?.port === port);
    if (!servers.length) throw new Error("couldn't find the board's server");
    for (const s of servers) s.closeAllConnections();
}
