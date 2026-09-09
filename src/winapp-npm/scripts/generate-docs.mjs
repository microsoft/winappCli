#!/usr/bin/env node
/**
 * generate-docs.mjs
 *
 * Auto-generates `docs/npm-usage.md` from the TypeScript source code using
 * the TypeScript Compiler API.  Extracts all publicly exported functions,
 * interfaces, and type aliases from `src/index.ts` (and re-exported modules)
 * together with their JSDoc comments and full type information.
 *
 * Usage:
 *   node scripts/generate-docs.mjs          # generate docs/npm-usage.md
 *   node scripts/generate-docs.mjs --check  # exit 1 if the file would change
 */
import { createRequire } from 'node:module';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { tableCell } from './markdown-table-cell.mjs';

const require = createRequire(import.meta.url);
const ts = require('typescript');

// ---------------------------------------------------------------------------
// CLI arg parsing
// ---------------------------------------------------------------------------
const cliArgs = process.argv.slice(2);
const checkOnly = cliArgs.includes('--check');

const SCRIPT_DIR = import.meta.dirname;
const NPM_ROOT = resolve(SCRIPT_DIR, '..');
const OUTPUT = resolve(NPM_ROOT, '../../docs/npm-usage.md');

// ---------------------------------------------------------------------------
// CommonOptions properties — documented once, skipped in per-function tables
// ---------------------------------------------------------------------------
const COMMON_OPTION_NAMES = new Set(['quiet', 'verbose', 'cwd', 'signal', 'workflowId']);

// Rendered wherever a section says which inherited options also apply.
const COMMON_OPTION_NOTE = '(`quiet`, `verbose`, `cwd`, `signal`, `workflowId`)';

// ---------------------------------------------------------------------------
// Create TypeScript program from tsconfig.json
// ---------------------------------------------------------------------------
const configPath = ts.findConfigFile(NPM_ROOT, ts.sys.fileExists, 'tsconfig.json');
if (!configPath) throw new Error('tsconfig.json not found');
const configFile = ts.readConfigFile(configPath, ts.sys.readFile);
const parsed = ts.parseJsonConfigFileContent(configFile.config, ts.sys, NPM_ROOT);
const program = ts.createProgram(parsed.fileNames, parsed.options);
const checker = program.getTypeChecker();

// Get module exports of src/index.ts
const indexPath = resolve(NPM_ROOT, 'src/index.ts');
const indexSource = program.getSourceFile(indexPath);
if (!indexSource) throw new Error('Could not find src/index.ts in program');
const moduleSym = checker.getSymbolAtLocation(indexSource);
if (!moduleSym) throw new Error('Could not resolve module symbol for index.ts');
const allExports = checker.getExportsOfModule(moduleSym);

// ---------------------------------------------------------------------------
// Categorize exports
// ---------------------------------------------------------------------------
function resolveSymbol(sym) {
  return sym.flags & ts.SymbolFlags.Alias ? checker.getAliasedSymbol(sym) : sym;
}

function getSourcePath(sym) {
  const decl = sym.declarations?.[0];
  return decl ? decl.getSourceFile().fileName : '';
}

function isExternal(sym) {
  return getSourcePath(sym).includes('node_modules');
}

const cliCommandFns = []; // functions from winapp-commands.ts
const utilityFns = [];    // other functions
const typeExports = [];   // interfaces & type aliases

for (const sym of allExports) {
  const name = sym.getName();
  if (name === 'default') continue;
  // Skip internal helpers — underscore-prefixed names are not part of the public API.
  if (name.startsWith('_')) continue;

  const resolved = resolveSymbol(sym);
  const src = getSourcePath(resolved);

  if (resolved.flags & (ts.SymbolFlags.Function | ts.SymbolFlags.Method)) {
    if (src.includes('winapp-commands')) {
      cliCommandFns.push({ name, symbol: resolved });
    } else {
      utilityFns.push({ name, symbol: resolved });
    }
  } else if (resolved.flags & (ts.SymbolFlags.Interface | ts.SymbolFlags.TypeAlias)) {
    typeExports.push({ name, symbol: resolved, external: isExternal(resolved) });
  }
}

// ---------------------------------------------------------------------------
// Extraction helpers
// ---------------------------------------------------------------------------
function getDoc(sym) {
  return ts.displayPartsToString(sym.getDocumentationComment(checker)).trim();
}

function getJsDocTags(sym) {
  return sym.getJsDocTags(checker) || [];
}

/** Extract the description text from a @param JSDoc tag */
function paramTagDesc(tag) {
  const text = ts.displayPartsToString(tag.text || []);
  // text is typically "paramName - description"
  const m = text.match(/^\w+\s*[-–—]\s*/);
  if (m) return text.slice(m[0].length).trim();
  const sp = text.indexOf(' ');
  return sp !== -1 ? text.slice(sp + 1).trim() : '';
}

function isOptionalDecl(decl) {
  if (!decl) return false;
  if (ts.isPropertySignature(decl) || ts.isPropertyDeclaration(decl)) return !!decl.questionToken;
  if (ts.isParameter(decl)) return !!decl.questionToken || !!decl.initializer;
  return false;
}

function getSymType(sym) {
  const decl = sym.valueDeclaration || sym.declarations?.[0];
  return checker.getTypeOfSymbolAtLocation(sym, decl || indexSource);
}

function typeStr(type) {
  return checker.typeToString(type, undefined, ts.TypeFormatFlags.NoTruncation);
}

// ---------------------------------------------------------------------------
// Emit a function section
// ---------------------------------------------------------------------------
function emitFunction(lines, name, symbol, isCLIWrapper) {
  const type = getSymType(symbol);
  const sigs = type.getCallSignatures();
  if (sigs.length === 0) return;

  const sig = sigs[0];
  const doc = getDoc(symbol);
  const retType = typeStr(sig.getReturnType());
  const tags = getJsDocTags(symbol);

  lines.push(`### \`${name}()\``);
  lines.push('');
  if (doc) {
    lines.push(doc);
    lines.push('');
  }

  // --- Signature ---
  const paramSegments = sig.parameters.map((p) => {
    const pType = typeStr(getSymType(p));
    const decl = p.valueDeclaration;
    const opt = isOptionalDecl(decl);
    return `${p.getName()}${opt ? '?' : ''}: ${pType}`;
  });
  lines.push('```typescript');
  lines.push(`function ${name}(${paramSegments.join(', ')}): ${retType}`);
  lines.push('```');
  lines.push('');

  // --- Parameters / options ---
  if (isCLIWrapper) {
    // Single options-bag pattern: expand interface props, skip common ones
    const param = sig.parameters[0];
    if (param) {
      const pType = getSymType(param);
      const props = pType.getProperties();
      const filtered = props.filter((p) => !COMMON_OPTION_NAMES.has(p.getName()));

      if (filtered.length === 0) {
        lines.push('*Inherits [CommonOptions](#commonoptions) only.*');
        lines.push('');
      } else {
        lines.push('**Options:**');
        lines.push('');
        lines.push('| Property | Type | Required | Description |');
        lines.push('|----------|------|----------|-------------|');
        for (const prop of filtered) {
          const propDecl = prop.valueDeclaration || prop.declarations?.[0];
          let pt = typeStr(getSymType(prop));
          pt = pt.replace(/\\/g, '\\\\').replace(/\|/g, '\\|');
          const pdoc = tableCell(getDoc(prop));
          const opt = isOptionalDecl(propDecl);
          lines.push(`| \`${prop.getName()}\` | \`${pt}\` | ${opt ? 'No' : 'Yes'} | ${pdoc} |`);
        }
        lines.push('');
        lines.push(`*Also accepts [CommonOptions](#commonoptions) ${COMMON_OPTION_NOTE}.*`);
        lines.push('');
      }
    }
  } else if (sig.parameters.length > 0) {
    // Multi-parameter utility function: show a Parameters table
    const paramTags = tags.filter((t) => t.name === 'param');
    lines.push('**Parameters:**');
    lines.push('');
    lines.push('| Parameter | Type | Required | Description |');
    lines.push('|-----------|------|----------|-------------|');
    for (const param of sig.parameters) {
      const pType = typeStr(getSymType(param));
      const decl = param.valueDeclaration;
      const opt = isOptionalDecl(decl);
      const pTag = paramTags.find((t) => {
        const text = ts.displayPartsToString(t.text || []);
        return text.startsWith(param.getName());
      });
      const desc = pTag ? tableCell(paramTagDesc(pTag)) : '';
      lines.push(`| \`${param.getName()}\` | \`${pType.replace(/\\/g, '\\\\').replace(/\|/g, '\\|')}\` | ${opt ? 'No' : 'Yes'} | ${desc} |`);
    }
    lines.push('');
  }

  // --- @returns tag ---
  const retTag = tags.find((t) => t.name === 'returns');
  if (retTag) {
    lines.push(`**Returns:** ${ts.displayPartsToString(retTag.text || [])}`);
    lines.push('');
  }

  // --- @example tags ---
  const exampleTags = tags.filter((t) => t.name === 'example');
  if (exampleTags.length > 0) {
    lines.push('**Example:**');
    lines.push('');
    for (const ex of exampleTags) {
      const text = ts.displayPartsToString(ex.text || []).trim();
      lines.push('```typescript');
      lines.push(text);
      lines.push('```');
      lines.push('');
    }
  }

  lines.push('---');
  lines.push('');
}

// ---------------------------------------------------------------------------
// Emit an interface / type-alias section
// ---------------------------------------------------------------------------
function emitType(lines, name, symbol, external) {
  if (external) {
    lines.push(`### \`${name}\``);
    lines.push('');
    lines.push('Re-exported from Node.js for convenience. See [Node.js docs](https://nodejs.org/api/child_process.html).');
    lines.push('');
    return;
  }

  const doc = getDoc(symbol);

  if (symbol.flags & ts.SymbolFlags.Interface) {
    const type = checker.getDeclaredTypeOfSymbol(symbol);
    const props = type.getProperties();

    lines.push(`### \`${name}\``);
    lines.push('');
    if (doc) {
      lines.push(doc);
      lines.push('');
    }

    if (props.length > 0) {
      lines.push('| Property | Type | Required | Description |');
      lines.push('|----------|------|----------|-------------|');
      for (const prop of props) {
        const propDecl = prop.valueDeclaration || prop.declarations?.[0];
        let pt = typeStr(getSymType(prop));
        pt = pt.replace(/\\/g, '\\\\').replace(/\|/g, '\\|');
        const pdoc = tableCell(getDoc(prop));
        const opt = isOptionalDecl(propDecl);
        lines.push(`| \`${prop.getName()}\` | \`${pt}\` | ${opt ? 'No' : 'Yes'} | ${pdoc} |`);
      }
      lines.push('');
    }
  } else if (symbol.flags & ts.SymbolFlags.TypeAlias) {
    // For type aliases, get the RHS from the declaration node to avoid circular display
    const decl = symbol.declarations?.[0];
    let aliasText = '';
    if (decl && ts.isTypeAliasDeclaration(decl)) {
      const rhsType = checker.getTypeAtLocation(decl.type);
      // For union types, checker.typeToString expands them properly when given the RHS node type
      aliasText = checker.typeToString(
        rhsType,
        decl,
        ts.TypeFormatFlags.NoTruncation | ts.TypeFormatFlags.InTypeAlias
      );
    } else {
      const type = checker.getDeclaredTypeOfSymbol(symbol);
      aliasText = typeStr(type);
    }

    lines.push(`### \`${name}\``);
    lines.push('');
    if (doc) {
      lines.push(doc);
      lines.push('');
    }
    lines.push('```typescript');
    lines.push(`type ${name} = ${aliasText}`);
    lines.push('```');
    lines.push('');
  }
}

// ---------------------------------------------------------------------------
// npx winapp node commands — read from docs/fragments/node-commands.md
// ---------------------------------------------------------------------------

const NODE_COMMANDS_FRAGMENT = resolve(NPM_ROOT, '../../docs/fragments/node-commands.md');

function emitNodeCommands(lines) {
  if (!existsSync(NODE_COMMANDS_FRAGMENT)) {
    console.warn(`[generate-docs] Warning: ${NODE_COMMANDS_FRAGMENT} not found — skipping node commands section.`);
    return;
  }
  const fragment = readFileSync(NODE_COMMANDS_FRAGMENT, 'utf8').trimEnd();
  lines.push(fragment);
  lines.push('');
}

// ---------------------------------------------------------------------------
// Generate the full markdown document
// ---------------------------------------------------------------------------
function generate() {
  const lines = [];
  const L = (s = '') => lines.push(s);

  L('---');
  L('ms.custom: mslearn');
  L('---');
  L('<!-- AUTO-GENERATED — DO NOT EDIT -->');
  L('<!-- Regenerate with: cd src/winapp-npm && npm run generate-docs -->');
  L();
  L('# NPM Package — Programmatic API');
  L();
  L('TypeScript/JavaScript API reference for `@microsoft/winappcli`.');
  L('Each CLI command is available as an async function that captures stdout/stderr and returns a typed result.');
  L('Helper utilities for MSIX identity, Electron debug identity, and build tools are also exported.');
  L();

  // --- Installation ---
  L('## Installation');
  L();
  L('```bash');
  L('npm install @microsoft/winappcli');
  L('```');
  L();

  // --- Quick start ---
  L('## Quick start');
  L();
  L('```typescript');
  L("import { init, packageApp, certGenerate } from '@microsoft/winappcli';");
  L();
  L('// Initialize a new project with defaults');
  L('await init({ useDefaults: true });');
  L();
  L('// Generate a dev certificate');
  L('await certGenerate({ install: true });');
  L();
  L('// Package the built app');
  L("await packageApp({ inputFolder: './dist', cert: './devcert.pfx' });");
  L('```');
  L();

  // --- Common types (always-present; documented first) ---
  L('## Common types');
  L();
  L('Every CLI command wrapper accepts an options object extending `CommonOptions` and returns `Promise<WinappResult>`.');
  L();
  const commonTypeNames = ['CommonOptions', 'WinappResult'];
  for (const tName of commonTypeNames) {
    const entry = typeExports.find((t) => t.name === tName);
    if (entry) emitType(lines, entry.name, entry.symbol, entry.external);
  }

  // --- CLI command wrappers ---
  L('## CLI command wrappers');
  L();
  L(`These functions wrap native \`winapp\` CLI commands. All accept [CommonOptions](#commonoptions) ${COMMON_OPTION_NOTE}.`);
  L();

  for (const { name, symbol } of cliCommandFns) {
    emitFunction(lines, name, symbol, true);
  }

  // --- Utility functions ---
  L('## Utility functions');
  L();

  for (const { name, symbol } of utilityFns) {
    emitFunction(lines, name, symbol, false);
  }

  // --- npx winapp node commands (CLI-only, not programmatic exports) ---
  emitNodeCommands(lines);

  // --- Types reference (everything not yet documented above) ---
  L('## Types reference');
  L();

  const skipTypes = new Set([...commonTypeNames, 'default']);
  for (const { name, symbol, external } of typeExports) {
    if (skipTypes.has(name)) continue;
    emitType(lines, name, symbol, external);
  }

  return lines.join('\n') + '\n';
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------
const output = generate();

if (checkOnly) {
  if (!existsSync(OUTPUT)) {
    console.error(`[generate-docs] ${OUTPUT} does not exist. Run without --check to generate.`);
    process.exit(1);
  }
  const existing = readFileSync(OUTPUT, 'utf8');
  if (existing !== output) {
    console.error(
      '[generate-docs] docs/npm-usage.md is out of date.\n' +
        'Run `cd src/winapp-npm && npm run generate-docs` to regenerate.'
    );
    process.exit(1);
  }
  console.log('[generate-docs] docs/npm-usage.md is up to date.');
} else {
  writeFileSync(OUTPUT, output, 'utf8');
  console.log(`[generate-docs] Generated ${OUTPUT}`);
}
