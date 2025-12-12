const fs = require('fs').promises;
const fsSync = require('fs');
const path = require('path');
const { execSync } = require('child_process');

/**
 * Generates C# addon files for an Electron project
 * @param {Object} options - Configuration options
 * @param {string} options.name - Name of the addon (default: 'csAddon')
 * @param {string} options.projectRoot - Root directory of the project (default: current working directory)
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 */
async function generateCsAddonFiles(options = {}) {
  const { 
    name = 'csAddon', 
    projectRoot = process.cwd(), 
    verbose = true 
  } = options;

  try {
    // Validate addon name (should be a valid C# namespace/class name)
    validateAddonName(name);

    await checkAndInstallVisualStudioBuildTools(false); // Don't show verbose build tools info

    // Check if dotnet SDK is available and offer to install if missing
    await checkAndInstallDotnet10Sdk(false); // Don't show verbose SDK info

    // Check if addon already exists
    const addonDir = path.join(projectRoot, name);
    if (fsSync.existsSync(addonDir)) {
      throw new Error(`Addon directory already exists: ${name}. Please choose a different name or remove the existing directory.`);
    }
    
    if (verbose) {
      console.log(`📁 Creating addon directory: ${name}`);
    }

    // Create the addon directory
    await fs.mkdir(addonDir, { recursive: true });

    // Copy and process template files
    await copyTemplateFiles(name, addonDir, projectRoot, false); // Don't show individual file messages

    // Install required npm packages
    await installNodeApiDotnet(projectRoot, false); // Don't show package install details

    // Add build scripts to package.json
    await addCsBuildScripts(name, projectRoot, false); // Don't show script addition messages

    // Update .gitignore
    await updateGitignore(projectRoot, false); // Don't show gitignore update message

    const result = {
      success: true,
      addonName: name,
      addonPath: addonDir,
      files: [
        path.join(addonDir, `${name}.csproj`),
        path.join(addonDir, 'addon.cs'),
        path.join(addonDir, 'README.md')
      ]
    };

    return result;

  } catch (error) {
    throw new Error(`Failed to generate C# addon files: ${error.message}`);
  }
}

/**
 * Validates that the addon name is suitable for C# namespace/class
 * @param {string} name - Addon name to validate
 */
function validateAddonName(name) {
  // Must start with a letter or underscore
  if (!/^[a-zA-Z_]/.test(name)) {
    throw new Error('Addon name must start with a letter or underscore');
  }

  // Must contain only letters, numbers, and underscores
  if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(name)) {
    throw new Error('Addon name must contain only letters, numbers, and underscores');
  }

  // Should not be a C# keyword
  const csharpKeywords = [
    'abstract', 'as', 'base', 'bool', 'break', 'byte', 'case', 'catch', 'char',
    'checked', 'class', 'const', 'continue', 'decimal', 'default', 'delegate',
    'do', 'double', 'else', 'enum', 'event', 'explicit', 'extern', 'false',
    'finally', 'fixed', 'float', 'for', 'foreach', 'goto', 'if', 'implicit',
    'in', 'int', 'interface', 'internal', 'is', 'lock', 'long', 'namespace',
    'new', 'null', 'object', 'operator', 'out', 'override', 'params', 'private',
    'protected', 'public', 'readonly', 'ref', 'return', 'sbyte', 'sealed',
    'short', 'sizeof', 'stackalloc', 'static', 'string', 'struct', 'switch',
    'this', 'throw', 'true', 'try', 'typeof', 'uint', 'ulong', 'unchecked',
    'unsafe', 'ushort', 'using', 'virtual', 'void', 'volatile', 'while'
  ];

  if (csharpKeywords.includes(name.toLowerCase())) {
    throw new Error(`Addon name cannot be a C# keyword: ${name}`);
  }
}

/**
 * Checks if dotnet SDK is installed and available
 * @param {boolean} verbose - Enable verbose logging
 */
async function checkDotnetSdk(verbose) {
  // Try to find dotnet executable
  let dotnetPath = 'dotnet';
  
  try {
    // First try to use dotnet from PATH
    execSync('dotnet --version', { 
      encoding: 'utf8',
      stdio: 'pipe'
    });
  } catch (error) {
    // If not in PATH, try Program Files
    const programFiles = process.env.ProgramFiles || 'C:\\Program Files';
    const dotnetExePath = path.join(programFiles, 'dotnet', 'dotnet.exe');
    
    if (fsSync.existsSync(dotnetExePath)) {
      dotnetPath = dotnetExePath;
    } else {
      return false;
    }
  }
  
  try {
    const output = execSync(`"${dotnetPath}" --list-sdks`, { 
      encoding: 'utf8',
      stdio: verbose ? ['pipe', 'pipe', 'inherit'] : 'pipe'
    }).trim();
    
    // Look for a line in output that starts with "10.0"
    const sdkLines = output.split('\n');
    const hasDotnet10 = sdkLines.some(line => line.startsWith('10.0'));
    return hasDotnet10;    
  } catch (error) {
    return false;
  }
}

/**
 * Copies and processes template files to the addon directory and project root
 * @param {string} addonName - Name of the addon
 * @param {string} addonDir - Target addon directory
 * @param {string} projectRoot - Project root directory
 * @param {boolean} verbose - Enable verbose logging
 */
async function copyTemplateFiles(addonName, addonDir, projectRoot, verbose) {
  const templateDir = path.join(__dirname, 'cs-addon-template');
  
  if (!fsSync.existsSync(templateDir)) {
    throw new Error(`Template directory not found: ${templateDir}`);
  }

  // Process and copy .csproj file
  const csprojTemplate = path.join(templateDir, 'AddonTemplate.csproj.template');
  const csprojTarget = path.join(addonDir, `${addonName}.csproj`);
  
  let csprojContent = await fs.readFile(csprojTemplate, 'utf8');
  csprojContent = csprojContent.replace(/{AddonName}/g, addonName);
  
  await fs.writeFile(csprojTarget, csprojContent, 'utf8');
  
  if (verbose) {
    console.log(`📄 Created ${addonName}.csproj`);
  }

  // Process and copy addon.cs file
  const addonCsTemplate = path.join(templateDir, 'addon.cs.template');
  const addonCsTarget = path.join(addonDir, 'addon.cs');
  
  let addonCsContent = await fs.readFile(addonCsTemplate, 'utf8');
  addonCsContent = addonCsContent.replace(/{AddonName}/g, addonName);
  
  await fs.writeFile(addonCsTarget, addonCsContent, 'utf8');
  
  if (verbose) {
    console.log(`📄 Created addon.cs`);
  }

  // Process and copy README.md file
  const readmeTemplate = path.join(templateDir, 'README.md.template');
  const readmeTarget = path.join(addonDir, 'README.md');
  
  let readmeContent = await fs.readFile(readmeTemplate, 'utf8');
  readmeContent = readmeContent.replace(/{AddonName}/g, addonName);
  
  await fs.writeFile(readmeTarget, readmeContent, 'utf8');
  
  if (verbose) {
    console.log(`📄 Created README.md`);
  }

  // Copy Directory.packages.props to project root (if it doesn't exist)
  const packagePropsTarget = path.join(projectRoot, 'Directory.packages.props');
  if (!fsSync.existsSync(packagePropsTarget)) {
    const packagePropsTemplate = path.join(templateDir, 'Directory.packages.props.template');
    await fs.copyFile(packagePropsTemplate, packagePropsTarget);
    
    if (verbose) {
      console.log(`📄 Created Directory.packages.props`);
    }
  } else {
    if (verbose) {
      console.log(`📄 Directory.packages.props already exists, skipping`);
    }
  }
}

/**
 * Installs node-api-dotnet package
 * @param {string} projectRoot - Root directory of the project
 * @param {boolean} verbose - Enable verbose logging
 */
async function installNodeApiDotnet(projectRoot, verbose) {
  // Check if package.json exists
  const packageJsonPath = path.join(projectRoot, 'package.json');
  if (!fsSync.existsSync(packageJsonPath)) {
    throw new Error('package.json not found in project root');
  }

  // Read current package.json
  const packageJsonContent = await fs.readFile(packageJsonPath, 'utf8');
  const packageJson = JSON.parse(packageJsonContent);

  // Check if node-api-dotnet is already installed
  const dependencies = packageJson.dependencies || {};
  const devDependencies = packageJson.devDependencies || {};
  
  if (dependencies['node-api-dotnet'] || devDependencies['node-api-dotnet']) {
    if (verbose) {
      console.log(`✅ node-api-dotnet is already installed`);
    }
    return;
  }

  if (verbose) {
    console.log(`📦 Installing node-api-dotnet...`);
  }

  const installCommand = 'npm install node-api-dotnet';
  
  try {
    execSync(installCommand, { 
      cwd: projectRoot, 
      stdio: verbose ? 'inherit' : 'pipe' 
    });
    
    if (verbose) {
      console.log(`✅ node-api-dotnet installed successfully`);
    }
  } catch (error) {
    throw new Error(`Failed to install node-api-dotnet: ${error.message}`);
  }
}

/**
 * Adds build scripts to package.json
 * @param {string} addonName - Name of the addon
 * @param {string} projectRoot - Root directory of the project
 * @param {boolean} verbose - Enable verbose logging
 */
async function addCsBuildScripts(addonName, projectRoot, verbose) {
  const packageJsonPath = path.join(projectRoot, 'package.json');
  
  // Read current package.json
  const packageJsonContent = await fs.readFile(packageJsonPath, 'utf8');
  const packageJson = JSON.parse(packageJsonContent);

  // Initialize scripts if it doesn't exist
  if (!packageJson.scripts) {
    packageJson.scripts = {};
  }

  // Add build script - use publish to generate .node file
  const buildScriptName = `build-${addonName}`;
  // Use dotnet publish - RuntimeIdentifier is set in the .csproj with a default
  // Can be overridden with: npm run build-csAddon -- /p:RuntimeIdentifier=win-arm64
  const buildCommand = `dotnet publish ./${addonName}/${addonName}.csproj -c Release`;
  
  if (packageJson.scripts[buildScriptName]) {
    if (verbose) {
      console.log(`⚠️  Build script '${buildScriptName}' already exists, skipping`);
    }
  } else {
    packageJson.scripts[buildScriptName] = buildCommand;
    if (verbose) {
      console.log(`📝 Added build script: ${buildScriptName}`);
    }
  }

  // Add clean script (without cs- prefix)
  const cleanScriptName = `clean-${addonName}`;
  const cleanCommand = `dotnet clean ./${addonName}/${addonName}.csproj`;
  
  if (!packageJson.scripts[cleanScriptName]) {
    packageJson.scripts[cleanScriptName] = cleanCommand;
    if (verbose) {
      console.log(`📝 Added clean script: ${cleanScriptName}`);
    }
  }

  // Write back to package.json
  await fs.writeFile(packageJsonPath, JSON.stringify(packageJson, null, 2), 'utf8');
}

/**
 * Updates .gitignore to exclude C# build artifacts
 * @param {string} projectRoot - Root directory of the project
 * @param {boolean} verbose - Enable verbose logging
 */
async function updateGitignore(projectRoot, verbose) {
  const gitignorePath = path.join(projectRoot, '.gitignore');
  
  const entriesToAdd = [
    '',
    '# C# build artifacts',
    'bin/',
    'obj/',
    '*.user',
    '*.suo'
  ];

  let gitignoreContent = '';
  let exists = false;

  try {
    if (fsSync.existsSync(gitignorePath)) {
      gitignoreContent = await fs.readFile(gitignorePath, 'utf8');
      exists = true;
    }
  } catch (error) {
    // File doesn't exist, we'll create it
  }

  // Check if entries already exist
  const needsUpdate = entriesToAdd.some(entry => 
    entry && !gitignoreContent.includes(entry)
  );

  if (!needsUpdate && exists) {
    if (verbose) {
      console.log(`✅ .gitignore already contains C# build artifact entries`);
    }
    return;
  }

  // Add entries
  const newContent = gitignoreContent.trim() + '\n' + entriesToAdd.join('\n') + '\n';
  await fs.writeFile(gitignorePath, newContent, 'utf8');

  if (verbose) {
    console.log(`📝 Updated .gitignore with C# build artifact entries`);
  }
}

/**
 * Get the winget command line for installing .NET 10 SDK
 * @returns {string} The winget command line
 */
function getDotnet10SdkWingetCommand() {
  return 'winget install --id Microsoft.DotNet.SDK.10 --source winget';
}

/**
 * Check for .NET 10 SDK and offer to install if missing
 * @param {boolean} verbose - Enable verbose logging
 * @param {string} addonName - Name of the addon being created (for display purposes)
 */
async function checkAndInstallDotnet10Sdk(verbose = false, addonName = 'csAddon') {
  const hasDotnet10 = await checkDotnetSdk(verbose);
  if (!hasDotnet10) {
    console.log('.NET 10 SDK is required for C# addons but was not found.');
    console.log('');
    console.log('The following command will be run:');
    console.log('  ' + getDotnet10SdkWingetCommand());
    console.log('');
    
    // Check if we're in an interactive terminal
    if (process.stdin.isTTY) {
      const readline = require('readline');
      const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout
      });
      
      return new Promise((resolve) => {
        rl.question('Would you like to install it now using winget (user interaction may be required)? (y/N): ', async (answer) => {
          rl.close();
          
          if (answer.toLowerCase() === 'y') {
            console.log('');
            console.log('Installing .NET 10 SDK...');
            const success = await installDotnet10Sdk();
            
            if (!success) {
              console.error('❌ Failed to install .NET 10 SDK.');
              console.error('   Please install it manually from: https://dotnet.microsoft.com/download/dotnet/10.0');
              process.exit(1);
            } else {
              console.log('✅ .NET 10 SDK installed successfully!');
              console.log('');
              console.log('⚠️  IMPORTANT: You need to restart your terminal/command prompt for dotnet to be available in your PATH.');
              console.log('   After restarting, you can run: npm run build-' + addonName);
              console.log('');
            }
          } else {
            console.error('You can install it from: https://dotnet.microsoft.com/download/dotnet/10.0');
            process.exit(1);
          }
          resolve();
        });
      });
    } else {
      console.error('You can install it from: https://dotnet.microsoft.com/download/dotnet/10.0');
      process.exit(1);
    }
  }
}

/**
 * Install .NET 10 SDK using winget
 * @returns {Promise<boolean>} true if successful, false otherwise
 */
function installDotnet10Sdk() {
  return new Promise(resolve => {
    const { spawn } = require('child_process');
    
    // Use winget to install .NET 10 SDK
    const command = getDotnet10SdkWingetCommand();
    const winget = spawn(command, {
      stdio: 'inherit',
      shell: true
    });
    
    winget.on('close', (code) => {
      resolve(code === 0);
    });
    
    winget.on('error', (err) => {
      console.error(`Error running winget: ${err.message}`);
      resolve(false);
    });
  });
}

/**
 * Get the winget command line for installing Visual Studio Build Tools
 * @returns {string} The winget command line
 */
function getVisualStudioBuildToolsWingetCommand() {
  return 'winget install --id Microsoft.VisualStudio.Community --source winget --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --passive --wait"';
}

/**
 * Check for Visual Studio Build Tools and offer to install if missing
 * @param {boolean} verbose - Enable verbose logging
 */
async function checkAndInstallVisualStudioBuildTools(verbose = false) {
  const hasVisualStudioBuildTools = checkVisualStudioBuildTools();
  if (!hasVisualStudioBuildTools) {
    console.log('Visual Studio Build Tools are required for C# addons but were not found.');
    console.log('');
    console.log('The following command will be run:');
    console.log('  ' + getVisualStudioBuildToolsWingetCommand());
    console.log('');
    
    // Check if we're in an interactive terminal
    if (process.stdin.isTTY) {
      const readline = require('readline');
      const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout
      });
      
      return new Promise((resolve) => {
        rl.question('Would you like to install it now using winget (user interaction may be required)? (y/N): ', async (answer) => {
          rl.close();
          
          if (answer.toLowerCase() === 'y') {
            console.log('');
            console.log('Installing Visual Studio Build Tools...');
            const success = await installVisualStudioBuildTools();
            
            if (!success) {
              console.error('❌ Failed to install Visual Studio Build Tools.');
              console.error('   Please install it manually from: https://visualstudio.microsoft.com/downloads/');
              process.exit(1);
            } else {
              console.log('✅ Visual Studio Build Tools installed successfully!');
              console.log('');
              console.log('⚠️  IMPORTANT: You need to restart your terminal/command prompt for the build tools to be available.');
              console.log('');
            }
          } else {
            console.error('You can install it from: https://visualstudio.microsoft.com/downloads/');
            process.exit(1);
          }
          resolve();
        });
      });
    } else {
      console.error('You can install it from: https://visualstudio.microsoft.com/downloads/');
      process.exit(1);
    }
  }
}

/**
 * Check if Visual Studio Build Tools are installed
 * @returns {boolean} true if Visual Studio Build Tools are found, false otherwise
 */
function checkVisualStudioBuildTools() {
  try {
    // Use vswhere to find Visual Studio installations
    // vswhere is typically installed with Visual Studio and should be in PATH
    // or in Program Files
    const programFilesX86 = process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)';
    const vswherePath = path.join(programFilesX86, 'Microsoft Visual Studio', 'Installer', 'vswhere.exe');
    
    let vswhereCmd = 'vswhere.exe';
    
    // If vswhere is not in PATH, use the full path
    if (fsSync.existsSync(vswherePath)) {
      vswhereCmd = vswherePath;
    }
    
    try {
      // Use vswhere to find Visual Studio 2022, 2026, or later with BuildTools
      const output = execSync(`"${vswhereCmd}" -products * -requires Microsoft.VisualStudio.Workload.NativeDesktop -property installationPath`, { 
        encoding: 'utf8',
        stdio: 'pipe'
      }).trim();
      
      // If we got any output, VS with the required workload is installed
      if (output && output.length > 0) {
        return true;
      }
    } catch (error) {
      // vswhere not found or query failed, fall back to cl.exe check
    }
  } catch (error) {
    return false;
  }
  return false;
}

/**
 * Install Visual Studio Build Tools using winget
 * @returns {Promise<boolean>} true if successful, false otherwise
 */
function installVisualStudioBuildTools() {
  return new Promise(resolve => {
    const { spawn } = require('child_process');
    
    // Use winget to install Visual Studio Community with Native Desktop workload
    const command = getVisualStudioBuildToolsWingetCommand();
    const winget = spawn(command, {
      stdio: 'inherit',
      shell: true
    });
    
    winget.on('close', (code) => {
      resolve(code === 0);
    });
    
    winget.on('error', (err) => {
      console.error(`Error running winget: ${err.message}`);
      resolve(false);
    });
  });
}

module.exports = {
  generateCsAddonFiles,
  checkAndInstallDotnet10Sdk,
  checkAndInstallVisualStudioBuildTools
};
