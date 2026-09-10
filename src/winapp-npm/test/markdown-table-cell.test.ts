// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as path from 'path';
import { pathToFileURL } from 'url';

type TableCell = (text: string | undefined | null) => string;

// The npm package compiles to CommonJS, so a plain `await import()` would be transpiled to require()
// and fail on an ESM .mjs module. This indirection keeps a real dynamic import at runtime.
const importEsm = new Function('specifier', 'return import(specifier)') as (
  specifier: string
) => Promise<{ tableCell: TableCell }>;

// npm scripts run from src/winapp-npm, matching how ui-record-guard.test.ts resolves generated files.
const MODULE_URL = pathToFileURL(path.resolve(process.cwd(), 'scripts', 'markdown-table-cell.mjs')).href;

function loadTableCell(): Promise<TableCell> {
  return importEsm(MODULE_URL).then((mod) => mod.tableCell);
}

// String.raw cannot express a trailing backslash, and stacked escapes are easy to misread, so the
// cases below build their strings from this constant and annotate the characters they contain.
const BS = '\\';

/**
 * Counts pipes that would still split a Markdown table row. A backslash escapes the character that
 * follows it, so `\\` is a literal backslash that protects nothing and `\|` is a safe pipe.
 */
function countUnescapedPipes(cell: string): number {
  let count = 0;
  for (let i = 0; i < cell.length; i++) {
    if (cell[i] === BS) {
      i++; // the next character is escaped, whatever it is
      continue;
    }
    if (cell[i] === '|') count++;
  }
  return count;
}

test('a backslash before a pipe cannot cancel the pipe escape', async () => {
  const tableCell = await loadTableCell();

  // Escaping only the pipe turns `\|` into `\\|`, which Markdown renders as a literal backslash
  // followed by an *unescaped* pipe — so the row splits anyway.
  const cell = tableCell(`a${BS}|b`); // a \ | b

  assert.equal(cell, `a${BS}${BS}${BS}|b`); // a \ \ \ | b
  assert.equal(countUnescapedPipes(cell), 0);
});

test('a trailing backslash cannot escape the row delimiter', async () => {
  const tableCell = await loadTableCell();

  const cell = tableCell(`a path ending in C:${BS}`); // ...C:\

  assert.equal(cell, `a path ending in C:${BS}${BS}`); // ...C:\\
  assert.ok(cell.endsWith(`${BS}${BS}`), 'the closing pipe this generator emits after the cell must survive');
});

test('Windows paths keep their backslashes literal while pipes stay escaped', async () => {
  const tableCell = await loadTableCell();

  const cell = tableCell(`use C:${BS}Users${BS}me | or D:${BS}tmp`);

  assert.equal(cell, `use C:${BS}${BS}Users${BS}${BS}me ${BS}| or D:${BS}${BS}tmp`);
  assert.equal(countUnescapedPipes(cell), 0);
});

test('multi-paragraph text collapses to a single line', async () => {
  const tableCell = await loadTableCell();

  const cell = tableCell('First paragraph\nwrapped here.\n\nSecond paragraph.');

  assert.equal(cell, 'First paragraph wrapped here.<br><br>Second paragraph.');
  assert.ok(!/\r|\n/.test(cell), 'a table row must not contain a line break');
});

test('backslash, pipe and newlines survive together', async () => {
  const tableCell = await loadTableCell();

  const cell = tableCell(`Pass a${BS}|b to the filter.\r\n\r\nSee C:${BS}logs\nfor output.`);

  assert.equal(countUnescapedPipes(cell), 0);
  assert.ok(!/\r|\n/.test(cell));
  assert.ok(cell.includes('<br><br>'), 'the paragraph break must be preserved');
  assert.ok(cell.includes(`C:${BS}${BS}logs`), 'path backslashes must stay literal');
});

test('empty input yields an empty cell', async () => {
  const tableCell = await loadTableCell();

  assert.equal(tableCell(''), '');
  assert.equal(tableCell(undefined), '');
  assert.equal(tableCell(null), '');
});

test('ordinary prose is passed through untouched apart from trimming', async () => {
  const tableCell = await loadTableCell();

  assert.equal(tableCell('  Suppress progress messages.  '), 'Suppress progress messages.');
});
