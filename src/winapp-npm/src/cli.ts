#!/usr/bin/env node

import { generateCppAddonFiles } from './cpp-addon-utils';
import { generateCsAddonFiles } from './cs-addon-utils';
import { addElectronDebugIdentity, clearElectronDebugIdentity } from './msix-utils';
import { getWinappCliPath, callWinappCli, WINAPP_CLI_CALLER_VALUE } from './winapp-cli-utils';
import { spawn } from 'child_process';
import * as fs from 'fs';

// CLI name - change this to rebrand the tool
const CLI_NAME = 'winapp';

// Commands that should be handled by Node.js (everything else goes to winapp-cli)
const NODE_ONLY_COMMANDS = new Set(['node']);

interface ParsedArgs {
  help?: boolean;
  name?: string;
  template?: string;
  verbose?: boolean;
  [key: string]: string | boolean | undefined;
}

interface PackageJson {
  name: string;
  version: string;
  description?: string;
}

/**
 * Main CLI entry point for winapp package
 */
export async function main(): Promise<void> {
  const args = process.argv.slice(2);

  if (args.length === 0) {
    await showCombinedHelp();
    process.exit(1);
  }

  const command = args[0];
  const commandArgs = args.slice(1);

  try {
    // Handle help/version specially to show combined info
    if (['help', '--help', '-h'].includes(command)) {
      await showCombinedHelp();
      return;
    }

    if (['version', '--version', '-v'].includes(command)) {
      await showVersion();
      return;
    }

    // Route Node.js-only commands to local handlers
    if (NODE_ONLY_COMMANDS.has(command)) {
      await handleNodeCommand(command, commandArgs);
      return;
    }

    // Route everything else to winapp-cli
    await callWinappCli(args, { exitOnError: true });
  } catch (error) {
    const err = error as Error;
    console.error(`Error: ${err.message}`);
    process.exit(1);
  }
}

async function handleNodeCommand(command: string, args: string[]): Promise<void> {
  switch (command) {
    case 'node':
      await handleNode(args);
      break;

    default:
      console.error(`Unknown Node.js command: ${command}`);
      process.exit(1);
  }
}

function getPackageJson(): PackageJson {
  const packageJsonPath = require.resolve('../package.json');
  return JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
}

async function showCombinedHelp(): Promise<void> {
  const packageJson = getPackageJson();

  console.log(`${packageJson.name} v${packageJson.version}`);
  console.log(packageJson.description);
  console.log('');

  // Try to get help from winapp-cli first
  try {
    const winappCliPath = getWinappCliPath();
    await new Promise<void>((resolve) => {
      const child = spawn(winappCliPath, ['--help'], {
        stdio: 'inherit',
        shell: false,
        env: {
          ...process.env,
          WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE,
        },
      });

      child.on('close', () => {
        resolve();
      });

      child.on('error', () => {
        // If winapp-cli is not available, continue without showing fallback help
        resolve();
      });
    });
  } catch {
    // Continue without showing fallback help if winapp-cli is not available
  }

  // Add Node.js-specific commands
  console.log('');
  console.log('Node.js Extensions:');
  console.log('  node <subcommand>         Node.js-specific commands');
  console.log('');
  console.log('Node.js Subcommands:');
  console.log('  node create-addon         Generate native addon files for Electron');
  console.log('  node add-electron-debug-identity  Add package identity to Electron debug process');
  console.log('  node clear-electron-debug-identity  Remove package identity from Electron debug process');
  console.log('');
  console.log('Examples:');
  console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
  console.log(`  ${CLI_NAME} node create-addon --template cs --name myAddon`);
  console.log(`  ${CLI_NAME} node add-electron-debug-identity`);
  console.log(`  ${CLI_NAME} node clear-electron-debug-identity`);
}

async function showVersion(): Promise<void> {
  const packageJson = getPackageJson();

  console.log(`${packageJson.description || 'Windows App Development CLI'}`);
  console.log('');
  console.log(`Node.js Package: ${packageJson.name} v${packageJson.version}`);

  // Try to get version from native CLI
  try {
    const winappCliPath = getWinappCliPath();

    if (!fs.existsSync(winappCliPath)) {
      console.log('Native CLI: Not available (executable not found)');
      return;
    }

    console.log('Native CLI:');

    await new Promise<void>((resolve) => {
      const child = spawn(winappCliPath, ['--version'], {
        stdio: 'inherit',
        shell: false,
        env: {
          ...process.env,
          WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE,
        },
      });

      child.on('close', (code) => {
        if (code !== 0) {
          console.log('  (version command failed)');
        }
        resolve();
      });

      child.on('error', () => {
        console.log('  Not available (execution failed)');
        resolve();
      });
    });
  } catch {
    console.log('Native CLI: Not available');
  }
}

async function handleNode(args: string[]): Promise<void> {
  // Handle help flags
  if (args.length === 0 || ['--help', '-h', 'help'].includes(args[0])) {
    console.log(`Usage: ${CLI_NAME} node <subcommand> [options]`);
    console.log('');
    console.log('Node.js-specific commands');
    console.log('');
    console.log('Subcommands:');
    console.log('  create-addon                  Generate native addon files for Electron');
    console.log('  add-electron-debug-identity   Add package identity to Electron debug process');
    console.log('  clear-electron-debug-identity Remove package identity from Electron debug process');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node create-addon --help`);
    console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
    console.log(`  ${CLI_NAME} node create-addon --name myCsAddon --template cs`);
    console.log(`  ${CLI_NAME} node add-electron-debug-identity`);
    console.log(`  ${CLI_NAME} node clear-electron-debug-identity`);
    console.log('');
    console.log(`Use "${CLI_NAME} node <subcommand> --help" for detailed help on each subcommand.`);
    return;
  }

  const subcommand = args[0];
  const subcommandArgs = args.slice(1);

  switch (subcommand) {
    case 'create-addon':
      await handleCreateAddon(subcommandArgs);
      break;

    case 'add-electron-debug-identity':
      await handleAddonElectronDebugIdentity(subcommandArgs);
      break;

    case 'clear-electron-debug-identity':
      await handleClearElectronDebugIdentity(subcommandArgs);
      break;

    default:
      console.error(`❌ Unknown node subcommand: ${subcommand}`);
      console.error(`Run "${CLI_NAME} node" for available subcommands.`);
      process.exit(1);
  }
}

async function handleCreateAddon(args: string[]): Promise<void> {
  const options = parseArgs(args, {
    name: undefined, // Will be set based on template
    template: 'cpp',
    verbose: false,
  });

  // Set default name based on template
  if (!options.name) {
    options.name = options.template === 'cs' ? 'csAddon' : 'nativeWindowsAddon';
  }

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node create-addon [options]`);
    console.log('');
    console.log('Generate addon files for Electron project');
    console.log('');
    console.log('Options:');
    console.log('  --name <name>         Addon name (default depends on template)');
    console.log('  --template <type>     Addon template: cpp, cs (default: cpp)');
    console.log('  --verbose             Enable verbose output (default: false)');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Templates:');
    console.log('  cpp                   C++ native addon (node-gyp)');
    console.log('  cs                    C# addon (node-api-dotnet)');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node create-addon`);
    console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
    console.log(`  ${CLI_NAME} node create-addon --template cs --name MyCsAddon`);
    console.log('');
    console.log('Note: This command must be run from the root of an Electron project');
    console.log('      (directory containing package.json)');
    return;
  }

  // Validate template
  if (!['cpp', 'cs'].includes(options.template as string)) {
    console.error(`❌ Invalid template: ${options.template}. Valid options: cpp, cs`);
    process.exit(1);
  }

  try {
    let result;

    if (options.template === 'cs') {
      // Use C# addon generator
      result = await generateCsAddonFiles({
        name: options.name as string,
        verbose: options.verbose as boolean,
      });

      console.log(`New addon at: ${result.addonPath}`);

      const restoreArgs = ['restore'];
      if (options.verbose) {
        restoreArgs.push('--verbose');
      }

      await callWinappCli(restoreArgs, { exitOnError: true });

      console.log('');

      if (result.needsTerminalRestart) {
        printTerminalRestartInstructions();
      }

      console.log(`Next steps:`);
      console.log(`  1. npm run build-${result.addonName}`);
      console.log(`  2. See ${result.addonName}/README.md for usage examples`);
    } else {
      // Use C++ addon generator
      result = await generateCppAddonFiles({
        name: options.name as string,
        verbose: options.verbose as boolean,
      });

      console.log(`New addon at: ${result.addonPath}`);
      console.log('');

      if (result.needsTerminalRestart) {
        printTerminalRestartInstructions();
      }

      console.log(`Next steps:`);
      console.log(`  1. npm run build-${result.addonName}`);
      console.log(`  2. In your source, import the addon with:`);
      console.log(
        `     "const ${result.addonName} = require('./${result.addonName}/build/Release/${result.addonName}.node')";`
      );
    }
  } catch (error) {
    const err = error as Error;
    console.error(`❌ Failed to generate addon files: ${err.message}`);
    process.exit(1);
  }
}

function printTerminalRestartInstructions(): void {
  console.log(
    '⚠️ IMPORTANT: You need to restart your terminal/command prompt for newly installed tools to be available in your PATH.'
  );

  // Simple check: This variable usually only exists if running inside PowerShell
  if (process.env.PSModulePath) {
    console.log('💡 To refresh current session, copy and run this line:');
    console.log(
      '   \x1b[36m$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")\x1b[0m'
    );
  }
  console.log('');
}

async function handleAddonElectronDebugIdentity(args: string[]): Promise<void> {
  const options = parseArgs(args, {
    verbose: false,
    'no-install': false,
    manifest: undefined,
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node add-electron-debug-identity [options]`);
    console.log('');
    console.log('Add package identity to Electron debug process');
    console.log('');
    console.log('This command will:');
    console.log('  1. Create a backup of node_modules/electron/dist/electron.exe');
    console.log(
      '  2. Generate a sparse MSIX manifest in .winapp/debug folder, and assets in node_modules/electron/dist/ folder'
    );
    console.log('  3. Add package identity to the Electron executable');
    console.log('  4. Register the sparse package with external location');
    console.log('');
    console.log('Options:');
    console.log(
      '  --manifest <path>     Path to custom appxmanifest.xml (default: appxmanifest.xml in current directory)'
    );
    console.log('  --no-install          Do not install the package after creation (will require manual registration)');
    console.log('  --verbose             Enable verbose output (default: false)');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Note: This command must be run from the root of an Electron project');
    console.log('      (directory containing node_modules/electron)');
    return;
  }

  try {
    await addElectronDebugIdentity({
      verbose: options.verbose as boolean,
      noInstall: options['no-install'] as boolean,
      manifest: options.manifest as string | undefined,
    });

    console.log(`✅ Electron debug identity setup completed successfully!`);
  } catch (error) {
    const err = error as Error;
    console.error(`❌ Failed to add Electron debug identity: ${err.message}`);
    process.exit(1);
  }
}

async function handleClearElectronDebugIdentity(args: string[]): Promise<void> {
  const options = parseArgs(args, {
    verbose: false,
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node clear-electron-debug-identity [options]`);
    console.log('');
    console.log('Remove package identity from Electron debug process');
    console.log('');
    console.log('This command will:');
    console.log('  1. Restore electron.exe from the backup created by add-electron-debug-identity');
    console.log('  2. Remove the backup files');
    console.log('');
    console.log('Options:');
    console.log('  --verbose             Enable verbose output (default: false)');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Note: This command must be run from the root of an Electron project');
    console.log('      (directory containing node_modules/electron)');
    return;
  }

  try {
    const result = await clearElectronDebugIdentity({
      verbose: options.verbose as boolean,
    });

    if (result.restoredFromBackup) {
      console.log(`✅ Electron debug identity cleared successfully!`);
    } else {
      console.log(`ℹ️  No backup found - electron.exe may already be clean.`);
    }
  } catch (error) {
    const err = error as Error;
    console.error(`❌ Failed to clear Electron debug identity: ${err.message}`);
    process.exit(1);
  }
}

function parseArgs(args: string[], defaults: ParsedArgs = {}): ParsedArgs {
  const result: ParsedArgs = { ...defaults };

  for (let i = 0; i < args.length; i++) {
    const arg = args[i];

    if (arg === '--help' || arg === '-h') {
      result.help = true;
    } else if (arg.startsWith('--')) {
      const key = arg.slice(2);
      const nextArg = args[i + 1];

      if (nextArg && !nextArg.startsWith('--')) {
        // Value argument
        result[key] = nextArg;
        i++; // Skip next arg
      } else {
        // Boolean flag
        result[key] = true;
      }
    }
  }

  return result;
}

// Run if called directly
if (require.main === module) {
  main();
}
