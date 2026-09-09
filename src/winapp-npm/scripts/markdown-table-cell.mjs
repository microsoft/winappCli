// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Markdown table-cell escaping for the docs generator.
 *
 * Kept in its own module so it can be unit-tested directly: importing generate-docs.mjs would run
 * the whole generator, which builds a TypeScript program and rewrites docs/npm-usage.md.
 */

/**
 * Make arbitrary JSDoc text safe for a single Markdown table cell.
 *
 * Two separate hazards, both of which have broken this file's tables before:
 *
 * - A multi-paragraph comment (such as `CommonOptions.signal`) ends the row at its first newline and
 *   dumps the remainder as body text.
 * - A literal `|` splits the row into extra columns.
 *
 * Backslashes are escaped *first*. Escaping only the pipe is incomplete: text containing `\|` would
 * become `\\|`, which Markdown renders as a literal backslash followed by an unescaped pipe — the
 * exact breakage the pipe escaping exists to prevent. A trailing lone backslash would likewise
 * escape the row's own closing delimiter.
 *
 * This is for plain Markdown text. Values rendered inside a code span need different treatment,
 * because a code span does not process backslash escapes and would show the doubled backslashes.
 *
 * @param {string | undefined | null} text Raw documentation text.
 * @returns {string} A single-line, table-safe cell value.
 */
export function tableCell(text) {
  if (!text) return '';
  return text
    .replace(/\\/g, '\\\\')
    .replace(/\|/g, '\\|')
    .replace(/\r?\n\s*\r?\n/g, '<br><br>')
    .replace(/\r?\n\s*/g, ' ')
    .trim();
}
