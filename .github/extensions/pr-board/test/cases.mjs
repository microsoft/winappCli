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
const sheetOpen = (page) => page.eval(`document.body.classList.contains('sheet')`);
const text = (page, sel) => page.eval(`(document.querySelector(${J(sel)})?.innerText || '').replace(/\\s+/g, ' ').trim()`);
const exists = (page, sel) => page.eval(`!!document.querySelector(${J(sel)})`);

async function openRow(page, n, kind = "pr") {
    await page.click(`#overview .ov-sec .ov-title[data-kind="${kind}"][data-pr="${n}"]`);
    await page.waitFor(`document.body.classList.contains('sheet')`, 3000, `#${n} to open`);
    await sleep(400); // the backdrop ignores clicks for 350ms after opening
}
// A board update from the server, like the agent replying somewhere else.
const poke = (ctx, n = 103) => ctx.board.tool("pr_board_set_summary", { repo: REPO, pr: n, summary: `Summary ${Date.now()}`, nextSteps: [], risk: "low" });
const state = (ctx) => JSON.parse(fs.readFileSync(ctx.board.stateFile, "utf8"));
// A point on the dimmed list: the sheet's top margin.
const backdrop = async () => ({ x: 520, y: 12 });
// Close the sheet the way a user does: Esc (from outside the message box, which keeps Esc for itself).
// An open menu takes the first Esc.
async function closeSheet(page) {
    await page.eval(`document.activeElement?.blur()`);
    for (let i = 0; i < 3 && (await sheetOpen(page)); i++) { await page.key("Escape"); await sleep(80); }
    await page.waitFor(`!document.body.classList.contains('sheet')`, 3000, "the sheet to close");
}
async function tabTo(page, id) {
    await page.click(`#wsHead [role="tab"][data-tab="${id}"]`);
    await page.waitFor(`document.querySelector('#wsHead [role="tab"][data-tab="${id}"]')?.getAttribute('aria-selected') === 'true'`, 3000, `the ${id} tab`);
}

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

        test(`the sheet renders cleanly (${theme}, ${width}px)`, async (ctx) => {
            const { page } = ctx;
            await openRow(page, 102);
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

    // ---- Replies from the person we're waiting on ----------------------------------------------------
    const waitingRow = (n, kind) => `document.querySelector('#overview .ov-sec .ov-title[data-kind="${kind}"][data-pr="${n}"]')?.closest('.ov-row')`;

    test("replies: a reporter's comment on an issue we wait on becomes Your move, unread, with a preview", async (ctx) => {
        const { page, world } = ctx;
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, issue: 203, owner: "reporter", action: "Confirm the fix on your machine" });
        await page.waitFor(`/Waiting on others|Confirm the fix/.test(${waitingRow(203, "issue")}?.innerText || '')`, 4000, "#203 to wait on the reporter");
        assert.equal(await page.eval(`!!${waitingRow(203, "issue")}.querySelector('.unread-dot')`), false);
        world.comment(203, "hank", "Works now, thanks! My settings stay after a restart.");
        await refresh(ctx);
        await page.waitFor(`/hank replied: “Works now, thanks!/.test(${waitingRow(203, "issue")}?.innerText || '')`, 4000, "the reply on the row");
        const row = await page.eval(`${waitingRow(203, "issue")}.innerText`);
        assert.match(row, /New reply/);
        assert.equal(await page.eval(`!!${waitingRow(203, "issue")}.querySelector('.unread-dot')`), true, "no unread dot");
        assert.ok((await ymCards(page)).some((c) => c.kind === "issue" && c.n === 203), "it isn't in Your move");
        assert.equal(ctx.host.sends.length, 0, "proactive mode is paused, so nothing is sent");
    }, { changesGitHub: true });

    test("replies: an author's push on a PR waiting on the author asks you to re-review", async (ctx) => {
        const { page, world } = ctx;
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, pr: 103, owner: "author", action: "Fix the failing check" });
        await page.waitFor(`/Fix the failing check/.test(${waitingRow(103, "pr")}?.innerText || '')`, 4000, "#103 to wait on alice");
        world.push(103, "Fix the cache test");
        await refresh(ctx);
        await page.waitFor(`/alice pushed new commits: re-review/.test(${waitingRow(103, "pr")}?.innerText || '')`, 4000, "the re-review on the row");
        assert.equal(await page.eval(`${waitingRow(103, "pr")}.querySelector('.right [data-act="review"]')?.innerText.trim()`), "Re-review");
        assert.equal(ctx.host.sends.length, 0, "a push needs no summary from the agent");
    }, { changesGitHub: true });

    test("replies: comments from bots or from you don't count", async (ctx) => {
        const { page, world } = ctx;
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, issue: 203, owner: "reporter", action: "Confirm the fix on your machine" });
        world.comment(203, "github-actions[bot]", "This issue is stale.");
        world.comment(203, "me", "Could you try the new build?");
        await refresh(ctx);
        await sleep(600);
        const row = await page.eval(`${waitingRow(203, "issue")}.innerText`);
        assert.match(row, /Confirm the fix on your machine/);
        assert.doesNotMatch(row, /replied|New reply/);
        assert.equal(await page.eval(`!!${waitingRow(203, "issue")}.querySelector('.unread-dot')`), false);
        assert.equal(ctx.host.sends.length, 0);
    }, { changesGitHub: true });

    test("replies: a reply on an issue a PR fixes shows on the PR's row", async (ctx) => {
        const { page, world } = ctx;
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, issue: 201, owner: "reporter", action: "Check the draft build" });
        world.comment(201, "frank", "The draft build fixes it for me.");
        await refresh(ctx);
        await page.waitFor(`/frank replied: “The draft build fixes it/.test(${waitingRow(108, "pr")}?.innerText || '')`, 4000, "the reply on PR #108's row");
        await autoOn(ctx);
        await waitForSend(ctx, /Auto: follow up on contoso\/widgets#201 .*because frank replied/);
        assert.match(autoPrompts(ctx).at(-1).prompt, /issue view 201/);
    }, { changesGitHub: true });

    // ---- Proactive mode ----------------------------------------------------------------------------------
    test("auto panel: opens from the header, saves layers and limits, pauses, and closes with Esc", async (ctx) => {
        const { page } = ctx;
        assert.match(await text(page, "#autoBtn"), /Auto/);
        assert.match(await text(page, "#pauseBtn"), /Resume auto/, "the fixture starts paused");
        await page.click("#autoBtn");
        await page.waitFor(`!!document.querySelector('#autoHost .auto-panel')`, 2000, "the panel");
        assert.equal(await page.eval(`document.getElementById('auto-layer-firstReview').checked`), false, "the fixture turned first review off");
        assert.equal(await page.eval(`document.getElementById('auto-layer-rereview').checked`), true);
        await page.click("#auto-layer-firstReview");
        await page.waitFor(() => true);
        await sleep(400);
        assert.equal(state(ctx).meta.auto.settings.layers.firstReview, true, "the layer wasn't saved");
        await page.eval(`(() => { const i = document.getElementById('auto-maxConcurrent'); i.value = '3'; i.dispatchEvent(new Event('change', { bubbles: true })); })()`);
        await sleep(400);
        assert.equal(state(ctx).meta.auto.settings.maxConcurrent, 3);
        await page.click('#autoHost header [data-act="auto-pause"]');
        await page.waitFor(`/Pause auto/.test(document.getElementById('pauseBtn').innerText)`, 3000, "resume from the panel");
        assert.equal(state(ctx).meta.auto.settings.paused, false);
        assert.match(await text(page, "#autoHost .guard"), /never posts, approves, requests changes, merges, labels or comments on GitHub/);
        await ctx.shot(`auto-panel-light`);
        await page.theme("dark");
        await ctx.shot(`auto-panel-dark`);
        await page.key("Escape");
        await page.waitFor(`!document.querySelector('#autoHost .auto-panel')`, 2000, "Esc to close it");
    });

    test("layer 1: new activity on a PR you review shows on the row, and the agent summarizes it once", async (ctx) => {
        const { page, world } = ctx;
        await autoOn(ctx);
        world.comment(104, "bob", "I moved the lock into TokenStore.");
        await refresh(ctx);
        await waitForSend(ctx, /Auto: summarize what changed on contoso\/widgets#104 .*\(layer 1/);
        await page.waitFor(`/New: bob commented “I moved the lock/.test(${waitingRow(104, "pr")}?.innerText || '')`, 4000, "the New line on the row");
        await ctx.board.tool("pr_board_set_what_changed", { repo: REPO, pr: 104, text: "bob moved the refresh lock into **TokenStore**, which fixes [[finding:f1]]." });
        await page.waitFor(`/What changed: bob moved the refresh lock into TokenStore/.test(${waitingRow(104, "pr")}?.innerText || '')`, 4000, "the summary on the row");
        assert.equal(state(ctx).prs["104"].auto.running, null, "the summary didn't finish the run");
        await openRow(page, 104);
        await tabTo(page, "overview");
        assert.match(await text(page, "#wsScroll .wc-box"), /What changed since you last looked.*bob moved the refresh lock/);
        await ctx.shot("what-changed-panel");
        await refresh(ctx);
        await sleep(300);
        assert.equal(autoPrompts(ctx).length, 1, "the same activity was summarized twice");
    }, { changesGitHub: true });

    test("layer 2: a push on a PR you reviewed starts one re-review, which lands as your decision", async (ctx) => {
        const { page, world } = ctx;
        await autoOn(ctx);
        const head = world.push(104, "Take a lock around refresh");
        await refresh(ctx);
        await waitForSend(ctx, new RegExp(`Auto: re-review contoso/widgets#104 .*because bob pushed ${head.slice(0, 7)} \\(layer 2`));
        await page.waitFor(`/Auto: re-review because bob pushed/.test(${waitingRow(104, "pr")}?.innerText || '')`, 4000, "the agent state on the row");
        assert.match(JSON.stringify(state(ctx).prs["104"].history), /Auto · re-review after a push because bob pushed/);
        await refresh(ctx);
        await sleep(300);
        assert.equal(autoPrompts(ctx).filter((s) => /re-review/.test(s.prompt)).length, 1, "the same commit was re-reviewed twice");
        await ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 104, review: { headSha: head, verdict: "approve", risk: "low", summary: "The race is fixed.", findings: [] } });
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, pr: 104, owner: "you", action: "Re-review done: the race is fixed, no new findings → approve?" });
        await page.waitFor(`/Re-review done: the race is fixed/.test([...document.querySelectorAll('#overview .ym-card')].map((c) => c.innerText).join(' '))`, 4000, "the decision in Your move");
        assert.ok(await exists(page, '#overview .ym-card [data-act="ball-post"][data-pr="104"]'), "no Post review… button");
    }, { changesGitHub: true });

    test("layer 2: merging main into a PR you reviewed isn't new work; a merge with other changes is", async (ctx) => {
        const { page, world } = ctx;
        await autoOn(ctx);
        const m = world.mergeMain(104);
        await refresh(ctx);
        await waitForSend(ctx, /\(layer 1/);
        await sleep(400);
        assert.equal(autoPrompts(ctx).filter((s) => /\(layer 2/.test(s.prompt)).length, 0, "a merge of main started a re-review");
        assert.match(autoPrompts(ctx)[0].prompt, /merged the base branch in/);
        assert.match(autoPrompts(ctx)[0].prompt, new RegExp(`merged the base branch in \\(${m.slice(0, 7)}, nothing else changed\\)`));
        await ctx.board.tool("pr_board_set_what_changed", { repo: REPO, pr: 104, text: "bob merged main in; nothing else changed." });
        await page.waitFor(`!/Re-run needed|Needs re-review/.test(${waitingRow(104, "pr")}?.innerText || '')`, 3000);
        const other = world.mergeOther(104);
        await refresh(ctx);
        await waitForSend(ctx, new RegExp(`re-review contoso/widgets#104 .*pushed ${other.slice(0, 7)} \\(layer 2`));
    }, { changesGitHub: true });

    test("layer 3: turned off, nothing; turned on, it drafts a first review of a ready PR you're asked on", async (ctx) => {
        const { world } = ctx;
        world.addPr({ number: 109, title: "Add a retry budget", author: "dana", requested: ["me"] });
        await autoOn(ctx);
        await refresh(ctx);
        await sleep(500);
        assert.equal(autoPrompts(ctx).filter((s) => /#109/.test(s.prompt)).length, 0, "first review ran while off");
        await autoOn(ctx, { layers: { firstReview: true } });
        await waitForSend(ctx, /Auto: first review of contoso\/widgets#109 .*because dana asked you to review it \(layer 3/);
    }, { changesGitHub: true });

    test("layer 3: a new unreviewed PR from a teammate gets a first review without asking; bot PRs wait unless you opt in", async (ctx) => {
        const { world } = ctx;
        world.addPr({ number: 110, title: "Bump lodash", author: "dependabot[bot]" });
        world.addPr({ number: 111, title: "Faster thumbnails on scroll", author: "erin" });
        await autoOn(ctx, { maxConcurrent: 5, layers: { firstReview: true } });
        await refresh(ctx);
        const p = await waitForSend(ctx, /Auto: first review of contoso\/widgets#111 .*because it's ready and nobody here is on it \(layer 3/);
        assert.match(p, /open_pr_session/);
        assert.match(p, /never post, approve/);
        await sleep(500);
        assert.equal(autoPrompts(ctx).filter((s) => /#110\b/.test(s.prompt)).length, 0, "a Dependabot PR got a first review");
        assert.equal(autoPrompts(ctx).filter((s) => /#10[346]\b/.test(s.prompt)).length, 0, "a PR with failing CI, a review or in the queue got a first review");
        await autoOn(ctx, { reviewBots: true });
        await waitForSend(ctx, /Auto: first review of contoso\/widgets#110 /);
        assert.equal(ctx.world.mutations.length, 0);
    }, { changesGitHub: true });

    test("layer 5: your PRs get Get it ready & keep it ready on their own, show it, skip drafts, and stay off once you turn it off", async (ctx) => {
        const { page } = ctx;
        await page.eval(`post("/api/action", { pr: 101, action: "monitor", enable: false })`); // you turned it off for #101 earlier
        ctx.world.addPr({ number: 112, title: "Try a new cache layout", author: "me" });
        ctx.world.prs.at(-1).isDraft = true; // a draft you opened to try something
        const before = ctx.host.sends.length;
        await autoOn(ctx, { maxConcurrent: 5, layers: { keepReady: true } });
        await refresh(ctx);
        const p = await waitForSend(ctx, /Auto: keep contoso\/widgets#107 .*ready for review because it's your PR \(layer 5/);
        assert.match(p, /get contoso\/widgets#107 "Add CSV export" ready for human review and keep it ready/);
        assert.match(p, /merge_pr \*\*off\*\*/);
        assert.match(p, /Never merge, approve or post a review/);
        await sleep(500);
        const keeps = ctx.host.sends.slice(before).filter((s) => /\(layer 5/.test(s.prompt)).map((s) => s.prompt.match(/#(\d+)/)[1]);
        assert.ok(!keeps.includes("101"), "#101 was turned on again after you turned it off");
        assert.ok(!keeps.includes("108") && !keeps.includes("112"), "a draft got keep-ready");
        await openRow(page, 107);
        assert.match(await text(page, "#wsHead .ws-meta"), /Turning on….*On automatically.*Turn off/);
        await ctx.shot("keep-ready-auto");
        await page.click('#wsHead .auto-keep [data-act="monitor"]');
        await page.waitFor(`/Turning off/.test(document.querySelector('#wsHead .ws-meta').innerText)`, 4000, "Turning off");
        assert.match(ctx.host.sends.at(-1).prompt, /turn (monitoring|Agent merge) off|off/i);
        assert.ok(state(ctx).prs["107"].keepReadyOff, "turning it off wasn't recorded");
        await ctx.board.tool("pr_board_reply", { repo: REPO, pr: 107, text: "Stopped." });
        await refresh(ctx);
        await sleep(500);
        assert.equal(ctx.host.sends.slice(before).filter((s) => /keep contoso\/widgets#107/.test(s.prompt)).length, 1, "it turned keep-ready on again");
        assert.equal(ctx.world.mutations.length, 0);
    }, { changesGitHub: true });

    test("settings saved before v2 get first review and keep-ready turned on, and keep everything else", async (ctx) => {
        const { page } = ctx;
        const st = state(ctx);
        st.meta.auto.settings = { paused: true, layers: { notice: true, rereview: false, firstReview: false, issues: true }, maxConcurrent: 3, dailyCap: 7, cooldownMin: 30, stableMin: 5, machineCheck: true, cpuMax: 70, minFreeGB: 4, hoursFrom: null, hoursTo: null };
        fs.writeFileSync(ctx.board.stateFile, JSON.stringify(st));
        await ctx.board.api("/api/settings", { settings: {} });
        const s = state(ctx).meta.auto.settings;
        assert.deepEqual([s.layers.firstReview, s.layers.keepReady, s.layers.rereview, s.maxConcurrent, s.dailyCap, s.paused, s.v], [true, true, false, 3, 7, true, 2]);
        await page.click("#autoBtn");
        await page.waitFor(`!!document.querySelector('#autoHost .auto-panel')`, 2000, "the panel");
        assert.equal(await page.eval(`document.getElementById('auto-layer-keepReady').checked`), true);
        await page.click("#auto-layer-keepReady");
        await sleep(400);
        assert.equal(state(ctx).meta.auto.settings.layers.keepReady, false, "turning layer 5 off didn't stick");
        await ctx.board.api("/api/settings", { settings: {} });
        assert.equal(state(ctx).meta.auto.settings.layers.keepReady, false, "the migration ran again");
    });

    test("layer 4: a reporter's reply starts a follow-up that reproduces and validates, with no separate summary", async (ctx) => {
        const { world } = ctx;
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, issue: 203, owner: "reporter", action: "Confirm the fix on your machine" });
        await autoOn(ctx);
        world.comment(203, "hank", "Still broken on 0.8.1 after a restart.");
        await refresh(ctx);
        const p = await waitForSend(ctx, /Auto: follow up on contoso\/widgets#203 .*because hank replied \(layer 4/);
        assert.match(p, /reproduce or validate/);
        assert.match(p, /Reproduced", "Not reproduced" or "Fixed by #n"/);
        assert.equal(autoPrompts(ctx).filter((s) => /#203/.test(s.prompt)).length, 1, "the issue also got a separate summary run");
    }, { changesGitHub: true });

    test("capacity: busy sessions count, so with 2 slots and 1 busy session only 1 of 2 re-reviews starts", async (ctx) => {
        const { world } = ctx;
        await autoOn(ctx, { maxConcurrent: 2 });
        world.push(104);
        world.push(105);
        await refresh(ctx);
        await waitForSend(ctx, /\(layer 2/);
        await sleep(500);
        assert.equal(autoPrompts(ctx).length, 1, "both started although #102's session is busy");
        const q = (await ctx.board.tool("pr_board_get_queue", { repo: REPO })).queue;
        assert.match(q.find((x) => x.layer === "rereview")?.why || "", /waiting for a free slot \(2 of 2 busy\)/);
        const first = autoPrompts(ctx)[0].prompt.match(/#(10[45])/)[1];
        await ctx.board.tool("pr_board_reply", { repo: REPO, pr: Number(first), text: "Re-review done." });
        await refresh(ctx);
        await waitForSend(ctx, new RegExp(`re-review contoso/widgets#${first === "104" ? 105 : 104}`));
    }, { changesGitHub: true });

    test("limits: low memory, the daily cap, and active hours each hold a run back", async (ctx) => {
        const { world } = ctx;
        await autoOn(ctx, { machineCheck: true, cpuMax: 100, minFreeGB: 100000 });
        world.push(104);
        await refresh(ctx);
        await sleep(400);
        const why = async () => (await ctx.board.tool("pr_board_get_queue", { repo: REPO })).queue.find((x) => x.number === 104)?.why || "";
        assert.equal(autoPrompts(ctx).length, 0);
        assert.match(await why(), /free memory is low/);
        await autoOn(ctx, { minFreeGB: 0, dailyCap: 0 });
        await sleep(300);
        assert.match(await why(), /daily cap \(0\) is reached/);
        const h = new Date().getHours();
        await autoOn(ctx, { dailyCap: 10, hoursFrom: (h + 2) % 24, hoursTo: (h + 3) % 24 });
        await sleep(300);
        assert.match(await why(), /outside active hours/);
        assert.equal(autoPrompts(ctx).length, 0);
        await autoOn(ctx, { hoursFrom: null, hoursTo: null });
        await waitForSend(ctx, /re-review contoso\/widgets#104/);
    }, { changesGitHub: true });

    test("pause: nothing starts on its own; Run now still works; Skip drops a run for good", async (ctx) => {
        const { page, world } = ctx;
        await autoOn(ctx, { paused: true });
        world.push(104);
        world.push(105);
        await refresh(ctx);
        await sleep(500);
        assert.equal(autoPrompts(ctx).length, 0, "something started while paused");
        await page.click("#autoBtn");
        await page.waitFor(`document.querySelectorAll('#autoHost .q [data-act="auto-run"]').length === 2`, 3000, "two queued runs");
        assert.match(await text(page, "#autoHost"), /proactive mode is paused/);
        await ctx.shot("auto-queue-paused");
        const keys = await page.eval(`[...document.querySelectorAll('#autoHost [data-act="auto-run"]')].map((b) => b.dataset.key)`);
        await page.click(`#autoHost [data-act="auto-run"][data-key="${keys[0]}"]`);
        await waitForSend(ctx, /Auto: re-review/);
        assert.match(JSON.stringify(state(ctx).meta.auto.log.at(-1)), /"by":"user"/);
        await page.waitFor(`!!document.querySelector('#autoHost [data-act="auto-skip"][data-key="${keys[1]}"]')`, 3000);
        await page.click(`#autoHost [data-act="auto-skip"][data-key="${keys[1]}"]`);
        await sleep(500);
        await autoOn(ctx);
        await refresh(ctx);
        await sleep(500);
        assert.equal(autoPrompts(ctx).length, 1, "the skipped run started after resuming");
    }, { changesGitHub: true });

    test("pr_board_tick hands the prompts back instead of sending them; pr_board_get_queue starts nothing", async (ctx) => {
        const { world } = ctx;
        await autoOn(ctx);
        world.push(104);
        const q = await ctx.board.tool("pr_board_get_queue", { repo: REPO });
        assert.equal(ctx.host.sends.length, 0);
        const dry = await ctx.board.tool("pr_board_tick", { repo: REPO, dryRun: true });
        assert.equal(dry.started.length, 0);
        assert.equal(dry.wouldStart.length, 1, `dry run: ${JSON.stringify(dry.wouldStart)} (queue before: ${q.queue.length})`);
        const t = await ctx.board.tool("pr_board_tick", { repo: REPO });
        assert.equal(t.started.length, 1);
        assert.match(t.started[0].prompt, /Auto: re-review contoso\/widgets#104/);
        assert.equal(ctx.host.sends.length, 0, "the tick also sent the prompt");
        const again = await ctx.board.tool("pr_board_tick", { repo: REPO });
        assert.equal(again.started.length, 0, "the same run started twice");
    }, { changesGitHub: true });

    test("guardrails: every auto prompt forbids GitHub writes, and nothing is written", async (ctx) => {
        const { world } = ctx;
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, issue: 203, owner: "reporter", action: "Confirm" });
        await autoOn(ctx, { maxConcurrent: 5, layers: { firstReview: true } });
        world.addPr({ number: 109, title: "Add a retry budget", author: "dana", requested: ["me"] });
        world.push(104);
        world.comment(203, "hank", "Fixed for me.");
        world.comment(107, "zed", "Nice, one question on the CSV header.");
        await refresh(ctx);
        await waitForSend(ctx, /layer 1/);
        await sleep(500);
        const ps = autoPrompts(ctx);
        assert.deepEqual([...new Set(ps.map((s) => s.prompt.match(/\(layer (\d)/)[1]))].sort(), ["1", "2", "3", "4"]);
        for (const s of ps) assert.match(s.prompt, /never post, approve, request changes, merge, label or comment on GitHub, and never push to someone else's branch/);
        assert.equal(world.mutations.length, 0);
    }, { changesGitHub: true });

    // ---- Peek and expand -------------------------------------------------------------------------------
    test("sheet: clicking a row opens it over the list; Esc closes it and keeps scroll and focus", async ({ page }) => {
        await page.eval(`document.getElementById('overview').scrollTop = 300`);
        await sleep(100);
        await openRow(page, 104);
        const before = await page.eval(`document.getElementById('overview').scrollTop`);
        assert.equal(await sheetOpen(page), true);
        assert.match(await text(page, "#wsHead .pager"), /^‹ prev \d+ of \d+ in the list next ›$/);
        await closeSheet(page);
        assert.equal(await page.eval(`document.getElementById('overview').scrollTop`), before);
        assert.equal(await page.eval(`document.activeElement?.dataset?.pr`), "104");
    });

    test("sheet: a real click on the dimmed margin closes it", async ({ page }) => {
        await openRow(page, 104);
        await page.click(null, { at: await backdrop(page) });
        await page.waitFor(`!document.body.classList.contains('sheet')`, 2000, "the sheet to close");
    });

    test("sheet: a drag or text selection that ends on the margin doesn't close it", async ({ page }) => {
        await openRow(page, 102);
        await tabTo(page, "overview");
        const inside = await page.box("#wsScroll .story-step p");
        await page.drag({ x: inside.x - 60, y: inside.y }, await backdrop(page));
        await sleep(200);
        assert.equal(await sheetOpen(page), true, "a drag from inside closed it");
        assert.ok((await page.eval(`String(getSelection())`)).length > 0, "the drag selected text");
        const head = await page.box("#wsHead h2");
        await page.drag(head, await backdrop(page), 3);
        assert.equal(await sheetOpen(page), true, "a drag from the header closed it");
    });

    test("sheet: clicks on the margin right after opening are ignored", async ({ page }) => {
        await page.click(`#overview .ov-sec .ov-title[data-kind="pr"][data-pr="104"]`);
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await page.click(null, { at: await backdrop(page) });
        assert.equal(await sheetOpen(page), true, "a click within 350ms of opening closed it");
    });

    test("sheet: there's one view: no peek panel, no Expand or Collapse, and E does nothing", async ({ page }) => {
        await openRow(page, 104);
        assert.equal(await exists(page, "#flyHost, .fly, [data-act='expand'], [data-act='collapse'], .fly-grip"), false, "a peek control is left");
        await page.key("e");
        await sleep(200);
        assert.equal(await sheetOpen(page), true);
        await closeSheet(page);
        await page.click('#overview .ov-sec .ov-acts [data-act="open-item"][data-pr="103"]');
        await page.waitFor(`document.body.classList.contains('sheet')`, 2000, "⋯ to open the sheet too");
    });


    test("pager: J and K walk the list, and the pager follows", async ({ page }) => {
        await openRow(page, 104);
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
        const row = await page.box(`#overview .ov-sec .ov-title[data-kind="pr"][data-pr="104"]`);
        const before = await page.eval(`document.getElementById('overview').scrollTop`);
        await page.click(null, { at: row });
        await page.waitFor(`document.body.classList.contains('sheet')`);
        assert.equal(await page.eval(`Math.round(document.querySelector('.layout').getBoundingClientRect().width)`), 600);
        await page.click("#wsHead .sheet-bar .back");
        await page.waitFor(`!document.body.classList.contains('sheet')`, 2000, "Back to close the sheet");
        assert.equal(await page.eval(`document.getElementById('overview').scrollTop`), before);
    }, { width: 600 });

    test("regression: the sheet opens at the top of its landing tab", async ({ page }) => {
        await openRow(page, 107);
        await sleep(300);
        assert.match(await text(page, '#wsHead [aria-selected="true"]'), /Story/);
        assert.equal(await page.eval(`document.getElementById('wsScroll').scrollTop`), 0, "Story opened scrolled down");
        assert.equal(await page.eval(`(() => { const b = document.getElementById('wsScroll'); const r = b.querySelector('.gates')?.getBoundingClientRect(); return !!r; })()`), true, "no readiness checklist");
    });

    test("the thread column lands on the New line while the tabs stay at the top", async ({ page }) => {
        await openRow(page, 102);
        await page.waitFor(`!!document.querySelector('#tScroll .new-div')`, 3000, "the thread column");
        await sleep(400);
        assert.match(await text(page, '#wsHead [aria-selected="true"]'), /Story/);
        assert.equal(await page.eval(`document.getElementById('wsScroll').scrollTop`), 0, "Story opened scrolled down");
        assert.equal(await page.eval(`(() => { const s = document.getElementById('tScroll'), d = s.querySelector('.new-div').getBoundingClientRect(), r = s.getBoundingClientRect(); return d.top >= r.top && d.bottom <= r.bottom; })()`), true, "the New line is in view");
    });

    // ---- The thread column -----------------------------------------------------------------------------
    const tcolBox = (page) => page.eval(`(() => { const r = document.getElementById('tCol').getBoundingClientRect(), m = document.getElementById('wsMain').getBoundingClientRect(); return { w: Math.round(r.width), h: Math.round(r.height), left: Math.round(r.left), top: Math.round(r.top), mainRight: Math.round(m.right), mainBottom: Math.round(m.bottom) }; })()`);
    const visible = (page, sel) => page.eval(`(() => { const e = document.querySelector(${J(sel)}); if (!e || !e.offsetParent) return false; const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; })()`);

    test("thread column: wide sheets show tabs and the thread side by side; the divider resizes it and is remembered", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 104);
        let b = await tcolBox(page);
        assert.ok(Math.abs(b.w - 380) <= 6, `default width ${b.w}`);
        assert.ok(b.left >= b.mainRight - 1, "the thread is to the right of the tabs");
        assert.equal(await exists(page, '#wsHead [data-tab="thread"]'), false, "there's still a Thread tab");
        assert.equal(await visible(page, "#tScroll"), true, "the messages");
        assert.equal(await visible(page, "#input"), true, "the composer");
        assert.match(await text(page, "#tCol"), /Thread/);
        const div = await page.box("#tDiv");
        await page.drag(div, { x: div.x - 120, y: div.y });
        b = await tcolBox(page);
        assert.ok(Math.abs(b.w - 500) <= 8, `dragged width ${b.w}`);
        await page.drag(await page.box("#tDiv"), { x: 200, y: div.y });
        b = await tcolBox(page);
        const half = await page.eval(`Math.round(document.querySelector('.ws').clientWidth / 2)`);
        assert.ok(b.w <= half + 2, `wider than half: ${b.w} > ${half}`);
        await page.drag(await page.box("#tDiv"), { x: div.x - 120, y: div.y });
        await closeSheet(page);
        await page.goto(ctx.board.url);
        await openRow(page, 103);
        b = await tcolBox(page);
        assert.ok(Math.abs(b.w - 500) <= 8, `remembered width ${b.w}`);
        await ctx.shot("thread-wide");
    }, { width: 1300 });

    test("thread column: medium sheets collapse it to a rail with an unread count, and remember that", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 103);
        assert.equal(await visible(page, "#tScroll"), true, "open by default");
        await page.click('#tHead [data-act="tcol-toggle"][aria-expanded="true"]');
        await page.waitFor(`document.body.classList.contains('tc-collapsed')`, 2000, "the rail");
        let b = await tcolBox(page);
        assert.ok(b.w <= 60, `rail width ${b.w}`);
        assert.equal(await visible(page, "#tScroll"), false, "messages hidden");
        assert.equal(await page.eval(`document.getElementById('wsActs').parentElement.id`), "wsMain", "the move bar moves under the tabs");
        await closeSheet(page);
        // #102 is unread; with the thread on a rail, opening it doesn't read it.
        await openRow(page, 102);
        assert.equal(await visible(page, '#tHead .tcol-rail .unread-dot'), true, "the rail shows the unread dot");
        assert.match(await text(page, "#tHead .tcol-rail"), /\d/, "the rail shows the message count");
        await ctx.shot("thread-rail");
        await closeSheet(page);
        assert.equal((await rows(page)).find((r) => r.n === 102 && r.kind === "pr").unread, true, "#102 was marked read while the thread was on a rail");
        await page.goto(ctx.board.url);
        await openRow(page, 102);
        assert.equal(await page.eval(`document.body.classList.contains('tc-collapsed')`), true, "the rail wasn't remembered");
        await page.click('#tHead .tcol-rail');
        await page.waitFor(`!document.body.classList.contains('tc-collapsed')`, 2000, "the column");
        assert.equal(await visible(page, "#tScroll"), true, "messages back");
        await closeSheet(page);
        await page.waitFor(`!document.querySelector('#overview .ov-sec .ov-title[data-pr="102"]').closest('.ov-row').querySelector('.unread-dot')`, 4000, "#102 to be read once the thread showed");
    }, { width: 950 });

    test("thread column: narrow sheets stack the tabs over a thread drawer with the composer always visible", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 104);
        const b = await tcolBox(page);
        assert.ok(b.top >= b.mainBottom - 1, "the thread is under the tabs: " + JSON.stringify(b) + " " + await page.eval(`document.body.className`));
        assert.equal(await visible(page, "#input"), true, "the composer");
        assert.equal(await visible(page, "#tScroll"), false, "the drawer starts closed");
        await page.click('#tHead [data-act="tcol-toggle"]');
        await page.waitFor(`document.body.classList.contains('tc-open')`, 2000, "the drawer");
        assert.equal(await visible(page, "#tScroll"), true, "the messages");
        assert.equal(await page.eval(`document.documentElement.scrollWidth <= innerWidth + 1`), true, "the page scrolls sideways");
        await ctx.shot("thread-narrow");
    }, { width: 600 });

    test("thread column: an anchor chip switches the tab on the left while the thread stays put", async ({ page }) => {
        await openRow(page, 104);
        await page.waitFor(`!!document.querySelector('#tScroll .anchor[data-a="finding:f1"]')`, 3000, "the chip");
        await page.eval(`document.getElementById('tScroll').scrollTop = 0`);
        const before = await page.eval(`document.getElementById('tScroll').scrollTop`);
        await page.click('#tScroll .anchor[data-a="finding:f1"]');
        await page.waitFor(`document.querySelector('#wsScroll #finding-f1')?.classList.contains('anchor-hi')`, 3000, "f1 highlighted");
        assert.match(await text(page, '#wsHead [aria-selected="true"]'), /Review/);
        assert.equal(await visible(page, '#tScroll .anchor[data-a="finding:f1"]'), true, "the thread went away");
        assert.equal(await page.eval(`document.getElementById('tScroll').scrollTop`), before, "the thread scrolled");
    }, { width: 1300 });

    test("thread column: a draft in the composer survives tab switches, the rail and closing the sheet", async ({ page }) => {
        await openRow(page, 105);
        await page.click("#input");
        await page.type("half-written thought");
        await tabTo(page, "changes");
        await tabTo(page, "github");
        assert.equal(await page.eval(`document.getElementById('input').value`), "half-written thought");
        await page.click('#tHead [data-act="tcol-toggle"][aria-expanded="true"]');
        await page.click('#tHead .tcol-rail');
        assert.equal(await page.eval(`document.getElementById('input').value`), "half-written thought");
        await closeSheet(page);
        await openRow(page, 105);
        assert.equal(await page.eval(`document.getElementById('input').value`), "half-written thought");
    }, { width: 950 });

    // ---- Parity: everything the peek panel had is in the sheet ------------------------------------------
    test("parity: your PR's sheet has the move, quick commands, ⋯ actions, Story sections, GitHub, and the thread with its composer", async ({ page }) => {
        await openRow(page, 107);
        assert.match(await text(page, "#wsHead .ball-line"), /Approved: merge it/, "the ball line");
        assert.match(await text(page, "#wsActs"), /Merge….*Done.*Snooze/, "the pinned move with Done and Snooze");
        const cmds = await text(page, "#wsHead .quick-cmds");
        for (const c of ["Status update", "Ask a question", "Turn on monitoring"]) assert.match(cmds, new RegExp(c), `quick command ${c}`);
        assert.match(await text(page, "#wsHead .actions"), /Summarize/);
        await page.click('#wsHead [data-act="more-menu"]');
        const menu = await text(page, "#wsHead .menu");
        for (const c of ["Get it ready & keep it ready", "Remove from board", "Pin to top", "Open on GitHub"]) assert.ok(menu.includes(c), `⋯ has ${c}: ${menu}`);
        await page.key("Escape"); // the open menu covers the tabs
        await tabTo(page, "overview");
        const story = await text(page, "#wsScroll");
        for (const c of ["Explain like I'm 5", "Where it is", "Required CI", "WHAT'S NEEDED FROM YOU"]) assert.ok(story.toUpperCase().includes(c.toUpperCase()), `Story has ${c}`);
        const tabs = await page.eval(`[...document.querySelectorAll('#wsHead [role=tab]')].map((t) => t.innerText.replace(/\\s+\\d+$/, '').trim())`);
        assert.deepEqual(tabs, ["Story", "Architecture", "Code", "Review", "GitHub"]);
        assert.equal(await exists(page, "#tCol #composer #input"), true, "the composer in the thread column");
    });

    test("parity: Watching, the pr-review chip, Fixes Issue, and Stop keeping it ready are in the sheet", async ({ page }) => {
        await openRow(page, 101);
        const head = await text(page, "#wsHead");
        assert.match(head, /Watching/);
        assert.match(head, /pr-review: Ran \(self-check\)/);
        await page.click('#wsHead [data-act="more-menu"]');
        assert.match(await text(page, "#wsHead .menu"), /Stop keeping it ready/);
        await closeSheet(page);
        await openRow(page, 108);
        assert.match(await text(page, "#wsHead"), /Fixes Issue #201/);
    });

    test("parity: a PR you review has its review commands, and What changed since my review shows the commits", async ({ page }) => {
        await openRow(page, 104);
        const cmds = await text(page, "#wsHead .quick-cmds");
        for (const c of ["What changed since my review?", "Summarize the author's replies", "Ask a question"]) assert.ok(cmds.includes(c), `quick command ${c}: ${cmds}`);
        assert.match(await text(page, "#wsHead .actions"), /Re-review/);
        await page.click('#wsHead .quick-cmds [data-act="what-changed"]');
        await page.waitFor(`/What changed since your review/.test(document.getElementById('wsScroll').innerText) && /Address review/.test(document.getElementById('wsScroll').innerText)`, 4000, "the commits since the review");
        assert.match(await text(page, '#wsHead [aria-selected="true"]'), /Story/);
    });

    test("parity: someone else's PR offers Track under Reviewing; an unread item offers Mark as read", async ({ page }) => {
        await openRow(page, 103);
        assert.match(await text(page, "#wsHead .quick-cmds"), /Quick question/);
        await page.click('#wsHead [data-act="more-menu"]');
        assert.match(await text(page, "#wsHead .menu"), /Track under Reviewing/);
        // With the thread on a rail, opening #102 doesn't read it.
        await page.key("Escape");
        await page.click('#tHead [data-act="tcol-toggle"][aria-expanded="true"]');
        await closeSheet(page);
        await page.eval(`expandItem("pr", 102, "github")`);
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await page.click('#wsHead [data-act="more-menu"]');
        assert.match(await text(page, "#wsHead .menu"), /Mark as read/);
        await page.click('#wsHead .menu [data-act="mark-read"]');
        await page.waitFor(`!document.querySelector('#overview .ov-sec .ov-title[data-pr="102"]').closest('.ov-row').querySelector('.unread-dot')`, 4000, "#102 to be read");
    });

    test("parity: an issue's sheet has Summary (explainer, where it is), quick commands, its session and PR chips, and ⋯", async ({ page }) => {
        await openRow(page, 203, "issue");
        const tabs = await page.eval(`[...document.querySelectorAll('#wsHead [role=tab]')].map((t) => t.innerText.replace(/\\s+\\d+$/, '').trim())`);
        assert.deepEqual(tabs, ["Summary", "Code", "GitHub"]);
        await tabTo(page, "overview");
        const sum = await text(page, "#wsScroll");
        assert.match(sum, /Explain like I'm 5/);
        assert.match(sum, /Where it is/);
        assert.match(await text(page, "#wsHead .quick-cmds"), /Status update.*Ask a question/);
        assert.match(await text(page, "#wsHead .ws-meta"), /No PR/);
        assert.match(await text(page, "#wsHead .ws-meta"), /Idle|Linked|Working/);
        await page.click('#wsHead [data-act="more-menu"]');
        assert.match(await text(page, "#wsHead .menu"), /Remove from board/);
    });

    test("parity: row shortcuts run the same command in the sheet", async (ctx) => {
        const { page } = ctx;
        const row = await page.box('#overview .ov-sec .ov-title[data-kind="pr"][data-pr="101"]');
        await page.mouse("mouseMoved", row.x, row.y, 0); // the shortcuts show on hover
        await page.click('#overview .ov-sec .ov-acts [data-act="row-status-update"][data-pr="101"]');
        await page.waitFor(`document.body.classList.contains('sheet')`, 2000, "the sheet");
        await sleep(400);
        assert.match(ctx.host.sends.at(-1)?.prompt || "", /status update/i);
    });

    // ---- Code tab --------------------------------------------------------------------------------------
    test("code tab: a PR shows its diff, read-only", async ({ page }) => {
        await openRow(page, 104);
        await tabTo(page, "changes");
        await page.waitFor(`/const c = 4;/.test(document.getElementById('wsScroll').innerText)`, 4000, "the diff");
        const code = await text(page, "#wsScroll .code-diff");
        assert.match(code, /src\/main\.js/);
        assert.match(code, /No diff to show/, "the binary file");
        await page.click("#wsScroll .code-diff tr.dl.a td.ln");
        await sleep(200);
        assert.equal(await exists(page, "#wsScroll .br-comp"), false, "a PR diff line opened a private comment box");
    });

    test("code tab: an issue with a branch shows the branch against main", async ({ page }) => {
        await openRow(page, 203, "issue");
        await tabTo(page, "changes");
        await page.waitFor(`/fix-203-settings/.test(document.getElementById('wsScroll').innerText) && !!document.querySelector('#wsScroll tr.dl')`, 4000, "the branch compare");
        assert.match(await text(page, "#wsScroll"), /2 commits ahead, 1 behind/);
    });

    test("code tab: an issue with no branch shows the code it mentions, with links to GitHub", async ({ page }) => {
        await openRow(page, 202, "issue");
        await tabTo(page, "changes");
        await page.waitFor(`!!document.querySelector('#wsScroll .code-ref')`, 4000, "the code references");
        const refs = await page.eval(`[...document.querySelectorAll('#wsScroll .code-ref')].map((d) => ({ path: d.querySelector('.df-p').innerText, text: d.innerText, hits: d.querySelectorAll('tr.hit').length, link: d.querySelector('summary a')?.href }))`);
        const r = (p) => refs.find((x) => x.path.startsWith(p));
        assert.equal(r("src/icons/render.js").path, "src/icons/render.js:3–4");
        assert.equal(r("src/icons/render.js").hits, 2, "lines 3-4 are highlighted");
        assert.match(r("src/icons/render.js").text, /devicePixelRatio/);
        assert.match(r("src/icons/render.js").link, /blob\/main\/src\/icons\/render\.js#L3-L4$/);
        assert.equal(r("src/theme/dark.css").path, "src/theme/dark.css:2", "the GitHub link in the issue");
        assert.match(r("docs/old/notes.md").text, /Not found on main/);
        assert.match(await page.eval(`document.querySelector('#wsScroll .code-empty a').href`), /github\.com\/search\?q=repo%3Acontoso%2Fwidgets/);
    });

    test("code tab: with nothing mentioned, it links to a code search", async (ctx) => {
        const { page, world } = ctx;
        world.issues[202].body = "Icons look odd sometimes.";
        world.issues[202].updated = 0;
        await refresh(ctx);
        await openRow(page, 202, "issue");
        await tabTo(page, "changes");
        await page.waitFor(`/doesn't mention any files/.test(document.getElementById('wsScroll').innerText)`, 4000, "the empty state");
        assert.match(await page.eval(`document.querySelector('#wsScroll .code-empty a').href`), /type=code/);
        assert.match(await text(page, "#wsScroll .code-empty a"), /Dark mode icons blurry|Dark icons blurry|icons blurry/i);
    }, { changesGitHub: true });

    test("code tab: when a session gets a branch for the issue, it switches to the branch", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 202, "issue");
        await tabTo(page, "changes");
        await page.waitFor(`!!document.querySelector('#wsScroll .code-ref')`, 4000, "the code references");
        await ctx.board.tool("pr_board_sync_sessions", { repo: REPO, sessions: [{ issue: 202, sessionId: "s-202", name: "Blurry icons", branch: "fix-203-settings", activity: "idle" }] });
        await refresh(ctx);
        await page.waitFor(`!document.querySelector('#wsScroll .code-ref') && /fix-203-settings/.test(document.getElementById('wsScroll').innerText)`, 5000, "the branch compare");
    });

    // ---- Composer ------------------------------------------------------------------------------------
    test("composer: Send clears the box, shows Replying…, then the reply arrives unread and reads", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 104);
        await page.click("#input");
        await page.type("What's left?");
        await page.click('#send');
        await page.waitFor(`document.getElementById('input')?.value === ''`, 2000, "the box to clear");
        await page.waitFor(`(() => { const b = document.getElementById('tScroll'); return b.scrollHeight - b.scrollTop - b.clientHeight < 40; })()`, 2000, "the thread to scroll to the bottom");
        assert.match(ctx.host.sends.at(-1)?.prompt || "", /What's left\?/);
        await closeSheet(page);
        await page.waitFor(`/Replying/.test(document.querySelector('#overview .ov-sec .ov-title[data-pr="104"]').closest('.ov-row').innerText)`, 3000, "the row to say Replying…");
        await ctx.board.tool("pr_board_reply", { repo: REPO, pr: 104, text: "Only [[finding:f1]] is left.", from: "PR #104 review" });
        await page.waitFor(`/New reply/.test(document.querySelector('#overview .ov-sec .ov-title[data-pr="104"]').closest('.ov-row').innerText) && !!document.querySelector('#overview .ov-sec .ov-title[data-pr="104"]').closest('.ov-row').querySelector('.unread-dot')`, 4000, "the New reply chip and unread dot");
        await openRow(page, 104);
        await closeSheet(page);
        await page.waitFor(`!document.querySelector('#overview .ov-sec .ov-title[data-pr="104"]').closest('.ov-row').querySelector('.unread-dot')`, 4000, "the reply to be read");
    });

    test("composer: a failed send puts the text back", async (ctx) => {
        const { page } = ctx;
        ctx.host.sendError = new Error("agent offline");
        page.allowStatus.add("POST /api/message 502");
        await openRow(page, 104);
        await page.click("#input");
        await page.type("Please re-check");
        await page.click('#send');
        await page.waitFor(`document.getElementById('input')?.value === 'Please re-check'`, 3000, "the text to come back");
        assert.match(await text(page, "#toast"), /Couldn't send/);
    });

    test("composer: a draft survives live updates, closing, and switching items", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 104);
        await page.click("#input");
        await page.type("half-written thought");
        for (let i = 0; i < 3; i++) await poke(ctx);
        await sleep(600);
        assert.equal(await page.eval(`document.getElementById('input').value`), "half-written thought", "a live update wiped it");
        assert.equal(await page.eval(`document.activeElement?.id`), "input", "a live update took focus away");
        await closeSheet(page);
        await openRow(page, 103);
        assert.equal(await page.eval(`document.getElementById('input').value`), "", "another item shows the draft");
        await closeSheet(page);
        await openRow(page, 104);
        assert.equal(await page.eval(`document.getElementById('input').value`), "half-written thought");
    });

    // ---- Readiness -------------------------------------------------------------------------------------
    test("readiness: core gates, recommended pr-review, and the label as a muted hint", async ({ page }) => {
        await openRow(page, 102);
        await tabTo(page, "overview");
        const gates = await page.eval(`[...document.querySelectorAll('#wsScroll .gates li')].map((l) => ({ id: l.dataset.gate || 'label', cls: l.className, text: l.innerText.replace(/\\s+/g, ' ') }))`);
        const g = (id) => gates.find((x) => x.id === id);
        assert.match(g("copilot").cls, /gs-fail/);
        assert.match(g("copilot").text, /Reviewed an older commit/);
        assert.match(g("ci").cls, /gs-fail/);
        assert.match(g("discussion").cls, /gs-pass/);
        assert.match(g("uptodate").cls, /gs-pass/);
        assert.match(g("review").text, /Recommended/);
        assert.match(g("label").cls, /gs-info/);
        assert.match(await text(page, "#wsHead"), /2\/4 ready/, "the meter counts only core gates");
    });

    test("readiness: keep-ready makes pr-review a core gate", async ({ page }) => {
        await openRow(page, 101);
        await tabTo(page, "overview");
        const ids = await page.eval(`[...document.querySelectorAll('#wsScroll .gates li[data-gate]')].map((l) => l.dataset.gate)`);
        assert.ok(ids.includes("review"));
        assert.match(await text(page, "#wsHead"), /5\/5 ready/);
        await closeSheet(page);
        await openRow(page, 107);
        assert.match(await text(page, "#wsHead"), /4\/4 ready/);
    });

    test("readiness: a blocking finding offers Fix and Not needed", async (ctx) => {
        const { page } = ctx;
        await ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 107, review: { headSha: HEAD[107], verdict: "request-changes", risk: "medium", summary: "One real problem.", findings: [{ id: "f9", title: "Export drops the header row", category: "risk", severity: "high", what: "x", fix: "y", files: [{ path: "src/csv.js", line: 3 }] }] } });
        await openRow(page, 107);
        await tabTo(page, "overview");
        await page.waitFor(`!!document.querySelector('#wsScroll .gates [data-act="finding-fix"][data-id="f9"]')`, 4000, "Fix on the finding");
        await page.click('#wsScroll .gates [data-act="finding-fix"][data-id="f9"]');
        // Asking for a fix takes it off the blocking list: the item now counts it as being fixed.
        await page.waitFor(`!document.querySelector('#wsScroll .gates [data-id="f9"]') && /1 being fixed/.test(document.querySelector('#wsScroll .gates li[data-gate="review"]')?.innerText || '')`, 4000, "the finding to show as being fixed");
        assert.equal(state(ctx).prs["107"].triage.f9.fixRequestedAt !== undefined, true);
        assert.match(ctx.host.sends.at(-1)?.prompt || "", /Export drops the header row/);
    });

    test("readiness: Not needed records the decision and clears the gate", async (ctx) => {
        const { page } = ctx;
        await ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 107, review: { headSha: HEAD[107], verdict: "request-changes", risk: "medium", summary: "One real problem.", findings: [{ id: "f9", title: "Export drops the header row", category: "risk", severity: "high", what: "x", fix: "y", files: [{ path: "src/csv.js", line: 3 }] }] } });
        await openRow(page, 107);
        await tabTo(page, "overview");
        await page.waitFor(`!!document.querySelector('#wsScroll .gates [data-act="finding-skip"][data-id="f9"]')`, 4000, "Not needed on the finding");
        await page.click('#wsScroll .gates [data-act="finding-skip"][data-id="f9"]');
        await page.waitFor(`!document.querySelector('#wsScroll .gates [data-id="f9"]') && document.querySelector('#wsScroll .gates li[data-gate="review"]')?.classList.contains('gs-pass')`, 4000, "the pr-review item to pass");
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
        await page.click('#wsHead [data-act="tab"][data-tab="review"]');
        await page.waitFor(`!!document.querySelector('#wsScroll #finding-f1')`, 3000, "the findings");
        assert.match(await text(page, "#wsScroll"), /Recommends changes/);
        assert.match(await text(page, "#wsScroll"), /Two refreshes can race/);
        assert.equal(await page.eval(`document.querySelector('#wsScroll [data-act="rv-preview"]').dataset.event`), "request_changes");
    });

    test("review tab: 0 findings offers Approve", async ({ page }) => {
            await page.click(`#overview .ov-sec .ov-title[data-kind="pr"][data-pr="105"]`);
        await page.waitFor(`document.body.classList.contains('sheet')`);
        await page.click('#wsHead [data-act="tab"][data-tab="review"]');
        await page.waitFor(`!!document.querySelector('#wsScroll [data-act="rv-preview"]')`, 3000, "the post button");
        assert.equal(await page.eval(`document.querySelector('#wsScroll [data-act="rv-preview"]').dataset.event`), "approve");
        assert.match(await text(page, '#wsScroll [data-act="rv-preview"]'), /Approve/);
    });

    test("review tab: the preview shows exactly what will post, and posting goes through the agent", async (ctx) => {
        const { page } = ctx;
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

    test("regression (#1382, #1388): a comment on a line outside the diff goes in the summary, and the preview says why", async (ctx) => {
        const { page } = ctx;
        await ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 104, review: { headSha: HEAD[104], verdict: "request-changes", risk: "medium", summary: "Two things.", findings: [
            { id: "f1", title: "Two refreshes can race", category: "risk", severity: "high", what: "x", fix: "y", files: [{ path: "src/auth.js", line: 40, endLine: 42 }] },
            { id: "f2", title: "Rename `t` to `token`", category: "nit", severity: "low", what: "x", fix: "y", files: [{ path: "src/auth.js", line: 10 }] },
            { id: "f3", title: "Logo is too big", category: "nit", severity: "low", what: "x", fix: "y", files: [{ path: "assets/logo.png", line: 1 }] },
            { id: "f4", title: "Old helper still used", category: "risk", severity: "medium", what: "x", fix: "y", files: [{ path: "src/legacy.js", line: 3 }] },
        ] } });
        for (const id of ["f1", "f2", "f3", "f4"]) await page.eval(`post("/api/triage", { pr: 104, findingId: "${id}", decision: "post" })`);
        await openRow(page, 104);
        await tabTo(page, "review");
        await page.click('#wsScroll [data-act="rv-preview"]');
        await page.waitFor(`!!document.querySelector('#wsScroll .preview .unplaced')`, 5000, "the note about comments in the summary");
        const note = await text(page, "#wsScroll .preview .unplaced");
        assert.match(note, /3 comments will appear in the summary, not on a line/);
        assert.match(note, /Rename `?t`? to `?token`?: line 10 of src\/auth\.js isn't part of this PR's diff/);
        assert.match(note, /Logo is too big: GitHub doesn't show a diff for assets\/logo\.png/);
        assert.match(note, /Old helper still used: src\/legacy\.js isn't one of the files this PR changes/);
        const inline = await page.eval(`[...document.querySelectorAll('#wsScroll .preview .inl .hint')].map((e) => e.innerText)`);
        assert.deepEqual(inline, ["Inline on src/auth.js:42"]);
        // What's sent to GitHub: one inline comment on lines 40-42 at the PR's head, and the rest in the body.
        await page.click('#wsScroll [data-act="rv-post"]');
        await page.waitFor("true", 100);
        for (let i = 0; i < 40 && !/request body is in/.test(ctx.host.sends.at(-1)?.prompt || ""); i++) await new Promise((r) => setTimeout(r, 100));
        const prompt = ctx.host.sends.at(-1)?.prompt || "";
        assert.match(prompt, /1 inline comment\(s\) and 3 finding\(s\) in the summary body \(3 of them point at lines outside the PR's diff/);
        const file = prompt.match(/`([^`]+posts[^`]+\.json)`/)[1];
        const req = JSON.parse(fs.readFileSync(file, "utf8"));
        assert.equal(req.commit_id, HEAD[104]);
        assert.deepEqual(req.comments.map((c) => [c.path, c.start_line, c.line, c.side]), [["src/auth.js", 40, 42, "RIGHT"]]);
        assert.match(req.body, /Rename `t` to `token`.*\(`src\/auth\.js:10`\)/s);
        assert.match(req.body, /Old helper still used.*\(`src\/legacy\.js:3`\)/s);
        assert.equal(ctx.world.mutations.length, 0, "the board posted it itself");
    });
    test("regression: after Get it ready, keep-ready doesn't ask for pr-review again on the same commit", async (ctx) => {
        const { page, world } = ctx;
        world.allowMutations = true; // keep-ready requests a Copilot review after the push below
        const realNow = Date.now;
        const later = (min) => { const off = min * 60_000; Date.now = () => realNow() + off; };
        const asks = () => ctx.host.sends.filter((x) => /pr-review hasn't run on it/.test(x.prompt)).map((x) => x.prompt.match(/commit is now (\w+)/)[1]);
        try {
            await page.eval(`post("/api/action", { pr: 107, action: "get-ready" })`);
            assert.match(ctx.host.sends.at(-1)?.prompt || "", /get it ready|ready for (human )?review/i);
            await ctx.board.tool("pr_board_sync_sessions", { repo: REPO, sessions: [{ pr: 107, sessionId: "s-107", name: "CSV export", role: "author", activity: "busy", agentMerge: { enabled: true, actions: ["address_reviews", "fix_ci", "resolve_conflicts"] } }] });
            await ctx.board.tool("pr_board_reply", { repo: REPO, pr: 107, text: "On it: running pr-lifecycle." });
            await refresh(ctx); // keep-ready first sees the head now
            await sleep(800);
            later(6); // the head has been stable for over 5 minutes
            await refresh(ctx);
            await sleep(1500);
            assert.deepEqual(asks(), [], "asked for pr-review on the head the Get it ready prompt already covers");
            // A new commit after that is new work: keep-ready asks for it, once.
            const oid = world.push(107, "Address Copilot's comments");
            later(7);
            await refresh(ctx);
            await sleep(800);
            later(14);
            await refresh(ctx);
            for (let i = 0; i < 30 && !asks().length; i++) await sleep(100);
            assert.deepEqual(asks(), [oid.slice(0, 7)]);
        } finally { Date.now = realNow; }
    }, { changesGitHub: true, allowMutations: true });
    test("regression (#1391): pr-review ran in a session but no findings reached the board: say so, and load them instead of re-running", async (ctx) => {
        const { page } = ctx;
        await ctx.board.tool("pr_board_sync_sessions", { repo: REPO, sessions: [{ pr: 101, sessionId: "s-101", name: "Uploader retries", role: "author", activity: "idle", agentMerge: { enabled: true, actions: ["address_reviews"] }, prReview: { ranAt: new Date().toISOString(), headSha: HEAD[101], result: "drafted", outcome: "passed" } }] });
        await openRow(page, 101);
        const chip = await text(page, "#wsHead .ws-meta");
        assert.match(chip, /pr-review: Ran \(self-check\)/);
        assert.doesNotMatch(chip, /Drafted/);
        await tabTo(page, "review");
        const tab = await text(page, "#wsScroll");
        assert.match(tab, new RegExp(`pr-review ran on ${HEAD[101].slice(0, 7)} \\(self-check, passed\\)`));
        assert.match(tab, /Its findings aren't loaded on the board; they're in the session "Uploader retries"/);
        assert.doesNotMatch(tab, /No review findings yet/);
        assert.equal(await exists(page, '#wsScroll [data-act="review"]'), false, "it offers to re-run pr-review on the same commit");
        assert.equal(await page.eval(`document.querySelector('#wsScroll a[href="ghapp://sessions/s-101"]')?.innerText`), "Open the session");
        await page.click('#wsScroll [data-act="load-review"]');
        const p = await waitForSend(ctx, /load the details/);
        assert.match(p, /Don't run pr-review again/);
        assert.match(p, /session `s-101`/);
        // The session answers with its own pr-review JSON; the board converts it.
        const file = `${ctx.board.home}\\pr-review-101.json`;
        fs.writeFileSync(file, JSON.stringify({ finalHeadSha: HEAD[101], verdict: "APPROVE", summary: "Self-check passed after one fix.", rounds: [
            { reviewedHeadSha: "1010aaa", findings: [{ title: "Retry forever", severity: "Blocking", file: "src/upload.js:12" }], declined: [{ title: "Use exponential backoff", severity: "nit", file: "src/upload.js:20-22", reason: "three retries are enough here" }] },
            { reviewedHeadSha: HEAD[101], findings: [], declined: [] }] }));
        const r = await ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 101, path: file });
        assert.ok(r.warnings.some((w) => /converted pr-review rounds: 0 finding\(s\) from round 2, 1 declined/.test(w)), JSON.stringify(r.warnings));
        assert.ok(r.warnings.some((w) => /verdict "APPROVE" → approve/.test(w)));
        await page.waitFor(`/Use exponential backoff/.test(document.getElementById('wsScroll').innerText)`, 4000, "the converted finding");
        const tab2 = await text(page, "#wsScroll");
        assert.match(tab2, /Declined by the session: three retries are enough here/);
        assert.match(tab2, /src\/upload\.js:20-22/);
        assert.equal(state(ctx).prs["101"].review.headSha, HEAD[101]);
        // Findings with top-level file/line/endLine (not files[]) are converted, in every review tool.
        const add = await ctx.board.tool("pr_board_add_findings", { repo: REPO, pr: 101, findings: [{ id: "f9", title: "Log the retry count", category: "nit", severity: "low", file: "src/upload.js", line: 30, endLine: 31 }] });
        assert.ok(!add.warnings.some((w) => /f9: no file line/.test(w)), JSON.stringify(add.warnings));
        assert.deepEqual(state(ctx).prs["101"].review.findings.find((f) => f.id === "f9").files, [{ path: "src/upload.js", line: 30, endLine: 31 }]);
        // A diagram sent as a Mermaid string becomes a diagram; anything else that isn't an object is skipped, never a crash.
        const mm = await ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 101, review: { summary: "Diagrams as text.", verdict: "approve", diagrams: ["flowchart LR\n A[CLI] --> B[DevTools engine]\n B -->|RPC| C(App)", "not a diagram"] } });
        assert.ok(mm.warnings.some((w) => /diagram 1: converted a Mermaid string into a flow diagram/.test(w)), JSON.stringify(mm.warnings));
        assert.ok(mm.warnings.some((w) => /diagram 2: skipped; expected an object, got string/.test(w)));
        await tabTo(page, "architecture");
        await page.waitFor(`/DevTools engine/.test(document.getElementById('wsScroll').innerText)`, 4000, "the converted diagram");
        await assert.rejects(ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 101, review: { status: "ok" } }), /Nothing the board can show: expected at least one of summary, verdict, findings \(or rounds\[\]\.findings\).*Got keys: status/);
    });
    // ---- Branch review (issue with no PR yet) ------------------------------------------------------
    test("changes tab: comment on a line, then send it to the agent", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 203, "issue");
        await tabTo(page, "changes");
        await page.waitFor(`!!document.querySelector('#wsScroll tr.dl td.ln')`, 4000, "the diff");
        await page.click(`#wsScroll tr.dl.a td.ln:nth-child(2)`);
        await page.waitFor(`!!document.querySelector('#wsScroll .br-comp textarea')`, 2000, "the comment box");
        await page.click("#wsScroll .br-comp textarea");
        await page.type("Handle a corrupt file too");
        await page.key("Enter", { code: "Enter", keyCode: 13, text: "\r" }).catch(() => {});
        await page.click('#wsScroll [data-act="br-save"]').catch(() => {});
        await page.waitFor(`/Handle a corrupt file too/.test(document.querySelector('#wsScroll').innerText) && !document.querySelector('#wsScroll .br-comp textarea')`, 4000, "the saved comment");
        assert.match(await text(page, '#wsScroll [data-act="br-send"]'), /Send 1 comment/);
        await page.click('#wsScroll [data-act="br-send"]');
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
        await tabTo(page, "changes");
        await page.waitFor(`/Why cache this\\?/.test(document.querySelector('#wsScroll').innerText)`, 4000, "the old comment");
        assert.match(await text(page, "#wsScroll"), /Outdated/);
    });

    // ---- GitHub tab --------------------------------------------------------------------------------
    test("GitHub tab: renders the timeline and never runs markup from GitHub", async ({ page }) => {
        await openRow(page, 102);
        await tabTo(page, "github");
        await page.waitFor(`/Why not default to/.test(document.querySelector('#wsScroll')?.innerText || '')`, 5000, "the GitHub view");
        const body = await text(page, "#wsScroll");
        assert.match(body, /src\/config\.js/);
        assert.match(body, /build-and-package/);
        assert.match(body, /Looks reasonable/);
        await sleep(300);
        assert.equal(await page.eval(`window.__xss`), undefined, "GitHub markup ran in the board");
        assert.equal(await page.eval(`document.querySelectorAll('#wsScroll script, #wsScroll [onerror], #wsScroll a[href^="javascript:"]').length`), 0);
    });

    test("GitHub tab: a failed or slow GitHub read shows an error, not a broken panel", async (ctx) => {
        const { page, world } = ctx;
        // The board keeps GitHub reads for 4 minutes, so no other test may open #103's GitHub tab.
        world.fault = { match: (a) => a.some((x) => x.includes("closingIssuesReferences")), delayMs: 800, error: "HTTP 502: Bad Gateway" };
        page.allowStatus.add("GET /api/github 502");
        await openRow(page, 103);
        await tabTo(page, "github");
        await page.waitFor(`/Bad Gateway|couldn't|Couldn't/.test(document.querySelector('#wsScroll')?.innerText || '')`, 6000, "an error message");
        assert.equal(await sheetOpen(page), true);
        assert.equal(await exists(page, '#wsScroll [data-act="gh-refresh"]'), true, "there's a way to retry");
    });

    // ---- Merge from the board ------------------------------------------------------------------------
    const openMergeDlg = async (page, sel = '#wsHead [data-act="merge-open"]') => {
        await page.click(sel);
        await page.waitFor(`!!document.querySelector('.merge-dlg') && !/Reading the PR/.test(document.querySelector('.merge-dlg').innerText)`, 5000, "the merge dialog");
    };
    const dlg = (page) => text(page, ".merge-dlg");
    const merges = (world) => world.mutations.map((a) => a.join(" "));

    test("merge: an approved, green PR shows what will happen, and Add to merge queue queues it", async (ctx) => {
        const { page, world } = ctx;
        world.allowMutations = true;
        await openRow(page, 107);
        await openMergeDlg(page);
        const d = await dlg(page);
        assert.match(d, /Merge PR #107\?/);
        assert.match(d, /Add CSV export/);
        assert.match(d, new RegExp(`branch-107 → main · commit ${HEAD[107].slice(0, 7)}`));
        assert.match(d, /adds it to the merge queue for main now/);
        assert.equal(world.mutations.length, 0, "opening the dialog wrote to GitHub");
        await page.click('.merge-dlg [data-act="merge-confirm"]');
        await page.waitFor(`/In the merge queue/.test(document.querySelector('.merge-dlg .mg-done')?.innerText || '')`, 8000, "the queued result");
        assert.deepEqual(merges(world), [`pr merge 107 -R ${REPO} --squash --match-head-commit ${HEAD[107]} --auto`]);
        assert.ok(!merges(world).some((m) => m.includes("--admin")));
        assert.equal(ctx.host.sends.length, 0, "the agent was asked to merge");
        await page.key("Escape");
        await page.waitFor(`!document.querySelector('.merge-dlg')`, 2000, "the dialog to close");
        assert.equal(await sheetOpen(page), true, "Esc closed the sheet too");
        await page.waitFor(`/In merge queue #2/.test(document.getElementById('wsHead').innerText)`, 5000, "the header to show the queue");
        assert.match(await text(page, "#tScroll"), /You added it to the merge queue for main/);
    }, { changesGitHub: true, allowMutations: true });

    test("merge: Cancel, Esc and the backdrop close the dialog without touching GitHub", async (ctx) => {
        const { page, world } = ctx;
        await openRow(page, 107);
        await openMergeDlg(page);
        await page.key("Escape");
        await page.waitFor(`!document.querySelector('.merge-dlg')`, 2000, "Esc");
        await openMergeDlg(page);
        await page.click('.merge-dlg footer [data-act="merge-close"]');
        await page.waitFor(`!document.querySelector('.merge-dlg')`, 2000, "Cancel");
        await openMergeDlg(page);
        await page.click(null, { at: { x: 10, y: 990 } });
        await page.waitFor(`!document.querySelector('.merge-dlg')`, 2000, "the backdrop");
        assert.equal(await sheetOpen(page), true);
        assert.equal(world.mutations.length, 0);
    });

    test("merge: a PR waiting on approval offers Add to queue when ready, and auto-merge can be turned off again", async (ctx) => {
        const { page, world } = ctx;
        world.allowMutations = true;
        await openRow(page, 101);
        await openMergeDlg(page);
        let d = await dlg(page);
        assert.match(d, /It waits for/i);
        assert.match(d, /Needs 1 approving review \(requested from zed\)/);
        assert.match(await text(page, '.merge-dlg [data-act="merge-confirm"]'), /Add to queue when ready/);
        await ctx.shot("merge-dialog-waits");
        await page.click('.merge-dlg [data-act="merge-confirm"]');
        await page.waitFor(`/Auto-merge is on/.test(document.querySelector('.merge-dlg')?.innerText || '')`, 8000, "auto-merge on");
        assert.match(merges(world)[0], /^pr merge 101 .*--auto$/);
        d = await dlg(page);
        assert.match(d, /Turn off auto-merge/);
        await page.click('.merge-dlg [data-act="merge-undo"]');
        await page.waitFor(`/Auto-merge is off/.test(document.querySelector('.merge-dlg .mg-done')?.innerText || '')`, 8000, "auto-merge off");
        assert.equal(merges(world)[1], `pr merge 101 -R ${REPO} --disable-auto`);
        assert.match(await text(page, "#tScroll"), /You turned on auto-merge.*You turned off auto-merge/);
    }, { changesGitHub: true, allowMutations: true });

    test("merge: the last-push rule explains why an approval doesn't count", async (ctx) => {
        const { page, world } = ctx;
        world.prs.find((p) => p.number === 107).pusher = "zed";
        await openRow(page, 107);
        await openMergeDlg(page);
        assert.match(await dlg(page), /zed pushed the latest commit, so their approval doesn't count here/);
        assert.match(await text(page, '.merge-dlg [data-act="merge-confirm"]'), /Add to queue when ready/);
        await page.key("Escape");
        world.prs.find((p) => p.number === 107).pusher = null;
        world.push(107, "One more fix");
        await openMergeDlg(page);
        assert.match(await dlg(page), /zed approved an older commit\. The branch rules need a fresh approval of the latest push/);
        assert.equal(world.mutations.length, 0);
    }, { changesGitHub: true });

    test("merge: failing required checks or conflicts say why, with no merge button", async (ctx) => {
        const { page, world } = ctx;
        const p = world.prs.find((x) => x.number === 107);
        p.checks[0].conclusion = "FAILURE";
        p.mergeable = "CONFLICTING";
        await openRow(page, 107);
        await openMergeDlg(page);
        const d = await dlg(page);
        assert.match(d, /What's in the way/i);
        assert.match(d, /Required check failed: build-and-package/);
        assert.match(d, /merge conflicts with main/);
        assert.equal(await exists(page, '.merge-dlg [data-act="merge-confirm"]'), false, "a merge button for a PR that can't merge");
        assert.match(await text(page, '.merge-dlg footer'), /Close/);
    }, { changesGitHub: true });

    test("merge: if new commits land after the dialog opened, nothing merges", async (ctx) => {
        const { page, world } = ctx;
        page.allowStatus.add("POST /api/merge 409");
        await openRow(page, 107);
        await openMergeDlg(page);
        world.push(107, "Late change");
        await page.click('.merge-dlg [data-act="merge-confirm"]');
        await page.waitFor(`/New commits landed after you opened this/.test(document.querySelector('.merge-dlg .mg-err')?.innerText || '')`, 6000, "the refusal");
        assert.equal(world.mutations.length, 0);
    }, { changesGitHub: true });

    test("merge: an agent's \"Merge\" choice opens the dialog instead of asking the agent", async (ctx) => {
        const { page } = ctx;
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, pr: 105, owner: "you", action: "Looks good: merge it?", choices: ["Merge", "Not yet"] });
        await page.waitFor(`!!document.querySelector('#overview .ym-card [data-act="choose"][data-pr="105"][data-choice="Merge"]')`, 4000, "the choice");
        const before = ctx.host.sends.length;
        await openMergeDlg(page, '#overview .ym-card [data-act="choose"][data-pr="105"][data-choice="Merge"]');
        assert.match(await dlg(page), /Merge PR #105\?/);
        assert.equal(ctx.host.sends.length, before, "the agent was asked instead");
        assert.equal(ctx.world.mutations.length, 0);
    });

    test("merge: a PR in the queue can be taken out", async (ctx) => {
        const { page, world } = ctx;
        world.allowMutations = true;
        await openRow(page, 106);
        assert.match(await text(page, '#wsHead [data-act="merge-open"]'), /In merge queue #1/);
        await openMergeDlg(page);
        assert.match(await dlg(page), /PR #106 is in the merge queue.*position 1/);
        await page.click('.merge-dlg [data-act="merge-undo"]');
        await page.waitFor(`/Taken out of the merge queue/.test(document.querySelector('.merge-dlg .mg-done')?.innerText || '')`, 8000, "the result");
        assert.match(merges(world)[0], /^api graphql -f query=mutation.*dequeuePullRequest.* -f id=PR_106$/s);
    }, { changesGitHub: true, allowMutations: true });

    // ---- #1359: own PRs, stale summaries, and "nothing needed from you" --------------------------------
    test("regression (#1359): a What changed summary older than newer pushes says it's superseded", async (ctx) => {
        const { page, world } = ctx;
        await ctx.board.tool("pr_board_set_what_changed", { repo: REPO, pr: 107, text: "zed requested changes: **fix the retry count**." });
        world.push(107, "Fix the retry count");
        await refresh(ctx);
        await ctx.board.tool("pr_board_reply", { repo: REPO, pr: 107, text: "Fixed the retry count; waiting on zed.", from: "PR #107 session" });
        await page.waitFor(`/\\d+ newer commits? since the last summary/.test(document.querySelector('#overview .ov-title[data-pr="107"]').closest('.ov-row, .ym-card').innerText)`, 5000, "the row to say it's superseded");
        assert.doesNotMatch(await rowText(page, 107), /fix the retry count\b(?!;)/i, "the row still shows the old ask");
        await openRow(page, 107);
        await tabTo(page, "overview");
        const box = await text(page, "#wsScroll .wc-box");
        assert.match(box, /Superseded by \d+ newer commits?/);
        assert.match(box, /Latest from PR #107 session.*Fixed the retry count; waiting on zed/);
        assert.equal(await page.eval(`document.querySelector('#wsScroll .wc-box .wc-old').open`), false, "the old summary is open");
        await ctx.shot("what-changed-superseded");
    }, { changesGitHub: true });

    test("regression (#1359): your own PR never leads with Post review or Your re-review", async (ctx) => {
        const { page, world } = ctx;
        await ctx.board.tool("pr_board_set_review", { repo: REPO, pr: 107, review: { headSha: HEAD[107], verdict: "approve", risk: "low", summary: "Self-check.", findings: [] } });
        const p = world.prs.find((x) => x.number === 107);
        p.reviewDecision = "CHANGES_REQUESTED";
        p.reviews.push({ id: "", author: { login: "zed" }, state: "CHANGES_REQUESTED", submittedAt: new Date(Date.now() - 180 * 60000).toISOString(), body: "", commit: { oid: HEAD[107] } });
        world.push(107, "Address zed's review");
        await refresh(ctx);
        await openRow(page, 107);
        const primaries = await page.eval(`[...document.querySelectorAll('#wsHead .actions button.primary')].map((b) => b.innerText.trim())`);
        assert.ok(!primaries.some((t) => /Post review|Re-review|Review/.test(t)), `primary: ${primaries}`);
        assert.equal(await exists(page, '#wsHead .actions [data-act="tab"][data-tab="review"]'), false, "Post review… on your own PR");
        const head = await text(page, "#wsHead .ws-meta");
        assert.doesNotMatch(head, /Your re-review/);
        assert.match(head, /Waiting on re-review/);
    }, { changesGitHub: true });

    test("regression (#1359): when the move isn't yours, the sheet says Nothing needed from you, with optional asks", async (ctx) => {
        const { page } = ctx;
        await ctx.board.tool("pr_board_set_next_step", { repo: REPO, pr: 101, owner: "reviewer", action: "zed re-reviews the retry fix", optional: ["Judge whether the empty-upload edge case matters"] });
        await openRow(page, 101);
        const line = await text(page, "#wsHead .nothing-you");
        assert.match(line, /Nothing needed from you right now/);
        assert.match(line, /Optional: Judge whether the empty-upload edge case matters/);
        await ctx.shot("nothing-needed");
        await page.click('#wsHead .nothing-you [data-act="ball-reply"]');
        assert.equal(await page.eval(`document.activeElement?.id`), "input");
        await closeSheet(page);
        await openRow(page, 107);
        assert.equal(await exists(page, "#wsHead .nothing-you"), false, "Nothing needed on a PR that's your move");
    });
    // ---- Anchors -----------------------------------------------------------------------------------
    test("anchors: a finding chip opens the Review tab and highlights it", async ({ page }) => {
        await openRow(page, 104);
        await page.waitFor(`!!document.querySelector('#tScroll .anchor[data-a="finding:f1"]')`, 3000, "the chip");
        assert.match(await text(page, '#tScroll .anchor[data-a="finding:f1"]'), /Two refreshes can race/);
        await page.click('#tScroll .anchor[data-a="finding:f1"]');
        await page.waitFor(`document.querySelector('#finding-f1')?.classList.contains('anchor-hi')`, 3000, "f1 highlighted");
        assert.match(await text(page, '#wsHead [aria-selected="true"]'), /Review/);
    });

    test("anchors: a reply with focus jumps there while the item is open", async (ctx) => {
        const { page } = ctx;
        await openRow(page, 104);
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
        await closeSheet(page);
        world.fault = null;
        await refresh(ctx);
        await page.waitFor(`document.getElementById('banner').hidden`, 4000, "the banner to clear");
    });

    test("robustness: live updates don't reset scroll or focus", async (ctx) => {
        const { page } = ctx;
        await page.eval(`document.getElementById('overview').scrollTop = 350`);
        await openRow(page, 104);
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
        await closeSheet(page);
        await page.eval(`document.activeElement.blur()`);
        await closeSheet(page);
        await page.waitFor(`!document.body.classList.contains('sheet')`);
        assert.equal(await page.eval(`document.getElementById('overview').scrollTop`), 350, "the list scrolled");
    });

    test("safety: the board never writes to GitHub on its own while nothing is kept ready", async (ctx) => {
        await sleep(500);
        assert.equal(ctx.world.mutations.length, 0);
        assert.equal(ctx.host.sends.length, 0, "the board sent the agent a prompt by itself");
    });
}

const refresh = (ctx) => ctx.board.refresh();

// Proactive mode, on and with no waiting, unless a test says otherwise.
const autoOn = (ctx, extra = {}) => ctx.board.api("/api/settings", { settings: { paused: false, stableMin: 0, cooldownMin: 0, machineCheck: false, ...extra } });
const autoPrompts = (ctx) => ctx.host.sends.filter((s) => s.prompt.startsWith("[PR board] Auto:"));
async function waitForSend(ctx, re, ms = 6000) {
    const end = Date.now() + ms;
    for (;;) {
        const hit = ctx.host.sends.find((s) => re.test(s.prompt));
        if (hit) return hit.prompt;
        if (Date.now() > end) throw new Error(`No prompt matched ${re}. Sent: ${ctx.host.sends.map((s) => s.prompt.split("\n")[0]).join(" | ") || "nothing"}`);
        await sleep(100);
    }
}

// Drop the page's live (SSE) connection on the server side, like a sleep/wake or network blip.
function dropLiveConnections(ctx) {
    const port = Number(new URL(ctx.board.url).port);
    const servers = process._getActiveHandles().filter((h) => h?.constructor?.name === "Server" && h.address?.()?.port === port);
    if (!servers.length) throw new Error("couldn't find the board's server");
    for (const s of servers) s.closeAllConnections();
}
