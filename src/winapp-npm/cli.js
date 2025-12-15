#!/usr/bin/env node

const { generateAddonFiles } = require('./addon-utils');
const { generateCsAddonFiles, checkAndInstallDotnet10Sdk, checkAndInstallVisualStudioBuildTools } = require('./cs-addon-utils');
const { addElectronDebugIdentity } = require('./msix-utils');
const { getWinappCliPath, callWinappCli, WINAPP_CLI_CALLER_VALUE } = require('./winapp-cli-utils');
const { spawn, exec } = require('child_process');
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
        shell: false,
        env: {
          ...process.env,
          WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE
        }
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
  console.log(`  ${CLI_NAME} node create-addon --template cs --name myAddon`);
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
        shell: false,
        env: {
          ...process.env,
          WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE
        }
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
  // Handle help flags
  if (args.length === 0 || ['--help', '-h', 'help'].includes(args[0])) {
    console.log(`Usage: ${CLI_NAME} node <subcommand> [options]`);
    console.log('');
    console.log('Node.js-specific commands');
    console.log('');
    console.log('Subcommands:');
    console.log('  create-addon                Generate native addon files for Electron');
    console.log('  add-electron-debug-identity Add MSIX identity to Electron debug process');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node create-addon --help`);
    console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
    console.log(`  ${CLI_NAME} node create-addon --name myCsAddon --template cs`);
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
    name: undefined, // Will be set based on template
    template: 'cpp',
    verbose: true
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
    console.log('  --verbose             Enable verbose output (default: true)');
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
  if (!['cpp', 'cs'].includes(options.template)) {
    console.error(`❌ Invalid template: ${options.template}. Valid options: cpp, cs`);
    process.exit(1);
  }

  try {
    let result;
    
    if (options.template === 'cs') {
      // Use C# addon generator
      result = await generateCsAddonFiles({
        name: options.name,
        verbose: options.verbose
      });
      
      console.log(`New addon at: ${result.addonPath}`);
      
      await callWinappCli(['restore'], { verbose: options.verbose, exitOnError: true });

      console.log('');
      
      if (result.needsTerminalRestart) {
        console.log('⚠️  IMPORTANT: You need to restart your terminal/command prompt for newly installed tools to be available in your PATH.');

        // Simple check: This variable usually only exists if running inside PowerShell
        if (process.env.PSModulePath) {
          console.log('');
          console.log('💡 To refresh immediately, copy and run this line:');
          console.log('\t\x1b[36m$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")\x1b[0m');
        }
        console.log('');
      }
      
      console.log(`Next steps:`);
      console.log(`  1. npm run build-${result.addonName}`);
      console.log(`  2. See ${result.addonName}/README.md for usage examples`);

    } else {

      if (!await pythonExists()) {
        console.error(`❌ Python is required to generate C++ addons but was not found in your PATH.`);
        console.error(`   Please install Python (version 3.10 or later) and ensure it is accessible from the command line.`);
        process.exit(1);
      }

      // Use C++ addon generator (existing)
      result = await generateAddonFiles({
        name: options.name,
        verbose: options.verbose
      });

      // Check for Visual Studio Build Tools and offer to install if missing
      await checkAndInstallVisualStudioBuildTools(options.verbose);

      console.log(`✅ Addon files generated successfully!`);
      console.log(`📦 Addon name: ${result.addonName}`);
      console.log(`📁 Addon path: ${result.addonPath}`);
      
      console.log(`🔨 Build with: npm run build-${result.addonName}`);
      console.log(`🔨 In your source, import the addon with:`);
      console.log(`       "const ${result.addonName} = require('./${result.addonName}/build/Release/${result.addonName}.node')";`);
    }
  } catch (error) {
    console.error(`❌ Failed to generate addon files: ${error.message}`);
    process.exit(1);
  }
}

function pythonExists() {
  const commands = ["python --version", "python3 --version", "py --version"];

  return new Promise(resolve => {
    let index = 0;

    function tryNext() {
      if (index >= commands.length) return resolve(false);

      exec(commands[index], (err) => {
        if (!err) return resolve(true);
        index++;
        tryNext();
      });
    }

    tryNext();
  });
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
