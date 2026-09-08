// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// JS-binding hooks layered around native init/restore.

import * as fs from 'fs';
import * as path from 'path';

import { CLI_NAME, parseArgs, logErrorAndExit } from '../cli-shared';
import { callWinappCli } from '../winapp-cli-utils';
import {
  getCodegenPackageVersion,
  getCodegenRuntimeDependency,
  resolveCodegenInvocation,
  supportsPackageImports,
} from './codegen-runner';
import { askBindingsKind } from './init-prompt';
import {
  ensureJsBindingsBlock,
  ensureJsBindingsImports,
  EnsureJsBindingsImportsResult,
  hasJsBindings,
} from './package-json-config';
import { packageJsonExists } from './package-json-doc';
import { RUNTIME_PACKAGE_NAME, runJsBindingsPipeline } from './orchestrator';
import { lockfileExists } from './lockfile-reader';
import { readWinappYamlPackages } from './yaml-packages-hash';
import { detectPackageManager } from './package-manager-detector';
import {
  ensureRuntimeDependency,
  formatRuntimeDependencyHint,
  isPackageDeclared,
  updateRuntimeDependency,
} from './runtime-dep-injector';
import { installPackageDependency, runPackageManagerInstall } from './runtime-installer';
import {
  resolveWorkspaceDir,
  resolveYamlPath,
  isVerbose,
  isQuiet,
  isJson,
  hasConfigOnly,
  hasAddJsBindings,
  hasUseDefaults,
  stripWrapperOnlyFlags,
  firstPositional,
} from '../cli-args';
import { assertSafeWorkspaceFile } from './path-safety';
import { evaluateGenerateBindingsPreflight } from './generate-bindings-preflight';
import { startSpinner } from './spinner';
import { startGroupedSpinner, GroupedSpinner, GroupedChildSpinner } from './grouped-spinner';

/** Indent for sub-task lines under the "Setting up JS bindings..." parent header. */
const JS_BINDINGS_INDENT = '  ';

export function makeIndentedLog(indent: string): (line: string) => void {
  return (line: string) => {
    if (line === '') {
      console.log('');
      return;
    }
    console.log(
      line
        .split('\n')
        .map((part) => (part.length > 0 ? indent + part : part))
        .join('\n')
    );
  };
}

/** Build the user-visible hint lines for an `ensureJsBindingsImports` result.
 *  Pure so `handleInit`'s hint UX is testable without spinning up a workspace. */
export function formatJsBindingsImportsHints(result: EnsureJsBindingsImportsResult): string[] {
  const hints: string[] = [];
  if (result.outcome === 'added') {
    hints.push('💡 Added "#winapp/bindings" package imports to package.json.');
  }
  for (const alias of result.diverged) {
    hints.push(
      `⚠ Existing "${alias}" alias in package.json differs from the winapp default; leaving as-is. ` +
        'Delete it and rerun `npx winapp init --add-js-bindings` to overwrite.'
    );
  }
  return hints;
}

export type JsBindingsImportsHintInput =
  { kind: 'configured'; result: EnsureJsBindingsImportsResult } | { kind: 'unsupported' };

/** Build the hint lines `handleInit` should emit for the `#winapp/bindings` imports map.
 *  Pure so quiet/json gating can be tested without mutating package.json. */
export function planJsBindingsImportsHints(input: JsBindingsImportsHintInput, opts: { quiet: boolean }): string[] {
  if (opts.quiet) {
    return [];
  }
  if (input.kind === 'unsupported') {
    return [
      '💡 Skipped "#winapp/bindings" package imports because the installed @microsoft/dynwinrt-codegen is too old. ' +
        'Upgrade with `npm i -D @microsoft/dynwinrt-codegen@latest && npx winapp init --add-js-bindings`, ' +
        "or keep using a relative path like `require('../.winapp/bindings/index.js')`.",
    ];
  }
  return formatJsBindingsImportsHints(input.result);
}

/** Run `work` under a child spinner (grouped if `ui.group` is set, standalone otherwise). */
async function runStep<T>(
  ui: {
    quiet: boolean;
    prefix: string;
    log: (line: string) => void;
    group?: GroupedSpinner;
  },
  progress: string,
  success: string,
  work: () => Promise<T> | T
): Promise<T> {
  if (ui.quiet) {
    return await work();
  }
  const spinner: GroupedChildSpinner = ui.group
    ? ui.group.addChild(progress)
    : startSpinner(progress, { prefix: ui.prefix, nonTtyLog: ui.log });
  try {
    const result = await work();
    spinner.succeed(success);
    return result;
  } catch (err) {
    spinner.fail(`${success} — failed: ${(err as Error).message}`);
    throw err;
  }
}

/** Passive `node generate-bindings`: read package config + lockfile, then run codegen. */
export async function handleGenerateBindings(args: string[]): Promise<void> {
  const options = parseArgs(args, {
    verbose: false,
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node generate-bindings [options]`);
    console.log('');
    console.log('Regenerate JS/TypeScript bindings from package.json + cached winmds');
    console.log('');
    console.log('This command will:');
    console.log('  1. Read the `winapp.jsBindings` block from package.json');
    console.log('  2. Read the cached winmd inventory from .winapp/winmds.lock.json');
    console.log('  3. Run dynwinrt-codegen into the output directory');
    console.log('');
    console.log('It only reads `winapp.jsBindings` + the cached lockfile and emits bindings —');
    console.log('it does NOT modify package.json. Run `winapp init` to opt into JS bindings');
    console.log('(it adds the `winapp.jsBindings` block and the @microsoft/dynwinrt dependency).');
    console.log('It also does NOT re-run the native restore. If you have never run');
    console.log('`winapp restore` in this workspace (so there is no winmd lockfile yet)');
    console.log('or you changed `winapp.yaml` since the last restore, run `winapp restore`');
    console.log('first, then re-run this command.');
    console.log('');
    console.log('Options:');
    console.log('  --verbose             Enable verbose codegen output (default: false)');
    console.log('  --quiet, -q           Suppress progress and informational output');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node generate-bindings`);
    console.log(`  ${CLI_NAME} node generate-bindings --verbose`);
    return;
  }

  const workspaceDir = resolveWorkspaceDir(args);
  const quiet = isQuiet(args) || isJson(args);

  // Preflight: package.json + `winapp.jsBindings` + cached restore lockfile.
  // (Schema mismatches surface later as `lockfileStale`.)
  const preflight = evaluateGenerateBindingsPreflight(workspaceDir);
  if (preflight.kind !== 'ok') {
    for (const line of preflight.messageLines) {
      console.error(line);
    }
    process.exit(1);
  }

  // Hand off to the shared pipeline (outcomes → ✅ / ❌ / ⚠).
  await runJsBindingsOrchestrator(workspaceDir, isVerbose(args), quiet, resolveYamlPath(args, workspaceDir));
}

/** Append the wrapper-specific `init` notes the native `--help` doesn't know about. */
export function printInitWrapperOnlyHelp(): void {
  // On TTY: move cursor up one line to eat the trailing blank line from native help.
  // On pipe: skip ANSI escape, accept the blank line (harmless in piped output).
  if (process.stdout.isTTY) {
    process.stdout.write('\x1B[1A\x1B[2K');
  }
  console.log(
    wrapHelpLine(
      '  --add-js-bindings                                ',
      'Generate Windows App SDK JS bindings under .winapp/bindings/ for direct JavaScript access to WinRT APIs'
    )
  );
  console.log('');
}

/**
 * Word-wrap a help description to the terminal width, indenting continuation
 * lines to align with the description start column (matching System.CommandLine).
 */
export function wrapHelpLine(prefix: string, description: string): string {
  const width = process.stdout.columns || 120;
  const descCol = prefix.length;
  const budget = width - descCol;
  if (budget < 20) return prefix + description;

  const words = description.split(' ');
  const lines: string[] = [];
  let current = '';

  for (const word of words) {
    if (current.length + word.length + 1 > budget && current.length > 0) {
      lines.push(current);
      current = word;
    } else {
      current = current ? current + ' ' + word : word;
    }
  }
  if (current) lines.push(current);

  const indent = ' '.repeat(descCol);
  return (
    prefix +
    lines[0] +
    (lines.length > 1
      ? '\n' +
        lines
          .slice(1)
          .map((l) => indent + l)
          .join('\n')
      : '')
  );
}

/**
 * Decide whether to skip the wrapper's JS bindings flow after native init.
 * Native init may auto-pick a subdirectory whose package.json we'd otherwise
 * corrupt, so we only proceed when one of these signals confirms cwd is the workspace.
 */
export function shouldSkipBindingsAfterInit(opts: {
  explicitWorkspace: boolean;
  useDefaults: boolean;
  packageJsonExistedBeforeInit: boolean;
  packageJsonExistsNow: boolean;
}): boolean {
  if (opts.explicitWorkspace) return false;
  if (opts.packageJsonExistedBeforeInit) return false;
  if (opts.useDefaults && opts.packageJsonExistsNow) return false;
  return true;
}

/** `init` hook: run native init, then optionally add and generate JS bindings. */
export async function handleInit(args: string[]): Promise<void> {
  const workspaceDir = resolveWorkspaceDir(args);
  // --json: suppress wrapper spinners/prompts so we never corrupt downstream parsers.
  const jsonMode = isJson(args);
  const quiet = isQuiet(args) || jsonMode;
  const configOnly = hasConfigOnly(args);
  const addJsBindings = hasAddJsBindings(args);
  const explicitWorkspace = firstPositional(args) !== undefined;
  const useDefaults = hasUseDefaults(args);
  const packageJsonExistedBeforeInit = packageJsonExists(workspaceDir);

  // Re-running on a configured workspace: infer the choice, don't re-prompt.
  const existingJsBindings = hasJsBindings(workspaceDir);

  // Native init runs first (its prompts finish, and we can gate on SDK setup).
  // Don't change child cwd: native resolves paths against its own cwd.
  await callWinappCli(['init', ...stripWrapperOnlyFlags(args)], { exitOnError: true });

  if (
    shouldSkipBindingsAfterInit({
      explicitWorkspace,
      useDefaults,
      packageJsonExistedBeforeInit,
      packageJsonExistsNow: packageJsonExists(workspaceDir),
    })
  ) {
    // Explicit opt-in but no package.json to write into: surface non-zero so
    // CI/automation doesn't mistake a no-op for success. Matches the hard-fail
    // for `--add-js-bindings --setup-sdks none` in cli.ts.
    if (addJsBindings) {
      console.error('❌ Cannot generate JS bindings: no package.json in this workspace.');
      process.exit(1);
    }
    if (!quiet) {
      console.log(
        '💡 JS bindings setup skipped: no package.json detected in the current directory. ' +
          'If your project is here, re-run as `npx winapp init .`; otherwise run `npx winapp restore` from the project directory.'
      );
    }
    return;
  }

  // Lockfile (from SDK winmd discovery) = winmds to bind against; --config-only defers.
  // Absent lockfile after a successful init means SDK setup was skipped — askBindingsKind
  // handles that silently for the no-opt-in path, so don't pre-empt it with a misleading
  // "different directory" message.
  const lockfilePresent = lockfileExists(workspaceDir);

  let outcome;
  try {
    outcome = await askBindingsKind({
      workspaceDir,
      argv: args,
      isInit: true,
      existingJsBindings,
      sdksReady: lockfilePresent || configOnly,
      addJsBindings,
      nonInteractive: jsonMode,
    });
  } catch (err) {
    logErrorAndExit(err);
  }

  if (outcome.silentReason && !quiet) {
    console.log(`💡 ${outcome.silentReason}`);
  }

  if (outcome.kind === 'no') {
    return;
  }

  // Persist default block for later restore/generate-bindings; skip+hint if no package.json.
  try {
    const pkgJsonPath = path.join(workspaceDir, 'package.json');
    assertSafeWorkspaceFile(workspaceDir, pkgJsonPath, 'package.json');
    if (!fs.existsSync(pkgJsonPath)) {
      if (!quiet) {
        console.warn(
          '⚠ package.json not found in this workspace. ' +
            'Run `npm init -y` (or equivalent) and then `npx winapp node generate-bindings` to enable JS bindings.'
        );
      }
      return;
    }

    // Render all JS bindings work as a grouped task (parent header + indented
    // child spinners). Disabled under --verbose (per-file codegen logs would
    // fight the live render) and --quiet.
    const verbose = isVerbose(args);
    const useGroup = !quiet && !verbose;
    const group = useGroup ? startGroupedSpinner('Setting up JS bindings...') : undefined;
    if (!useGroup && !quiet) {
      console.log('🔧 Setting up JS bindings...');
    }
    const taskLog = makeIndentedLog(JS_BINDINGS_INDENT);

    // Direct logs during an active group render get clobbered by the next
    // redraw tick, so route hints through a buffer and flush after settleGroup.
    const hintBuffer: string[] = [];
    const bufferingLog = group ? (line: string): void => void hintBuffer.push(line) : taskLog;
    const ui: ProgressUi = { quiet, prefix: JS_BINDINGS_INDENT, log: bufferingLog, group };

    let groupSettled = false;
    const settleGroup = (kind: 'success' | 'failure', message: string): void => {
      if (groupSettled) return;
      groupSettled = true;
      if (group) {
        if (kind === 'success') {
          group.succeed(message);
        } else {
          group.fail(message);
        }
        if (hintBuffer.length > 0 && !quiet) {
          for (const line of hintBuffer) {
            taskLog(line);
          }
        }
      } else if (!quiet) {
        console.log(`${kind === 'success' ? '✅' : '❌'} ${message}`);
      }
    };

    try {
      const willMutateBindings = !existingJsBindings || outcome.overwriteExistingConfig === true;
      if (willMutateBindings) {
        const progress = existingJsBindings
          ? 'Resetting "winapp.jsBindings" in package.json...'
          : 'Adding "winapp.jsBindings" to package.json...';
        const success = existingJsBindings
          ? 'Reset "winapp.jsBindings" in package.json to defaults.'
          : 'Added "winapp.jsBindings" to package.json. Edit `additionalWinmds` or `additionalRefs` to customize.';
        await runStep(ui, progress, success, () =>
          // quiet:true so the helper's own log is suppressed — the spinner owns the UI.
          ensureJsBindingsBlock(workspaceDir, {
            reset: outcome.overwriteExistingConfig === true,
            quiet: true,
          })
        );
      }

      // Package aliases require the dual CJS/ESM output introduced in preview.8.
      // Resolve codegen first so older projects keep their existing relative imports.
      await ensureCodegenInstalledForInit(workspaceDir, ui);
      let codegenVersion: string | null = null;
      try {
        codegenVersion = getCodegenPackageVersion(workspaceDir);
      } catch {
        // Installation is best-effort. Do not point package.json at output files
        // when the installed codegen version cannot be confirmed.
      }
      // Two writes on purpose: ensureJsBindingsBlock owns `winapp.jsBindings`
      // and ensureJsBindingsImports owns the `imports` map. Keeping them split
      // means the imports mutation only happens when codegen actually supports
      // the package-shaped output (checked below), and each helper stays
      // testable/reusable without the other.
      const importsHintLines = supportsPackageImports(codegenVersion)
        ? planJsBindingsImportsHints({ kind: 'configured', result: ensureJsBindingsImports(workspaceDir) }, { quiet })
        : planJsBindingsImportsHints({ kind: 'unsupported' }, { quiet });
      // Route through bufferingLog so `--verbose` (no group) logs immediately
      // and grouped mode flushes after the spinner settles.
      for (const line of importsHintLines) {
        bufferingLog(line);
      }

      // --config-only wrote no lockfile; codegen would fail. Defer to a later restore.
      if (configOnly) {
        await ensureRuntimeDependencyForInit(workspaceDir, args, ui);
        // Settle before logging hints so they don't get clobbered by the next group redraw.
        settleGroup('success', 'JS bindings configured (codegen deferred)');
        if (!quiet) {
          taskLog(
            '💡 --config-only requested; JS bindings codegen deferred. ' +
              'Run `npx winapp restore` (or `npx winapp node generate-bindings` after a restore) to generate.'
          );
        }
        return;
      }

      // Lockfile already written by native init → straight to orchestrator.
      // Guard: re-runs may infer Yes from old config even when SDK setup was skipped (no lockfile).
      if (!lockfilePresent) {
        settleGroup('success', 'JS bindings configured (SDK setup pending)');
        if (!quiet) {
          taskLog(
            '💡 Windows SDKs were not set up, so JS bindings were not generated. ' +
              'Run `npx winapp restore` then `npx winapp node generate-bindings` to generate them.'
          );
        }
        return;
      }

      // Resolve yaml against workspaceDir (native remaps --config-dir) to avoid false stale-lockfile.
      await runJsBindingsOrchestrator(
        workspaceDir,
        verbose,
        quiet,
        resolveYamlPath(args, workspaceDir),
        true,
        true,
        ui
      );
      settleGroup('success', 'JS bindings setup completed successfully');
    } catch (err) {
      settleGroup('failure', `JS bindings setup failed: ${(err as Error).message}`);
      // Tag so the outer catch knows the user already saw the message.
      try {
        (err as { _settled?: boolean })._settled = true;
      } catch {
        /* primitive throw — ignore */
      }
      throw err;
    }
  } catch (err) {
    // Path-safety / fs probe failures hit here before the inner spinner exists.
    if (!(err as { _settled?: boolean })?._settled) {
      console.error(`JS bindings setup failed: ${(err as Error).message}`);
    }
    process.exit(1);
  }
}

const CODEGEN_PACKAGE_NAME = '@microsoft/dynwinrt-codegen';

interface ProgressUi {
  quiet: boolean;
  prefix: string;
  log: (line: string) => void;
  /** When set, child sub-tasks render under this group instead of as standalone spinners. */
  group?: GroupedSpinner;
}

async function ensureCodegenInstalledForInit(workspaceDir: string, ui: ProgressUi): Promise<void> {
  // Two gates: codegen must be physically resolvable AND declared in package.json.
  // Either gap → install/declare via the package manager.
  const isDeclared = isPackageDeclared(workspaceDir, CODEGEN_PACKAGE_NAME);
  let codegenReachable = false;
  try {
    resolveCodegenInvocation(workspaceDir);
    codegenReachable = true;
  } catch {
    // Fall through and install.
  }
  if (codegenReachable && isDeclared) {
    return;
  }

  const pm = detectPackageManager(workspaceDir);
  // `latest` (not pinned) on purpose: codegen ships independently of winappcli and
  // the ABI-lockstep step then queries its `runtime-dependency` capability for the
  // matching runtime pin. Trust boundary is unchanged — `@microsoft/dynwinrt-codegen`
  // is published by the same Microsoft scope as winappcli, and the install runs via
  // the user's own pm so their `.npmrc` (registry, audit-signatures, lockfile
  // integrity) still applies. Install is gated by explicit opt-in (interactive
  // prompt or `--add-js-bindings`); users wanting a hard pin can declare the
  // package themselves in `devDependencies` (this branch then skips via `isDeclared`).
  const progress = isDeclared
    ? `Installing declared ${CODEGEN_PACKAGE_NAME} with ${pm.name}...`
    : `Installing ${CODEGEN_PACKAGE_NAME} with ${pm.name}...`;

  // Best-effort: install failures must warn but never abort (generated bindings
  // are still useful and users can install the dep manually). So we manage the
  // spinner directly instead of using runStep (which rethrows on failure).
  const spinner: GroupedChildSpinner | null = ui.quiet
    ? null
    : ui.group
      ? ui.group.addChild(progress)
      : startSpinner(progress, { prefix: ui.prefix, nonTtyLog: ui.log });

  const result = isDeclared
    ? runPackageManagerInstall(workspaceDir, pm.name)
    : installPackageDependency(workspaceDir, CODEGEN_PACKAGE_NAME, '', pm.name, 'devDependencies');

  if (result.ok) {
    const success = isDeclared
      ? `Installed ${CODEGEN_PACKAGE_NAME} (from package.json).`
      : `Added ${CODEGEN_PACKAGE_NAME} to devDependencies.`;
    spinner?.succeed(success);
  } else {
    const manualHint = isDeclared
      ? `Run \`${pm.installCommand}\` to install it locally.`
      : `Run \`${pm.name} install --save-dev ${CODEGEN_PACKAGE_NAME}\` to install it manually.`;
    spinner?.fail(`Could not auto-install ${CODEGEN_PACKAGE_NAME}: ${result.error}`);
    // Surface the actionable hint regardless of TTY so users see what to do next —
    // but stay silent under --quiet / --json so we don't pollute machine-readable stdout.
    if (!ui.quiet) {
      ui.log(`⚠ ${manualHint}`);
    }
  }
}

async function ensureRuntimeDependencyForInit(
  workspaceDir: string,
  _args: readonly string[],
  ui: ProgressUi
): Promise<void> {
  try {
    const { version } = await getCodegenRuntimeDependency(workspaceDir);
    let result = ensureRuntimeDependency(workspaceDir, RUNTIME_PACKAGE_NAME, version);

    // ABI lockstep: auto-update on mismatch (preview-to-preview is breaking).
    if (result.outcome === 'versionMismatch') {
      const existing = result.existingVersion ?? '';
      await runStep(
        ui,
        `Bumping ${RUNTIME_PACKAGE_NAME} ${existing} → ${version} (ABI lockstep)...`,
        `Bumped ${RUNTIME_PACKAGE_NAME} ${existing} → ${version} to match the installed @microsoft/dynwinrt-codegen (ABI lockstep).`,
        () => {
          updateRuntimeDependency(workspaceDir, RUNTIME_PACKAGE_NAME, version);
        }
      );
      result = { outcome: 'added', pinnedVersion: version };
    }

    if (!ui.quiet) {
      const pm = detectPackageManager(workspaceDir);
      const hint = formatRuntimeDependencyHint(
        result.outcome,
        RUNTIME_PACKAGE_NAME,
        result.pinnedVersion,
        pm.installCommand
      );
      ui.log(hint.message);
    }
  } catch (err) {
    // Stay silent under --quiet / --json so wrapper diagnostics don't leak into
    // machine-readable output (stdout JSON document / scripted callers).
    if (!ui.quiet) {
      console.warn(`⚠ Failed to ensure runtime dependency: ${(err as Error).message}`);
    }
  }
}

/**
 * `restore` intercept: run native restore unconditionally, then orchestrate
 * dynwinrt-codegen iff package.json declares `winapp.jsBindings`.
 */
export async function handleRestore(args: string[]): Promise<void> {
  const workspaceDir = resolveWorkspaceDir(args);
  // --json: suppress wrapper logs to keep stdout machine-readable.
  const quiet = isQuiet(args) || isJson(args);

  // See handleInit: do NOT set child cwd (avoids double-resolving a relative arg).
  // Strip wrapper-only flags so System.CommandLine doesn't reject the forwarded argv.
  await callWinappCli(['restore', ...stripWrapperOnlyFlags(args)], { exitOnError: true });

  if (!hasJsBindings(workspaceDir)) {
    return;
  }

  // No lockfile = native restore no-op (empty winapp.yaml); nothing to generate, skip.
  if (!lockfileExists(workspaceDir)) {
    if (!quiet) {
      console.log(
        '💡 No winmd inventory found (winapp.yaml has no packages yet), so JS bindings ' +
          'were not generated. Add packages to `winapp.yaml` and re-run `npx winapp restore`.'
      );
    }
    return;
  }

  // Stale lockfile can linger after packages removed; no packages = nothing to generate.
  // Resolve against workspaceDir, not the process cwd: `winapp restore .\my-project` restores that
  // directory's winapp.yaml, so reading cwd here found a different (or missing) file and skipped
  // binding generation after a restore that actually succeeded. Matches handleInit/handleUpdate.
  const restoreYamlPath = resolveYamlPath(args, workspaceDir);
  const yamlPackages = readWinappYamlPackages(workspaceDir, restoreYamlPath);
  if (!yamlPackages || yamlPackages.length === 0) {
    if (!quiet) {
      console.log(
        '💡 winapp.yaml has no packages, so JS bindings were not generated. ' +
          'Add packages to `winapp.yaml` and re-run `npx winapp restore`.'
      );
    }
    return;
  }

  await runJsBindingsOrchestrator(workspaceDir, isVerbose(args), quiet, restoreYamlPath);
}

/** Runs the JS bindings pipeline and translates outcomes into exit codes. */
async function runJsBindingsOrchestrator(
  workspaceDir: string,
  verbose: boolean = false,
  quiet: boolean = false,
  yamlPath?: string,
  installRuntimeDep: boolean = false,
  manageRuntimeDep: boolean = false,
  ui?: ProgressUi
): Promise<void> {
  const log = ui?.log ?? ((line: string) => console.log(line));
  const progressPrefix = ui?.prefix ?? '';
  const group = ui?.group;
  try {
    const result = await runJsBindingsPipeline({
      workspaceDir,
      verbose,
      quiet,
      yamlPath,
      installRuntimeDep,
      manageRuntimeDep,
      log,
      progressPrefix,
      group,
    });
    switch (result.outcome) {
      case 'completed':
        if (!quiet && !result.uiAlreadyAnnounced) {
          log(`✅ ${result.message}`);
        }
        return;
      case 'noJsBindings':
        // Silent — caller already vetted that jsBindings is configured.
        return;
      case 'lockfileMissing':
      case 'lockfileStale':
        console.error(`❌ ${result.message}`);
        process.exit(1);
        break;
      case 'noWinmdsToEmit':
        // Warning surfaces even with --quiet so users see actionable signals.
        console.warn(`⚠ ${result.message}`);
        return;
    }
  } catch (err) {
    logErrorAndExit(err);
  }
}
