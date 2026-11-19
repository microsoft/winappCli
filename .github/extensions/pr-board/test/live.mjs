// Read-only smoke test of the real board: your repo's PRs from the real gh, a copy of your board state.
// Writes to GitHub are refused, and the agent is a stand-in that only records prompts.
import assert from "node:assert/strict";

const rows = (page) => page.eval(`[...document.querySelectorAll('#overview .ov-sec .ov-row')].map((r) => { const t = r.querySelector('.ov-title'); return { kind: t.dataset.kind, n: Number(t.dataset.pr), text: r.innerText.replace(/\\s+/g, ' '), unread: !!r.querySelector('.unread-dot'), spin: !!r.querySelector('svg.spin') }; })`);

export function register(test) {
    test("live: the board loads real PRs without errors", async ({ page, shot }) => {
        await page.waitFor(`!/Loading/.test(document.getElementById('meta').innerText)`, 30000, "the first GitHub read");
        const r = await rows(page);
        assert.ok(r.length > 0, "no rows");
        assert.equal(await page.eval(`document.getElementById('banner').hidden`), true, await page.eval(`document.getElementById('banner').innerText`));
        await shot("live-overview");
    });

    test("live: rows are consistent (no duplicates, unread count, no decision with a spinner)", async ({ page }) => {
        const r = await rows(page);
        const keys = r.map((x) => `${x.kind}${x.n}`);
        assert.deepEqual(keys.filter((k, i) => keys.indexOf(k) !== i), [], "an item is listed twice");
        const chip = Number(await page.eval(`document.querySelector('#overview .unread-chip .n')?.innerText || 0`));
        assert.equal(chip, r.filter((x) => x.unread).length, "the Unread count doesn't match the rows with a dot");
        assert.deepEqual(r.filter((x) => x.spin && /Needs your decision/.test(x.text)).map((x) => x.n), []);
    });

    test("live: open the first PR, expand it, read its GitHub tab, and come back", async ({ page, shot }) => {
        const n = await page.eval(`document.querySelector('#overview .ov-sec .ov-title[data-kind="pr"]')?.dataset.pr`);
        assert.ok(n, "no PR rows");
        await page.click(`#overview .ov-sec .ov-title[data-kind="pr"][data-pr="${n}"]`);
        await page.waitFor(`!!document.querySelector('#flyHost .fly')`, 3000, "the panel");
        await new Promise((r) => setTimeout(r, 400));
        assert.equal(await page.eval(`document.querySelector('#flyHost .fly-body').scrollTop`), 0, "opened scrolled down");
        await page.click('#flyHost [data-act="fly-tab"][data-tab="github"]');
        await page.waitFor(`!!document.querySelector('#flyHost .fly-body .gh-card, #flyHost .fly-body .gh-head, #flyHost .fly-body .gh-tl')`, 30000, "the GitHub view");
        await shot("live-github-tab");
        await page.key("e");
        await page.waitFor(`document.body.classList.contains('sheet')`, 3000, "the sheet");
        await shot("live-sheet");
        await page.key("Escape");
        await page.waitFor(`!document.body.classList.contains('sheet')`, 3000, "back to the list");
    });
}
