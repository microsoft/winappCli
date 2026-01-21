import * as fs from 'fs';
import * as path from 'path';
import { execSync, exec, spawn } from 'child_process';

/**
 * Refresh the PATH environment variable from the system
 * This ensures we can detect newly installed tools without restarting the terminal
 */
function refreshPath(): void {
  try {
    // On Windows, refresh PATH from registry
    if (process.platform === 'win32') {
      // Try to get the updated PATH from the system
      // We'll do this synchronously since it's quick
      try {
        const machinePath = execSync(
          "powershell -NoProfile -Command \"[Environment]::GetEnvironmentVariable('Path', 'Machine')\"",
          { encoding: 'utf8', stdio: 'pipe' }
        ).trim();

        const userPath = execSync(
          "powershell -NoProfile -Command \"[Environment]::GetEnvironmentVariable('Path', 'User')\"",
          { encoding: 'utf8', stdio: 'pipe' }
        ).trim();

        // Combine and update process.env.PATH
        if (machinePath || userPath) {
          process.env.PATH = [machinePath, userPath, process.env.PATH].filter(Boolean).join(path.delimiter);
        }
      } catch {
        // If PowerShell fails, silently continue with existing PATH
      }
    }
  } catch {
    // Silently fail - we'll just use the existing PATH
  }
}

/**
 * Checks if dotnet SDK is installed and available
 * @param version - Version of the .NET SDK to check for
 * @param verbose - Enable verbose logging
 */
async function checkDotnetSdk(version: string, verbose: boolean): Promise<boolean> {
  // Refresh PATH to pick up any newly installed tools
  refreshPath();

  // Try to find dotnet executable
  let dotnetPath = 'dotnet';

  try {
    // First try to use dotnet from PATH
    execSync('dotnet --version', {
      encoding: 'utf8',
      stdio: 'pipe',
    });
  } catch {
    // If not in PATH, try Program Files
    const programFiles = process.env.ProgramFiles || 'C:\\Program Files';
    const dotnetExePath = path.join(programFiles, 'dotnet', 'dotnet.exe');

    if (fs.existsSync(dotnetExePath)) {
      dotnetPath = dotnetExePath;
    } else {
      return false;
    }
  }

  try {
    const output = execSync(`"${dotnetPath}" --list-sdks`, {
      encoding: 'utf8',
      stdio: verbose ? ['pipe', 'pipe', 'inherit'] : 'pipe',
    }).trim();

    // Look for a line in output that starts with "${version}.0"
    const sdkLines = output.split('\n');
    const hasDotnet = sdkLines.some((line) => line.startsWith(`${version}.0`));
    return hasDotnet;
  } catch {
    return false;
  }
}

/**
 * Get the winget command line for installing .NET SDK
 * @param version - Version of the .NET SDK to install
 * @returns The winget command line
 */
function getDotnetSdkWingetCommand(version: string): string {
  return `winget install --id Microsoft.DotNet.SDK.${version} --source winget`;
}

/**
 * Install .NET SDK using winget
 * @param version - Version of the .NET SDK to install
 * @returns true if successful, false otherwise
 */
function installDotnetSdk(version: string): Promise<boolean> {
  return new Promise((resolve) => {
    // Use winget to install .NET SDK
    const command = getDotnetSdkWingetCommand(version);
    const winget = spawn(command, {
      stdio: 'inherit',
      shell: true,
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
 * @param version - Version of the .NET SDK to check for (default: "10")
 * @param verbose - Enable verbose logging
 * @returns true if something was installed, false otherwise
 */
export async function checkAndInstallDotnetSdk(version: string = '10', verbose: boolean = false): Promise<boolean> {
  const hasDotnetSdk = await checkDotnetSdk(version, verbose);
  const dotnetDownloadUrl = `https://dotnet.microsoft.com/download/dotnet/${version}.0`;

  if (!hasDotnetSdk) {
    // Check if we're in an interactive terminal
    if (process.stdin.isTTY) {
      const readline = await import('readline');
      const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout,
      });

      return new Promise((resolve) => {
        rl.question(
          `❓ .NET ${version} SDK is not installed - install Microsoft.DotNet.SDK.${version} with winget (user interaction may be required)? (y/N): `,
          async (answer) => {
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
          }
        );
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
 * @returns true if Visual Studio Build Tools are found, false otherwise
 */
function checkVisualStudioBuildTools(): boolean {
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
    if (fs.existsSync(vswherePath)) {
      vswhereCmd = vswherePath;
    }

    try {
      // Use vswhere to find Visual Studio 2022, 2026, or later with BuildTools
      const output = execSync(
        `"${vswhereCmd}" -products * -requires Microsoft.VisualStudio.Workload.NativeDesktop -property installationPath`,
        {
          encoding: 'utf8',
          stdio: 'pipe',
        }
      ).trim();

      // If we got any output, VS with the required workload is installed
      if (output && output.length > 0) {
        return true;
      }
    } catch {
      // vswhere not found or query failed, fall back to cl.exe check
    }
  } catch {
    return false;
  }
  return false;
}

/**
 * Get the winget command line for installing Visual Studio Build Tools
 * @returns The winget command line
 */
function getVisualStudioBuildToolsWingetCommand(): string {
  const components = ['Microsoft.VisualStudio.Workload.NativeDesktop'];

  const addFlags = components.map((c) => `--add ${c}`).join(' ');
  return `winget install --id Microsoft.VisualStudio.Community --source winget --force --override "${addFlags} --passive --includeRecommended --wait"`;
}

/**
 * Install Visual Studio Build Tools using winget
 * @returns true if successful, false otherwise
 */
function installVisualStudioBuildTools(): Promise<boolean> {
  return new Promise((resolve) => {
    // Use winget to install Visual Studio Community with Native Desktop workload
    const command = getVisualStudioBuildToolsWingetCommand();
    const winget = spawn(command, {
      stdio: 'inherit',
      shell: true,
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
 * @param _verbose - Enable verbose logging (reserved for future use)
 * @returns true if something was installed, false otherwise
 */
export async function checkAndInstallVisualStudioBuildTools(_verbose: boolean = false): Promise<boolean> {
  const hasVisualStudioBuildTools = checkVisualStudioBuildTools();
  const vsDownloadUrl = 'https://aka.ms/vs/download';

  if (!hasVisualStudioBuildTools) {
    // Check if we're in an interactive terminal
    if (process.stdin.isTTY) {
      const readline = await import('readline');
      const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout,
      });

      return new Promise((resolve) => {
        rl.question(
          '❓ Visual Studio Build Tools are not installed - install Microsoft.VisualStudio.Community with winget (user interaction may be required)? (y/N): ',
          async (answer) => {
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
          }
        );
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
 * @returns true if Python is found, false otherwise
 */
async function checkPython(): Promise<boolean> {
  // Refresh PATH to pick up any newly installed tools
  refreshPath();

  const commands = ['python --version', 'python3 --version', 'py --version'];

  return new Promise((resolve) => {
    let index = 0;

    function tryNext(): void {
      if (index >= commands.length) {
        return resolve(false);
      }

      exec(commands[index], (err) => {
        if (!err) {
          return resolve(true);
        }
        index++;
        tryNext();
      });
    }

    tryNext();
  });
}

/**
 * Get the winget command line for installing Python
 * @returns The winget command line
 */
function getPythonWingetCommand(): string {
  return 'winget install --id Python.PythonInstallManager --source winget';
}

/**
 * Install Python using winget
 * @returns true if successful, false otherwise
 */
function installPython(): Promise<boolean> {
  return new Promise((resolve) => {
    // Use winget to install Python
    const command = getPythonWingetCommand();
    const winget = spawn(command, {
      stdio: 'inherit',
      shell: true,
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
 * @param _verbose - Enable verbose logging (reserved for future use)
 * @returns true if something was installed, false otherwise
 */
export async function checkAndInstallPython(_verbose: boolean = false): Promise<boolean> {
  const hasPython = await checkPython();
  if (!hasPython) {
    // Check if we're in an interactive terminal
    if (process.stdin.isTTY) {
      const readline = await import('readline');
      const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout,
      });

      return new Promise((resolve) => {
        rl.question(
          '❓ Python is not installed - install Python.PythonInstallManager with winget (user interaction may be required)? (y/N): ',
          async (answer) => {
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
          }
        );
      });
    } else {
      console.error('❌ Python is not installed - you can install it from: https://www.python.org/downloads/');
      process.exit(1);
    }
  }
  return false;
}
