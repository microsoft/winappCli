#!/usr/bin/env node
/**
 * generate-commands.mjs
 *
 * Auto-generates `src/winapp-commands.ts` from the live CLI schema.
 *
 * Usage:
 *   node scripts/generate-commands.mjs              # uses built CLI binary
 *   node scripts/generate-commands.mjs --check       # exit 1 if file would change
 *   node scripts/generate-commands.mjs --schema path  # use a specific schema JSON file
 */
import { execSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';

// ---------------------------------------------------------------------------
// CLI arg parsing
// ---------------------------------------------------------------------------
const args = process.argv.slice(2);
const checkOnly = args.includes('--check');
const schemaIdx = args.indexOf('--schema');
const schemaOverride = schemaIdx !== -1 ? args[schemaIdx + 1] : null;

const SCRIPT_DIR = import.meta.dirname;
const NPM_ROOT = resolve(SCRIPT_DIR, '..');
const OUTPUT = resolve(NPM_ROOT, 'src/winapp-commands.ts');

// ---------------------------------------------------------------------------
// Schema loading (same candidates as winapp-cli-utils.ts)
// ---------------------------------------------------------------------------
function loadSchema() {
  if (schemaOverride) {
    return JSON.parse(readFileSync(resolve(schemaOverride), 'utf8'));
  }

  const arch = process.arch === 'arm64' ? 'win-arm64' : 'win-x64';
  const candidates = [
    join(NPM_ROOT, `bin/${arch}/winapp.exe`),
    // Repo-root artifacts (built by scripts/build-cli.ps1)
    resolve(NPM_ROOT, `../../artifacts/cli/${arch}/winapp.exe`),
  ];
  const cliPath = candidates.find((p) => existsSync(p));

  if (cliPath) {
    const raw = execSync(`"${cliPath}" --cli-schema`, { encoding: 'utf8' });
    return JSON.parse(raw);
  }

  // Fallback: checked-in schema
  const fallback = resolve(NPM_ROOT, '../../docs/cli-schema.json');
  if (existsSync(fallback)) {
    return JSON.parse(readFileSync(fallback, 'utf8'));
  }

  throw new Error(
    'Cannot locate winapp CLI binary or docs/cli-schema.json.\n' +
      'Build the CLI first (scripts/build-cli.ps1) or ensure docs/cli-schema.json exists.'
  );
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** `--kebab-case` → `camelCase` */
function kebabToCamel(s) {
  return s.replace(/^-+/, '').replace(/-([a-z])/g, (_, c) => c.toUpperCase());
}

/** `kebab-case` → `PascalCase` */
function kebabToPascal(s) {
  const cc = kebabToCamel(s);
  return cc.charAt(0).toUpperCase() + cc.slice(1);
}

/** Clean up CLI description for JSDoc (single line, no trailing period). */
function cleanDesc(desc) {
  if (!desc) return '';
  return desc.replace(/\r?\n/g, ' ').replace(/\s+/g, ' ').trim();
}

const COMMON_OPTIONS = new Set(['--quiet', '--verbose', '--help']);
const TS_RESERVED = new Set(['package', 'default', 'export', 'import', 'class', 'function', 'return', 'delete', 'new']);

/**
 * Deprecated positional-argument property aliases, keyed by command path.
 *
 * When a CLI positional argument is renamed, the old npm SDK property name is a
 * breaking change for existing JS/TS callers. To avoid that, list the old name
 * here: the generator emits it as an optional, `@deprecated` field and resolves
 * `canonicalProp ?? aliasProp` when forwarding to the CLI, so old callers keep
 * working while `--help`/the schema use the new name.
 *
 * Map shape: `{ '<cmd path>': { '<canonicalProp>': '<deprecatedAliasProp>' } }`.
 */
const DEPRECATED_ARG_ALIASES = {
  // `run`'s positional was renamed from `input-folder` to `input` (it accepts a
  // .csproj/.sln/folder, not just a folder). Keep `inputFolder` working.
  run: { input: 'inputFolder' },
};

/**
 * Nullable enum types — strip `System.Nullable<...>` wrapper.
 */
const NULLABLE_ENUM_RE = /^System\.Nullable<(.+)>$/;

/** Collect all union types actually used across all commands. */
const usedUnions = new Map(); // tsName → { values, tsName }

/**
 * Derive a TypeScript union type name from the .NET valueType string.
 * e.g. "WinApp.Cli.Models.IfExists" → "IfExists"
 *      "System.Nullable<WinApp.Cli.Models.IfExists>" → "IfExists"
 */
function deriveUnionName(valueType) {
  let key = valueType;
  const nullableMatch = key.match(NULLABLE_ENUM_RE);
  if (nullableMatch) key = nullableMatch[1];
  // Extract short class name (last segment after '.')
  const lastDot = key.lastIndexOf('.');
  return lastDot >= 0 ? key.slice(lastDot + 1) : key;
}

/** Map a .NET value type to a TypeScript type string. */
function tsType(valueType, helpName) {
  if (!valueType) return 'string';
  if (valueType.includes('Boolean')) return 'boolean';
  if (valueType.includes('Int32') || valueType.includes('Int64') || valueType.includes('Double') || valueType.includes('Single')) return 'number';

  // If helpName has pipe-separated values, it's an enum — derive a named union type
  if (helpName && helpName.includes('|')) {
    const vals = helpName.split('|').map((v) => v.trim()).filter(Boolean);
    if (vals.length >= 2) {
      const tsName = deriveUnionName(valueType);
      usedUnions.set(tsName, { tsName, values: vals });
      return tsName;
    }
  }

  return 'string';
}

/** Whether a boolean option is a flag (arity max 0 or 1 with default false). */
function isBoolFlag(opt) {
  return tsType(opt.valueType, opt.helpName) === 'boolean';
}

/** Whether a positional argument is variadic (accepts multiple values). */
function isVariadicArg(argDef) {
  // Array types (e.g., DirectoryInfo[], String[])
  if (argDef.valueType && argDef.valueType.endsWith('[]')) return true;
  // Arity with no maximum or maximum > 1
  const arity = argDef.arity;
  if (arity && arity.maximum === undefined) return true;
  if (arity && arity.maximum > 1) return true;
  return false;
}

/**
 * Whether a named option accepts multiple values (a C# Option<T[]>, e.g. run's repeatable
 * `--property`). Keyed strictly on an array value type so single-valued options with an
 * open-ended arity aren't misclassified.
 */
function isVariadicOption(optDef) {
  return !!(optDef.valueType && optDef.valueType.endsWith('[]'));
}

/**
 * Commands that act as pass-throughs (arbitrary extra args forwarded to
 * an underlying tool). We add a `string[]` property for the extra args.
 */
const PASSTHROUGH_COMMANDS = {
  tool: { propName: 'toolArgs', description: "Arguments to pass to the SDK tool, e.g. ['makeappx', 'pack', '/d', './folder', '/p', './out.msix'].", separator: ' -- ' },
  store: { propName: 'storeArgs', description: 'Arguments to pass through to the Microsoft Store Developer CLI.', separator: '' },
  run: { propName: 'appArgs', description: 'Arguments to pass to the launched application (forwarded after --).', separator: ' -- ' },
};

// ---------------------------------------------------------------------------
// Flatten schema into leaf commands
// ---------------------------------------------------------------------------
function flattenCommands(node, parentPath = []) {
  const results = [];
  const subs = node.subcommands || {};

  for (const [name, cmd] of Object.entries(subs)) {
    if (cmd.hidden) continue;
    const cmdPath = [...parentPath, name];

    if (cmd.subcommands && Object.keys(cmd.subcommands).length > 0) {
      results.push(...flattenCommands(cmd, cmdPath));
    } else {
      results.push({ path: cmdPath, cmd });
    }
  }
  return results;
}

// ---------------------------------------------------------------------------
// Generate TS source
// ---------------------------------------------------------------------------
function generate(schema) {
  const commands = flattenCommands(schema);
  const lines = [];
  const L = (s = '') => lines.push(s);

  // -- header
  L('/**');
  L(' * AUTO-GENERATED — DO NOT EDIT');
  L(' *');
  L(' * Regenerate with:  npm run generate-commands');
  L(` * Source schema version: ${schema.version || 'unknown'}`);
  L(' *');
  L(' * Programmatic wrappers for all winapp CLI commands.');
  L(' * Each function builds the CLI arguments, invokes the native CLI,');
  L(' * and returns a typed result with captured stdout/stderr.');
  L(' */');
  L("import {");
  L("  callWinappCliCapture,");
  L("  CallWinappCliCaptureOptions,");
  L("  CallWinappCliCaptureResult,");
  L("} from './winapp-cli-utils';");
  L();

  // -- pre-scan all commands to collect used union types
  for (const { cmd } of commands) {
    for (const optDef of Object.values(cmd.options || {})) {
      tsType(optDef.valueType, optDef.helpName);
    }
  }

  // -- shared types
  L('// ---------------------------------------------------------------------------');
  L('// Shared / common types');
  L('// ---------------------------------------------------------------------------');
  L();

  // Emit discovered union types
  for (const [name, { values }] of usedUnions) {
    L(`/** ${name} values. */`);
    L(`export type ${name} = ${values.map((v) => `'${v}'`).join(' | ')};`);
    L();
  }

  L('/** Base options shared by most commands. */');
  L('export interface CommonOptions {');
  L('  /** Suppress progress messages. */');
  L('  quiet?: boolean;');
  L('  /** Enable verbose output. */');
  L('  verbose?: boolean;');
  L('  /** Working directory for the CLI process (defaults to process.cwd()). */');
  L('  cwd?: string;');
  L('  /**');
  L('   * Cancels the whole native invocation, not just a wait for the shared desktop.');
  L('   *');
  L('   * `winapp ui` commands take cooperative turns on the desktop, so a command may wait for another');
  L('   * workflow to finish. Aborting force-terminates the child on Windows; the CLI\'s own cleanup may');
  L('   * not run, but Windows releases its coordination handles and deletes its participant lease, and');
  L('   * other processes reclaim the queue entry. If the abort lands after the command acquired the');
  L('   * desktop, UI side effects may already have happened, and aborting an active recording can leave');
  L('   * partial output. Rejects with an `AbortError`.');
  L('   */');
  L('  signal?: AbortSignal;');
  L('  /**');
  L('   * Groups this call with other `winapp ui` calls passing the same value into one logical workflow.');
  L('   *');
  L('   * Collision arbitration is always on — every desktop-sensitive `winapp ui` command takes a turn');
  L('   * whether or not this is set. A workflow id adds *continuity*: calls sharing one keep the desktop');
  L('   * reserved between invocations for a short idle grace, may overlap with each other (a recording and');
  L('   * the clicks it is recording), and are never interleaved with another workflow\'s input. Without it,');
  L('   * each call is a self-contained one-shot that releases the desktop as soon as it finishes.');
  L('   *');
  L('   * Applied to the spawned child process only; `process.env` is never modified.');
  L('   */');
  L('  workflowId?: string;');
  L('}');
  L();
  L('/** Result returned by every command wrapper. */');
  L('export interface WinappResult {');
  L('  /** Process exit code (always 0 on success – non-zero throws). */');
  L('  exitCode: number;');
  L('  /** Captured standard output. */');
  L('  stdout: string;');
  L('  /** Captured standard error. */');
  L('  stderr: string;');
  L('}');
  L();

  // -- helpers
  L('// ---------------------------------------------------------------------------');
  L('// Helpers');
  L('// ---------------------------------------------------------------------------');
  L();
  L('function pushCommon(args: string[], opts: CommonOptions): void {');
  L('  // Insert global flags before any "--" passthrough separator so winapp consumes them rather');
  L('  // than forwarding them to the launched app/tool (e.g. run ... -- appArgs).');
  L('  const flags: string[] = [];');
  L("  if (opts.quiet) flags.push('--quiet');");
  L("  if (opts.verbose) flags.push('--verbose');");
  L('  if (flags.length === 0) return;');
  L("  const sep = args.indexOf('--');");
  L('  if (sep === -1) args.push(...flags);');
  L('  else args.splice(sep, 0, ...flags);');
  L('}');
  L();
  L('function captureOpts(opts: CommonOptions): CallWinappCliCaptureOptions {');
  L('  const result: CallWinappCliCaptureOptions = {};');
  L('  if (opts.cwd) result.cwd = opts.cwd;');
  L('  if (opts.signal) result.signal = opts.signal;');
  L('  if (opts.workflowId !== undefined) result.workflowId = opts.workflowId;');
  L('  return result;');
  L('}');
  L();
  L('async function execCommand(args: string[], opts: CommonOptions): Promise<WinappResult> {');
  L('  pushCommon(args, opts);');
  L('  const result: CallWinappCliCaptureResult = await callWinappCliCapture(args, captureOpts(opts));');
  L('  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };');
  L('}');

  // -- per-command wrappers
  for (const { path: cmdPath, cmd } of commands) {
    const cmdPathStr = cmdPath.join(' ');
    const fnName = getFunctionName(cmdPath);
    const ifaceName = kebabToPascal(cmdPath.join('-')) + 'Options';

    // Check for passthrough command
    const passthrough = PASSTHROUGH_COMMANDS[cmdPath.join(' ')] || null;

    // Collect non-common options
    const opts = [];
    for (const [optName, optDef] of Object.entries(cmd.options || {})) {
      if (COMMON_OPTIONS.has(optName)) continue;
      // Skip if all aliases are common
      if (optDef.aliases?.every((a) => COMMON_OPTIONS.has(a))) continue;
      opts.push({ cliName: optName, def: optDef, propName: kebabToCamel(optName) });
    }

    // Collect arguments (positional)
    const argAliases = DEPRECATED_ARG_ALIASES[cmdPathStr] || {};
    const positionalArgs = [];
    for (const [argName, argDef] of Object.entries(cmd.arguments || {})) {
      const propName = kebabToCamel(argName);
      // The passthrough arg (e.g. run's app-args) is emitted after '--' below, not as a positional.
      if (passthrough && propName === passthrough.propName) continue;
      positionalArgs.push({ cliName: argName, def: argDef, propName, alias: argAliases[propName] || null });
    }
    // Sort by order
    positionalArgs.sort((a, b) => (a.def.order ?? 0) - (b.def.order ?? 0));

    // Determine which positional args are required (arity minimum >= 1)
    const hasRequiredArgs = positionalArgs.some((a) => a.def.arity?.minimum >= 1);

    L();
    L('// ---------------------------------------------------------------------------');
    L(`// ${cmdPathStr}`);
    L('// ---------------------------------------------------------------------------');
    L();

    // --- Options interface ---
    L(`export interface ${ifaceName} extends CommonOptions {`);
    // positional args first
    for (const arg of positionalArgs) {
      const required = arg.def.arity?.minimum >= 1;
      const variadic = isVariadicArg(arg.def);
      const type = variadic ? 'string | string[]' : tsType(arg.def.valueType);
      L(`  /** ${cleanDesc(arg.def.description)} */`);
      L(`  ${arg.propName}${required ? '' : '?'}: ${type};`);
      if (arg.alias) {
        L(`  /** @deprecated Use \`${arg.propName}\` instead. Retained for backward compatibility. */`);
        L(`  ${arg.alias}?: ${type};`);
      }
    }
    // then named options
    for (const opt of opts) {
      const tp = isVariadicOption(opt.def) ? 'string | string[]' : tsType(opt.def.valueType, opt.def.helpName);
      L(`  /** ${cleanDesc(opt.def.description)} */`);
      L(`  ${opt.propName}?: ${tp};`);
    }
    // passthrough args property
    if (passthrough) {
      L(`  /** ${passthrough.description} */`);
      // Accept a single string OR an array. A bare string is normalized to a one-element
      // array before emission (below) so it isn't spread into individual characters — this
      // preserves the string form callers relied on before `run` moved onto this path.
      L(`  ${passthrough.propName}?: string | string[];`);
    }
    L('}');
    L();

    // --- Wrapper function ---
    // Internal functions (underscore-prefixed) only need the Options interface exported
    // for type-import by hand-maintained guard wrappers (e.g. ui-record-guard.ts).
    // The function body is intentionally skipped — guards call the CLI directly so there
    // is no unused private function to generate or suppress.
    if (fnName.startsWith('_')) {
      L(`// ${fnName}: options interface exported above; function body omitted — use the`);
      L(`//   public guarded wrapper (e.g. uiRecord from ui-record-guard.ts) instead.`);
      continue;
    }

    const defaultArg = hasRequiredArgs ? '' : ' = {}';
    L('/**');
    L(` * ${cleanDesc(cmd.description)}`);
    L(' */');
    L(`export async function ${fnName}(options: ${ifaceName}${defaultArg}): Promise<WinappResult> {`);

    // Build args array
    L(`  const args: string[] = [${cmdPath.map((p) => `'${p}'`).join(', ')}];`);

    // Positional args
    for (const arg of positionalArgs) {
      const required = arg.def.arity?.minimum >= 1;
      const variadic = isVariadicArg(arg.def);
      // When a deprecated alias exists, prefer the canonical property but fall
      // back to the alias so pre-rename callers keep working. Hoist to a local so
      // TypeScript can narrow it (a re-evaluated `??` expression is not narrowed).
      let accessor = `options.${arg.propName}`;
      if (arg.alias) {
        accessor = `${arg.propName}Value`;
        L(`  const ${accessor} = options.${arg.propName} ?? options.${arg.alias};`);
      }
      if (variadic) {
        if (required) {
          L(`  const ${arg.propName}Arr = Array.isArray(${accessor}) ? ${accessor} : [${accessor}];`);
          L(`  args.push(...${arg.propName}Arr);`);
        } else {
          L(`  if (${accessor}) {`);
          L(`    const ${arg.propName}Arr = Array.isArray(${accessor}) ? ${accessor} : [${accessor}];`);
          L(`    args.push(...${arg.propName}Arr);`);
          L('  }');
        }
      } else if (required) {
        L(`  args.push(${accessor});`);
      } else {
        L(`  if (${accessor}) args.push(${accessor});`);
      }
    }

    // Named options
    for (const opt of opts) {
      if (isBoolFlag(opt.def)) {
        L(`  if (options.${opt.propName}) args.push('${opt.cliName}');`);
      } else if (isVariadicOption(opt.def)) {
        // Repeatable option: emit one flag+value pair per element (e.g. --property A=1 --property B=2, or --id x --id y).
        L(`  if (options.${opt.propName}) {`);
        L(`    const ${opt.propName}Arr = Array.isArray(options.${opt.propName}) ? options.${opt.propName} : [options.${opt.propName}];`);
        L(`    for (const v of ${opt.propName}Arr) args.push('${opt.cliName}', v);`);
        L('  }');
      } else if (tsType(opt.def.valueType) === 'number') {
        L(`  if (options.${opt.propName} !== undefined) args.push('${opt.cliName}', options.${opt.propName}.toString());`);
      } else {
        L(`  if (options.${opt.propName}) args.push('${opt.cliName}', options.${opt.propName});`);
      }
    }

    // Passthrough args. Normalize a single string to a one-element array so a bare-string
    // argument is forwarded as one token, not spread into individual characters.
    if (passthrough) {
      L(`  if (options.${passthrough.propName} !== undefined) {`);
      L(`    const ${passthrough.propName}Arr = Array.isArray(options.${passthrough.propName}) ? options.${passthrough.propName} : [options.${passthrough.propName}];`);
      if (passthrough.separator === ' -- ') {
        L(`    if (${passthrough.propName}Arr.length > 0) {`);
        L(`      args.push('--', ...${passthrough.propName}Arr);`);
        L('    }');
      } else {
        L(`    args.push(...${passthrough.propName}Arr);`);
      }
      L('  }');
    }

    L('  return execCommand(args, options);');
    L('}');
  }

  return lines.join('\n') + '\n';
}

// ---------------------------------------------------------------------------
// Function naming: special cases then fallback to camelCase of path
// ---------------------------------------------------------------------------
const FN_NAME_OVERRIDES = {
  'package': 'packageApp', // `package` is a TS reserved-ish word
  'ui record': '_uiRecordGenerated', // the public uiRecord is the guarded wrapper in ui-record-guard.ts
};

function getFunctionName(cmdPath) {
  const key = cmdPath.join(' ');
  if (FN_NAME_OVERRIDES[key]) return FN_NAME_OVERRIDES[key];

  // e.g. ['cert', 'generate'] → 'certGenerate'
  const name = cmdPath.map((p, i) => (i === 0 ? kebabToCamel(p) : kebabToPascal(p))).join('');
  return TS_RESERVED.has(name) ? name + 'Command' : name;
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------
const schema = loadSchema();
const source = generate(schema);

if (checkOnly) {
  if (!existsSync(OUTPUT)) {
    console.error(`[generate-commands] ${OUTPUT} does not exist. Run without --check to generate it.`);
    process.exit(1);
  }
  const existing = readFileSync(OUTPUT, 'utf8');
  if (existing !== source) {
    console.error(
      '[generate-commands] src/winapp-commands.ts is out of date.\n' +
        'Run `npm run generate-commands` to regenerate it.'
    );
    process.exit(1);
  }
  console.log('[generate-commands] src/winapp-commands.ts is up to date.');
} else {
  writeFileSync(OUTPUT, source, 'utf8');
  console.log(`[generate-commands] Generated ${OUTPUT}`);
}
