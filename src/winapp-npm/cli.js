#!/usr/bin/env node

const { generateAddonFiles } = require('./addon-utils');
const { addElectronDebugIdentity } = require('./msix-utils');
const { getWinappCliPath, callWinappCli } = require('./winapp-cli-utils');
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const os = require('os');

// CLI name - change this to rebrand the tool
const CLI_NAME = 'winapp';

// Commands that should be handled by Node.js (everything else goes to winapp-cli)
const NODE_ONLY_COMMANDS = new Set(['node']);

/**
 * Main CLI entry point for winapp package
 */
async function main() {
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
    await callWinappCli(args, { verbose: true, exitOnError: true });
    
  } catch (error) {
    console.error(`Error: ${error.message}`);
    process.exit(1);
  }
}

async function handleNodeCommand(command, args) {
  switch (command) {
    case 'node':
      await handleNode(args);
      break;
      
    default:
      console.error(`Unknown Node.js command: ${command}`);
      process.exit(1);
  }
}

async function showCombinedHelp() {
  const packageJson = require('./package.json');
  
  console.log(`${packageJson.name} v${packageJson.version}`);
  console.log(packageJson.description);
  console.log('');
  
  // Try to get help from winapp-cli first
  try {
    const winappCliPath = getWinappCliPath();
    await new Promise((resolve, reject) => {
      const child = spawn(winappCliPath, ['--help'], {
        stdio: 'inherit',
        shell: false
      });
      
      child.on('close', (code) => {
        resolve();
      });
      
      child.on('error', (error) => {
        // If winapp-cli is not available, continue without showing fallback help
        resolve();
      });
    });
  } catch (error) {
    // Continue without showing fallback help if winapp-cli is not available
  }
  
  // Add Node.js-specific commands
  console.log('');
  console.log('Node.js Extensions:');
  console.log('  node <subcommand>         Node.js-specific commands');
  console.log('');
  console.log('Node.js Subcommands:');
  console.log('  node create-addon         Generate native addon files for Electron');
  console.log('  node add-electron-debug-identity  Add MSIX identity to Electron debug process');
  console.log('');
  console.log('Examples:');
  console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
  console.log(`  ${CLI_NAME} node add-electron-debug-identity`);
}

async function showVersion() {
  const packageJson = require('./package.json');
  
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
    
    await new Promise((resolve, reject) => {
      const child = spawn(winappCliPath, ['--version'], {
        stdio: 'inherit',
        shell: false
      });
      
      child.on('close', (code) => {
        if (code !== 0) {
          console.log('  (version command failed)');
        }
        resolve();
      });
      
      child.on('error', (error) => {
        console.log('  Not available (execution failed)');
        resolve();
      });
    });
  } catch (error) {
    console.log('Native CLI: Not available');
  }
}

// Run if called directly
if (require.main === module) {
  main();
}

module.exports = { main };

async function handleNode(args) {
  if (args.length === 0) {
    console.log(`Usage: ${CLI_NAME} node <subcommand> [options]`);
    console.log('');
    console.log('Node.js-specific commands');
    console.log('');
    console.log('Subcommands:');
    console.log('  create-addon                Generate native addon files for Electron');
    console.log('  add-electron-debug-identity Add MSIX identity to Electron debug process');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
    console.log(`  ${CLI_NAME} node add-electron-debug-identity`);
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
      
    default:
      console.error(`❌ Unknown node subcommand: ${subcommand}`);
      console.error(`Run "${CLI_NAME} node" for available subcommands.`);
      process.exit(1);
  }
}

async function handleCreateAddon(args) {
  const options = parseArgs(args, {
    name: 'nativeWindowsAddon',
    verbose: true
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node create-addon [options]`);
    console.log('');
    console.log('Generate native addon files for Electron project');
    console.log('');
    console.log('This command will:');
    console.log('  1. Create a new addon directory with template files');
    console.log('  2. Replace placeholders with the provided addon name');
    console.log('  3. Install required npm packages (nan, node-addon-api, node-gyp)');
    console.log('  4. Add build script to package.json');
    console.log('');
    console.log('Options:');
    console.log('  --name <name>         Addon name (default: nativeWindowsAddon)');
    console.log('  --verbose             Enable verbose output (default: true)');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node create-addon`);
    console.log(`  ${CLI_NAME} node create-addon --name myCustomAddon`);
    console.log('');
    console.log('Note: This command must be run from the root of an Electron project');
    console.log('      (directory containing package.json)');
    return;
  }

  try {
    const result = await generateAddonFiles({
      name: options.name,
      verbose: options.verbose
    });

    console.log(`✅ Addon files generated successfully!`);
    console.log(`📦 Addon name: ${result.addonName}`);
    console.log(`📁 Addon path: ${result.addonPath}`);
    console.log(`🔨 Build with: npm run build-${result.addonName}`);
    console.log(`🔨 In your source, import the addon with:`);
    console.log(`       "const ${result.addonName} = require('./${result.addonName}/build/Release/${result.addonName}.node')";`);
  } catch (error) {
    console.error(`❌ Failed to generate addon files: ${error.message}`);
    process.exit(1);
  }
}

async function handleAddonElectronDebugIdentity(args) {
  const options = parseArgs(args, {
    verbose: true
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node add-electron-debug-identity [options]`);
    console.log('');
    console.log('Add MSIX identity to Electron debug process');
    console.log('');
    console.log('This command will:');
    console.log('  1. Create a backup of node_modules/electron/dist/electron.exe');
    console.log('  2. Generate a sparse MSIX manifest and assets in msix-debug folder');
    console.log('  3. Add MSIX identity to the Electron executable');
    console.log('  4. Register the sparse package with external location');
    console.log('');
    console.log('Options:');
    console.log('  --verbose             Enable verbose output (default: true)');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Note: This command must be run from the root of an Electron project');
    console.log('      (directory containing node_modules/electron)');
    return;
  }

  try {
    const result = await addElectronDebugIdentity({
      verbose: options.verbose
    });

    console.log(`✅ Electron debug identity setup completed successfully!`);
  } catch (error) {
    console.error(`❌ Failed to add Electron debug identity: ${error.message}`);
    process.exit(1);
  }
}

function parseArgs(args, defaults = {}) {
  const result = { ...defaults };
  
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
