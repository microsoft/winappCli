#!/usr/bin/env node

const { setupSDKs } = require('./setup-sdks');
const { main: runBuildTool } = require('./run-buildtool');
const { 
  generateMsixAssets,
  createMsixPackage,
  generateDevCertificate,
  installDevCertificate,
  signMsixPackage,
  addMsixIdentityToExe,
  addElectronDebugIdentity
} = require('./msix-utils');
const { generateAddonFiles } = require('./addon-utils');

// CLI name - change this to rebrand the tool
const CLI_NAME = 'winsdk';

/**
 * Main CLI entry point for winsdk package
 */
async function main() {
  const args = process.argv.slice(2);
  
  if (args.length === 0) {
    showHelp();
    process.exit(1);
  }
  
  const command = args[0];
  const commandArgs = args.slice(1);
  
  try {
    switch (command) {
      case 'setup':
      case 'setup-sdks':
        {
          console.log('🚀 Setting up Windows SDKs...');
          
          // Parse command line options
          const options = { verbose: true };
          for (let i = 0; i < commandArgs.length; i++) {
            const arg = commandArgs[i];
            if (arg === '--no-cleanup' || arg === '--keep-old-versions') {
              options.cleanupOldVersions = false;
            } else if (arg === '--no-config') {
              options.useConfig = false;
            } else if (arg === '--no-gitignore') {
              options.updateGitignore = false;
            } else if (arg === '--quiet' || arg === '-q') {
              options.verbose = false;
            } else if (arg === '--experimental') {
              options.includeExperimental = true;
            }
          }
          
          if (options.cleanupOldVersions === false) {
            console.log('📦 Old package versions will be kept');
          }
          
          if (options.includeExperimental) {
            console.log('🧪 Experimental/prerelease packages will be included');
          }
          
          await setupSDKs(options);
          console.log('✅ Windows SDKs setup completed!');
        }
        break;
        
      case 'tool':
      case 'run-buildtool':
        if (commandArgs.length === 0) {
          console.error('Error: No build tool command specified');
          console.error(`Usage: ${CLI_NAME} tool <command> [args...]`);
          console.error(`Example: ${CLI_NAME} tool makeappx.exe pack /o /d "./msix" /nv /p "./dist/app.msix"`);
          process.exit(1);
        }
        
        // Set process.argv to match what run-buildtool expects
        process.argv = ['node', 'run-buildtool.js', ...commandArgs];
        await runBuildTool();
        break;
        
      case 'help':
      case '--help':
      case '-h':
        showHelp();
        break;
        
      case 'version':
      case '--version':
      case '-v':
        showVersion();
        break;
        
      case 'msix':
        await handleMsix(commandArgs);
        break;
        
      case 'addon':
        await handleAddon(commandArgs);
        break;
        
      default:
        console.error(`Unknown command: ${command}`);
        console.error(`Run "${CLI_NAME} help" for usage information.`);
        process.exit(1);
    }
  } catch (error) {
    console.error(`Error: ${error.message}`);
    process.exit(1);
  }
}

function showHelp() {
  const packageJson = require('./package.json');
  
  console.log(`${packageJson.name} v${packageJson.version}`);
  console.log(packageJson.description);
  console.log('');
  console.log('Usage:');
  console.log(`  ${CLI_NAME} <command> [options]`);
  console.log('');
  console.log('Commands:');
  console.log('  setup, setup-sdks           Download and setup Windows SDKs');
  console.log('    --no-cleanup              Keep old package versions instead of cleaning them up');
  console.log('    --keep-old-versions       Alias for --no-cleanup');
  console.log('    --no-config              Don\'t use configuration file for version management');
  console.log('    --no-gitignore           Don\'t update .gitignore file');
  console.log('    --experimental           Include experimental/prerelease packages from NuGet');
  console.log('    --quiet, -q              Suppress progress messages');
  console.log('  tool, run-buildtool <cmd>   Run a build tool command with Windows SDK paths');
  console.log('  msix <subcommand>           MSIX package management commands');
  console.log('  addon <subcommand>          Native addon generation commands');
  console.log('  help                         Show this help message');
  console.log('  version                      Show version information');
  console.log('');
  console.log('MSIX Subcommands:');
  console.log('  msix init                   Generate MSIX manifest and assets');
  console.log('  msix package                Create an MSIX package from a folder');
  console.log('  msix cert                   Generate or install development certificates');
  console.log('  msix sign                   Sign an MSIX package with a certificate');
  console.log('  msix add-identity-to-exe    Add MSIX identity to an executable');
  console.log('  msix add-electron-debug-identity  Add MSIX identity to Electron debug process');
  console.log('');
  console.log('Addon Subcommands:');
  console.log('  addon generate              Generate native addon files for Electron');
  console.log('');
  console.log('Examples:');
  console.log(`  ${CLI_NAME} setup`);
  console.log(`  ${CLI_NAME} setup --no-cleanup  # Keep old package versions`);
  console.log(`  ${CLI_NAME} setup --experimental  # Include prerelease packages`);
  console.log(`  ${CLI_NAME} addon generate --name myAddon`);
  console.log(`  ${CLI_NAME} msix init --sparse --output ./msix`);
  console.log(`  ${CLI_NAME} msix package ./app-folder ./output --cert ./cert.pfx`);
  console.log(`  ${CLI_NAME} msix cert generate --publisher "My Company" --install`);
  console.log(`  ${CLI_NAME} msix sign ./app.msix ./cert.pfx`);
  console.log(`  ${CLI_NAME} msix add-electron-debug-identity`);
  console.log(`  ${CLI_NAME} tool mt.exe -manifest app.manifest`);
  console.log(`  ${CLI_NAME} tool makeappx.exe pack /o /d "./msix" /p "./dist/app.msix"`);
  console.log(`  ${CLI_NAME} tool signtool.exe sign /fd SHA256 /f cert.pfx app.exe`);
  console.log('');
  console.log('Note: This package provides programmatic access to these functions.');
  console.log('See documentation for usage in Node.js applications.');
}

function showVersion() {
  const packageJson = require('./package.json');
  console.log(packageJson.version);
}

// Run if called directly
if (require.main === module) {
  main();
}

module.exports = { main };

async function handleMsix(args) {
  if (args.length === 0) {
    console.log(`Usage: ${CLI_NAME} msix <subcommand> [options]`);
    console.log('');
    console.log('MSIX package management commands');
    console.log('');
    console.log('Subcommands:');
    console.log('  init                        Generate MSIX manifest and assets');
    console.log('  package                     Create an MSIX package from a folder');
    console.log('  cert                        Generate or install development certificates');
    console.log('  sign                        Sign an MSIX package with a certificate');
    console.log('  add-identity-to-exe         Add MSIX identity to an executable');
    console.log('  add-electron-debug-identity Add MSIX identity to Electron debug process');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} msix init --sparse --output ./msix`);
    console.log(`  ${CLI_NAME} msix package ./app-folder ./output --cert ./cert.pfx`);
    console.log(`  ${CLI_NAME} msix cert generate --publisher "My Company"`);
    console.log(`  ${CLI_NAME} msix sign ./app.msix ./cert.pfx`);
    console.log(`  ${CLI_NAME} msix add-identity-to-exe ./app.exe ./appxmanifest.xml`);
    console.log(`  ${CLI_NAME} msix add-electron-debug-identity`);
    console.log('');
    console.log(`Use "${CLI_NAME} msix <subcommand> --help" for detailed help on each subcommand.`);
    return;
  }

  const subcommand = args[0];
  const subcommandArgs = args.slice(1);

  switch (subcommand) {
    case 'init':
      await handleMsixInit(subcommandArgs);
      break;
      
    case 'package':
      await handleMsixPackage(subcommandArgs);
      break;
      
    case 'cert':
      await handleMsixCert(subcommandArgs);
      break;
      
    case 'sign':
      await handleMsixSign(subcommandArgs);
      break;
      
    case 'add-identity-to-exe':
      await handleMsixIdentity(subcommandArgs);
      break;
      
    case 'add-electron-debug-identity':
      await handleMsixElectronDebugIdentity(subcommandArgs);
      break;
      
    default:
      console.error(`❌ Unknown MSIX subcommand: ${subcommand}`);
      console.error(`Run "${CLI_NAME} msix" for available subcommands.`);
      process.exit(1);
  }
}

async function handleMsixInit(args) {
  const options = parseArgs(args, {
    sparse: false,
    output: process.cwd(),
    name: null,
    publisher: null,
    description: null,
    version: '1.0.0.0',
    verbose: true
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} msix init [options]`);
    console.log('');
    console.log('Generate MSIX manifest and assets for an app');
    console.log('');
    console.log('Options:');
    console.log('  --sparse              Generate sparse package manifest');
    console.log('  --output <dir>        Output directory (default: current directory)');
    console.log('  --name <name>         Package name (default: from package.json)');
    console.log('  --publisher <name>    Publisher name (default: from package.json)');
    console.log('  --description <desc>  Package description (default: from package.json)');
    console.log('  --version <version>   Package version (default: 1.0.0.0)');
    console.log('  --verbose             Enable verbose output (default: true)');
    console.log('  --help                Show this help');
    return;
  }

  try {
    const result = await generateMsixAssets({
      packageName: options.name,
      publisherName: options.publisher,
      description: options.description,
      isSparsePackage: options.sparse,
      outputDir: options.output,
      version: options.version,
      verbose: options.verbose
    });

    console.log(`✅ MSIX assets generated successfully!`);
    console.log(`📁 Manifest: ${result.manifestPath}`);
    console.log(`🎨 Assets: ${result.assetsDir}`);
  } catch (error) {
    console.error(`❌ Failed to generate MSIX assets: ${error.message}`);
    process.exit(1);
  }
}

async function handleMsixPackage(args) {
  if (args.length < 2) {
    console.log(`Usage: ${CLI_NAME} msix package <input-folder> <output-folder> [options]`);
    console.log('');
    console.log('Create an MSIX package from a prepared package directory');
    console.log('');
    console.log('Options:');
    console.log('  --name <name>             Package name (default: from manifest)');
    console.log('  --skip-pri                Skip PRI file generation');
    console.log('  --cert <path>             Path to signing certificate (will auto-sign if provided)');
    console.log('  --cert-password <pass>    Certificate password (default: password)');
    console.log('  --generate-cert           Generate a new development certificate');
    console.log('  --install-cert            Install certificate to machine');
    console.log('  --publisher <name>        Publisher name for certificate generation');
    console.log('  --verbose                 Enable verbose output (default: true)');
    console.log('  --help                    Show this help');
    return;
  }

  const inputFolder = args[0];
  const outputFolder = args[1];
  const options = parseArgs(args.slice(2), {
    name: null,
    'skip-pri': false,
    cert: null,
    'cert-password': 'password',
    'generate-cert': false,
    'install-cert': false,
    publisher: null,
    verbose: true
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} msix package <input-folder> <output-folder> [options]`);
    return;
  }

  try {
    // Auto-sign if certificate is provided or if generate-cert is specified
    const autoSign = !!(options.cert || options['generate-cert']);
    
    const result = await createMsixPackage(inputFolder, outputFolder, {
      packageName: options.name,
      skipPri: options['skip-pri'],
      autoSign: autoSign,
      certificatePath: options.cert,
      certificatePassword: options['cert-password'],
      generateDevCert: options['generate-cert'],
      installDevCert: options['install-cert'],
      publisher: options.publisher,
      verbose: options.verbose
    });

    console.log(`✅ MSIX package created successfully!`);
    console.log(`📦 Package: ${result.msixPath}`);
    if (result.signed) {
      console.log(`🔐 Package has been signed`);
    }
  } catch (error) {
    console.error(`❌ Failed to create MSIX package: ${error.message}`);
    process.exit(1);
  }
}

async function handleMsixCert(args) {
  if (args.length === 0) {
    console.log(`Usage: ${CLI_NAME} msix cert <command> [options]`);
    console.log('');
    console.log('Commands:');
    console.log('  generate    Generate a development certificate');
    console.log('  install     Install a certificate to the machine');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} msix cert generate --publisher "My Company" --output ./cert.pfx`);
    console.log(`  ${CLI_NAME} msix cert install ./cert.pfx --password mypassword`);
    return;
  }

  const command = args[0];
  const commandArgs = args.slice(1);

  if (command === 'generate') {
    const options = parseArgs(commandArgs, {
      publisher: null,
      output: './dev-cert.pfx',
      password: 'password',
      'valid-days': 365,
      verbose: true
    });

    if (options.help) {
      console.log(`Usage: ${CLI_NAME} msix cert generate [options]`);
      console.log('');
      console.log('Options:');
      console.log('  --publisher <name>    Publisher name (required)');
      console.log('  --output <path>       Output certificate path (default: ./dev-cert.pfx)');
      console.log('  --password <pass>     Certificate password (default: password)');
      console.log('  --valid-days <days>   Certificate validity in days (default: 365)');
      console.log('  --verbose             Enable verbose output');
      console.log('  --help                Show this help');
      return;
    }

    if (!options.publisher) {
      console.error('❌ Publisher name is required');
      process.exit(1);
    }

    try {
      const result = await generateDevCertificate(options.output, {
        publisher: options.publisher,
        password: options.password,
        validDays: options['valid-days'],
        verbose: options.verbose
      });

      console.log(`✅ Certificate generated successfully!`);
      console.log(`🔐 Certificate: ${result.certificatePath}`);
    } catch (error) {
      console.error(`❌ Failed to generate certificate: ${error.message}`);
      process.exit(1);
    }
  } else if (command === 'install') {
    if (commandArgs.length === 0) {
      console.error('❌ Certificate path is required');
      process.exit(1);
    }

    const certPath = commandArgs[0];
    const options = parseArgs(commandArgs.slice(1), {
      password: 'password',
      force: false,
      verbose: true
    });

    if (options.help) {
      console.log(`Usage: ${CLI_NAME} msix cert install <cert-path> [options]`);
      console.log('');
      console.log('Options:');
      console.log('  --password <pass>     Certificate password (default: password)');
      console.log('  --force               Force install even if already present');
      console.log('  --verbose             Enable verbose output');
      console.log('  --help                Show this help');
      return;
    }

    try {
      const result = await installDevCertificate(certPath, {
        password: options.password,
        force: options.force,
        verbose: options.verbose
      });

      if (result.alreadyInstalled) {
        console.log(`ℹ️  Certificate is already installed`);
      } else {
        console.log(`✅ Certificate installed successfully!`);
      }
    } catch (error) {
      console.error(`❌ Failed to install certificate: ${error.message}`);
      process.exit(1);
    }
  } else {
    console.error(`❌ Unknown cert command: ${command}`);
    process.exit(1);
  }
}

async function handleMsixSign(args) {
  if (args.length < 2) {
    console.log(`Usage: ${CLI_NAME} msix sign <msix-path> <cert-path> [options]`);
    console.log('');
    console.log('Sign an MSIX package with a certificate');
    console.log('');
    console.log('Options:');
    console.log('  --password <pass>     Certificate password (default: password)');
    console.log('  --timestamp <url>     Timestamp server URL');
    console.log('  --verbose             Enable verbose output');
    console.log('  --help                Show this help');
    return;
  }

  const msixPath = args[0];
  const certPath = args[1];
  const options = parseArgs(args.slice(2), {
    password: 'password',
    timestamp: null,
    verbose: true
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} msix sign <msix-path> <cert-path> [options]`);
    return;
  }

  try {
    const result = await signMsixPackage(msixPath, certPath, {
      password: options.password,
      timestampUrl: options.timestamp,
      verbose: options.verbose
    });

    console.log(`✅ MSIX package signed successfully!`);
    console.log(`🔐 Signed package: ${result.msixPath}`);
  } catch (error) {
    console.error(`❌ Failed to sign MSIX package: ${error.message}`);
    process.exit(1);
  }
}

async function handleMsixIdentity(args) {
  if (args.length < 2) {
    console.log(`Usage: ${CLI_NAME} msix add-identity-to-exe <exe-path> <manifest-path> [options]`);
    console.log('');
    console.log('Add MSIX identity information to an executable');
    console.log('');
    console.log('Options:');
    console.log('  --temp-dir <dir>      Directory for temporary files');
    console.log('  --verbose             Enable verbose output');
    console.log('  --help                Show this help');
    return;
  }

  const exePath = args[0];
  const manifestPath = args[1];
  const options = parseArgs(args.slice(2), {
    'temp-dir': null,
    verbose: true
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} msix add-identity-to-exe <exe-path> <manifest-path> [options]`);
    return;
  }

  try {
    const result = await addMsixIdentityToExe(exePath, manifestPath, {
      tempDir: options['temp-dir'],
      verbose: options.verbose
    });

    console.log(`✅ MSIX identity added successfully!`);
    console.log(`📦 Package: ${result.packageName}`);
    console.log(`👤 Publisher: ${result.publisher}`);
    console.log(`🆔 App ID: ${result.applicationId}`);
  } catch (error) {
    console.error(`❌ Failed to add MSIX identity: ${error.message}`);
    process.exit(1);
  }
}

async function handleMsixElectronDebugIdentity(args) {
  const options = parseArgs(args, {
    verbose: true
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} msix add-electron-debug-identity [options]`);
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
    console.log(`📦 Package: ${result.packageName}`);
    console.log(`👤 Publisher: ${result.publisher}`);
    console.log(`🆔 App ID: ${result.applicationId}`);
    console.log(`📁 Manifest: ${result.manifestPath}`);
    console.log(`💾 Backup: ${result.backupPath}`);
  } catch (error) {
    console.error(`❌ Failed to add Electron debug identity: ${error.message}`);
    process.exit(1);
  }
}

async function handleAddon(args) {
  if (args.length === 0) {
    console.log(`Usage: ${CLI_NAME} addon <subcommand> [options]`);
    console.log('');
    console.log('Native addon generation commands');
    console.log('');
    console.log('Subcommands:');
    console.log('  generate                    Generate native addon files for Electron');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} addon generate --name myAddon`);
    console.log(`  ${CLI_NAME} addon generate --name customAddon --verbose`);
    console.log('');
    console.log(`Use "${CLI_NAME} addon <subcommand> --help" for detailed help on each subcommand.`);
    return;
  }

  const subcommand = args[0];
  const subcommandArgs = args.slice(1);

  switch (subcommand) {
    case 'generate':
      await handleAddonGenerate(subcommandArgs);
      break;
      
    default:
      console.error(`❌ Unknown addon subcommand: ${subcommand}`);
      console.error(`Run "${CLI_NAME} addon" for available subcommands.`);
      process.exit(1);
  }
}

async function handleAddonGenerate(args) {
  const options = parseArgs(args, {
    name: 'nativeWindowsAddon',
    verbose: true
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} addon generate [options]`);
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
    console.log(`  ${CLI_NAME} addon generate`);
    console.log(`  ${CLI_NAME} addon generate --name myCustomAddon`);
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
