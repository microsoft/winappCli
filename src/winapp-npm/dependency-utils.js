const fsSync = require('fs');
const path = require('path');
const { execSync } = require('child_process');

/**
 * Refresh the PATH environment variable from the system
 * This ensures we can detect newly installed tools without restarting the terminal
 */
function refreshPath() {
  try {
    // On Windows, refresh PATH from registry
    if (process.platform === 'win32') {
      const { exec } = require('child_process');
      
      // Try to get the updated PATH from the system
      // We'll do this synchronously since it's quick
      try {
        const machinePath = execSync(
          'powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable(\'Path\', \'Machine\')"',
          { encoding: 'utf8', stdio: 'pipe' }
        ).trim();
        
        const userPath = execSync(
          'powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable(\'Path\', \'User\')"',
          { encoding: 'utf8', stdio: 'pipe' }
        ).trim();
        
        // Combine and update process.env.PATH
        if (machinePath || userPath) {
          process.env.PATH = [machinePath, userPath, process.env.PATH].filter(Boolean).join(path.delimiter);
        }
      } catch (error) {
        // If PowerShell fails, silently continue with existing PATH
      }
    }
  } catch (error) {
    // Silently fail - we'll just use the existing PATH
  }
}

/**
 * Checks if dotnet SDK is installed and available
 * @param {string} version - Version of the .NET SDK to check for
 * @param {boolean} verbose - Enable verbose logging
 */
async function checkDotnetSdk(version, verbose) {
  // Refresh PATH to pick up any newly installed tools
  refreshPath();
  
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
    
    // Look for a line in output that starts with "${version}.0"
    const sdkLines = output.split('\n');
    const hasDotnet = sdkLines.some(line => line.startsWith(`${version}.0`));
    return hasDotnet;    
  } catch (error) {
    return false;
  }
}

/**
 * Get the winget command line for installing .NET SDK
 * @param {string} version - Version of the .NET SDK to install
 * @returns {string} The winget command line
 */
function getDotnetSdkWingetCommand(version) {
  return `winget install --id Microsoft.DotNet.SDK.${version} --source winget`;
}

/**
 * Install .NET SDK using winget
 * @param {string} version - Version of the .NET SDK to install
 * @returns {Promise<boolean>} true if successful, false otherwise
 */
function installDotnetSdk(version) {
  return new Promise(resolve => {
    const { spawn } = require('child_process');
    
    // Use winget to install .NET SDK
    const command = getDotnetSdkWingetCommand(version);
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
 * Check for .NET SDK and offer to install if missing
 * @param {string} version - Version of the .NET SDK to check for (default: "10")
 * @param {boolean} verbose - Enable verbose logging
 * @returns {Promise<boolean>} true if something was installed, false otherwise
 */
async function checkAndInstallDotnetSdk(version = "10", verbose = false) {
  const hasDotnetSdk = await checkDotnetSdk(version, verbose);
  if (!hasDotnetSdk) {
    // Check if we're in an interactive terminal
    if (process.stdin.isTTY) {
      const readline = require('readline');
      const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout
      });

      const dotnetDownloadUrl = `https://dotnet.microsoft.com/download/dotnet/${version}.0`;
      
      return new Promise((resolve) => {
        rl.question(`❓ .NET ${version} SDK is not installed - install Microsoft.DotNet.SDK.${version} with winget (user interaction may be required)? (y/N): `, async (answer) => {
          rl.close();
          
          if (answer.toLowerCase() === 'y') {
            console.log('');
            console.log(`Installing with \`${getDotnetSdkWingetCommand(version)}\``);
            const success = await installDotnetSdk(version);
            
            if (!success) {
              console.error(`❌ Failed to install .NET ${version} SDK.`);
              console.error(`   Please install it manually from: ${dotnetDownloadUrl}`);
              process.exit(1);
            } else {
              console.log(`✅ .NET ${version} SDK installed successfully!`);
              console.log('');
            }
            resolve(true);
          } else {
            console.error(`You can install it from: ${dotnetDownloadUrl}`);
            process.exit(1);
          }
          resolve(false);
        });
      });
    } else {
      console.error(`❌ .NET ${version} SDK is not installed - you can install it from: ${dotnetDownloadUrl}`);
      process.exit(1);
    }
  }
  return false;
}

/**
 * Check if Visual Studio Build Tools are installed
 * @returns {boolean} true if Visual Studio Build Tools are found, false otherwise
 */
function checkVisualStudioBuildTools() {
  // Refresh PATH to pick up any newly installed tools
  refreshPath();
  
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
 * Get the winget command line for installing Visual Studio Build Tools
 * @returns {string} The winget command line
 */
function getVisualStudioBuildToolsWingetCommand() {
  const components = [
    'Microsoft.VisualStudio.Workload.NativeDesktop'
  ];
  
  const addFlags = components.map(c => `--add ${c}`).join(' ');
  return `winget install --id Microsoft.VisualStudio.Community --source winget --force --override "${addFlags} --passive --includeRecommended --wait"`;
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

/**
 * Check for Visual Studio Build Tools and offer to install if missing
 * @param {boolean} verbose - Enable verbose logging
 * @returns {Promise<boolean>} true if something was installed, false otherwise
 */
async function checkAndInstallVisualStudioBuildTools(verbose = false) {
  const hasVisualStudioBuildTools = checkVisualStudioBuildTools();
  if (!hasVisualStudioBuildTools) {
    // Check if we're in an interactive terminal
    if (process.stdin.isTTY) {
      const readline = require('readline');
      const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout
      });

      const vsDownloadUrl = 'https://aka.ms/vs/download';
      
      return new Promise((resolve) => {
        rl.question('❓ Visual Studio Build Tools are not installed - install Microsoft.VisualStudio.Community with winget (user interaction may be required)? (y/N): ', async (answer) => {
          rl.close();
          
          if (answer.toLowerCase() === 'y') {
            console.log('');
            console.log(`Installing with \`${getVisualStudioBuildToolsWingetCommand()}\``);
            const success = await installVisualStudioBuildTools();
            
            if (!success) {
              console.error('❌ Failed to install Visual Studio Build Tools.');
              console.error(`   Please install it manually from: ${vsDownloadUrl}`);
              process.exit(1);
            } else {
              console.log('✅ Visual Studio Build Tools installed successfully!');
              console.log('');
            }
            resolve(true);
          } else {
            console.error(`You can install it from: ${vsDownloadUrl}`);
            process.exit(1);
          }
          resolve(false);
        });
      });
    } else {
      console.error(`❌ Visual Studio Build Tools are not installed - you can install them from: ${vsDownloadUrl}`);
      process.exit(1);
    }
  }
  return false;
}

/**
 * Check if Python is installed and available
 * @returns {Promise<boolean>} true if Python is found, false otherwise
 */
async function checkPython() {
  // Refresh PATH to pick up any newly installed tools
  refreshPath();
  
  const { exec } = require('child_process');
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

/**
 * Get the winget command line for installing Python
 * @returns {string} The winget command line
 */
function getPythonWingetCommand() {
  return 'winget install --id Python.PythonInstallManager --source winget';
}

/**
 * Install Python using winget
 * @returns {Promise<boolean>} true if successful, false otherwise
 */
function installPython() {
  return new Promise(resolve => {
    const { spawn } = require('child_process');
    
    // Use winget to install Python
    const command = getPythonWingetCommand();
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
 * Check for Python and offer to install if missing
 * @param {boolean} verbose - Enable verbose logging
 * @returns {Promise<boolean>} true if something was installed, false otherwise
 */
async function checkAndInstallPython(verbose = false) {
  const hasPython = await checkPython();
  if (!hasPython) {
    // Check if we're in an interactive terminal
    if (process.stdin.isTTY) {
      const readline = require('readline');
      const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout
      });
      
      return new Promise((resolve) => {
        rl.question('❓ Python is not installed - install Python.PythonInstallManager with winget (user interaction may be required)? (y/N): ', async (answer) => {
          rl.close();
          
          if (answer.toLowerCase() === 'y') {
            console.log('');
            console.log(`Installing with \`${getPythonWingetCommand()}\``);
            const success = await installPython();
            
            if (!success) {
              console.error('❌ Failed to install Python.');
              console.error('   Please install it manually from: https://www.python.org/downloads/');
              process.exit(1);
            } else {
              console.log('✅ Python installed successfully!');
              console.log('');
            }
            resolve(true);
          } else {
            console.error('You can install it from: https://www.python.org/downloads/');
            process.exit(1);
          }
          resolve(false);
        });
      });
    } else {
      console.error('❌ Python is not installed - you can install it from: https://www.python.org/downloads/');
      process.exit(1);
    }
  }
  return false;
}

module.exports = {
  checkAndInstallDotnetSdk,
  checkAndInstallVisualStudioBuildTools,
  checkAndInstallPython
};
