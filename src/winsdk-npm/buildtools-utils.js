const { execSync } = require("child_process");
const path = require("path");
const fs = require("fs");
const os = require("os");
const { downloadAndExtractNuGetPackage, getPackagePath } = require("./nuget-utils");

const PACKAGE_NAME = 'Microsoft.Windows.SDK.BuildTools';

async function downloadAndExtractBuildTools() {
  try {
    // Download to default location
    const result = await downloadAndExtractNuGetPackage(PACKAGE_NAME);
    
    console.log('BuildTools extraction completed!');
    console.log(`Package available at: ${result.path}`);
    
    return result.path;

  } catch (error) {
    console.error('Error downloading BuildTools:', error.message);
    throw error;
  }
}

function getCurrentArchitecture() {
  const arch = os.arch();
  
  // Map Node.js architecture names to BuildTools folder names
  switch (arch) {
    case 'x64':
      return 'x64';
    case 'ia32':
    case 'x32':
      return 'x86';
    case 'arm64':
      return 'arm64';
    case 'arm':
      return 'arm64'; // Use arm64 as fallback for arm
    default:
      // Fallback order: prefer x64, then x86, then arm64
      return ['x64', 'x86', 'arm64'];
  }
}

async function findBuildToolsBinPath() {
  // First check if BuildTools package is already downloaded
  let buildToolsPath = getPackagePath(PACKAGE_NAME);
  
  // If not found, download and extract BuildTools
  if (!buildToolsPath) {
    console.log(`${PACKAGE_NAME} not found. Downloading...`);
    const result = await downloadAndExtractBuildTools();
    buildToolsPath = result;
  }
  
  if (!buildToolsPath || !fs.existsSync(buildToolsPath)) {
    throw new Error(`Could not find ${PACKAGE_NAME} folder even after download`);
  }
  
  const binPath = path.join(buildToolsPath, "bin");
  if (!fs.existsSync(binPath)) {
    throw new Error(`Could not find bin folder in ${buildToolsPath}`);
  }
  
  // Find the version folder (should be something like 10.0.26100.0)
  const versionFolders = fs.readdirSync(binPath)
    .filter(item => fs.statSync(path.join(binPath, item)).isDirectory())
    .filter(item => /^\d+\.\d+\.\d+\.\d+$/.test(item));
  
  if (versionFolders.length === 0) {
    throw new Error(`Could not find version folder in ${binPath}`);
  }
  
  // Use the latest version (sort by version number)
  const latestVersion = versionFolders.sort((a, b) => {
    const aParts = a.split('.').map(Number);
    const bParts = b.split('.').map(Number);
    for (let i = 0; i < 4; i++) {
      if (aParts[i] !== bParts[i]) {
        return bParts[i] - aParts[i]; // Descending order
      }
    }
    return 0;
  })[0];
  
  const versionPath = path.join(binPath, latestVersion);
  
  // Determine architecture based on current machine
  const currentArch = getCurrentArchitecture();
  let archPath = null;
  
  if (Array.isArray(currentArch)) {
    // Fallback order for unknown architectures
    for (const arch of currentArch) {
      const candidateArchPath = path.join(versionPath, arch);
      if (fs.existsSync(candidateArchPath)) {
        archPath = candidateArchPath;
        break;
      }
    }
  } else {
    // Use the detected architecture
    const candidateArchPath = path.join(versionPath, currentArch);
    if (fs.existsSync(candidateArchPath)) {
      archPath = candidateArchPath;
    } else {
      // If the detected architecture isn't available, fall back to common architectures
      const fallbackArchs = ['x64', 'x86', 'arm64'];
      for (const arch of fallbackArchs) {
        if (arch !== currentArch) { // Skip the one we already tried
          const fallbackArchPath = path.join(versionPath, arch);
          if (fs.existsSync(fallbackArchPath)) {
            archPath = fallbackArchPath;
            console.warn(`Warning: Using ${arch} build tools instead of preferred ${currentArch}`);
            break;
          }
        }
      }
    }
  }
  
  if (!archPath) {
    throw new Error(`Could not find architecture folder in ${versionPath}`);
  }
  
  return archPath;
}

/**
 * Execute a command with BuildTools bin path added to PATH environment
 * @param {string} command - The command to execute
 * @param {object} options - Options to pass to execSync (optional)
 * @returns {Buffer} - The output from execSync
 */
async function execSyncWithBuildTools(command, options = {}) {
  const buildToolsBinPath = await findBuildToolsBinPath();
  
  // Get current PATH and prepend the BuildTools bin path
  const currentPath = process.env.PATH || '';
  const newPath = `${buildToolsBinPath}${path.delimiter}${currentPath}`;
  
  // Merge the new PATH with existing environment variables
  const env = {
    ...process.env,
    ...options.env,
    PATH: newPath
  };
  
  // Execute the command with the updated environment
  return execSync(command, {
    ...options,
    env
  });
}

/**
 * Get the full path to a specific BuildTools executable
 * @param {string} toolName - Name of the tool (e.g., 'mt.exe', 'signtool.exe')
 * @returns {string} - Full path to the executable
 */
async function getBuildToolPath(toolName) {
  const binPath = await findBuildToolsBinPath();
  const toolPath = path.join(binPath, toolName);
  
  if (!fs.existsSync(toolPath)) {
    throw new Error(`Could not find ${toolName} in ${binPath}`);
  }
  
  return toolPath;
}

/**
 * Ensure BuildTools are available and return the bin path
 * @returns {string} - Path to the BuildTools bin directory
 */
async function ensureBuildTools() {
  return await findBuildToolsBinPath();
}

module.exports = {
  execSyncWithBuildTools,
  getBuildToolPath,
  findBuildToolsBinPath,
  getCurrentArchitecture,
  ensureBuildTools,
  downloadAndExtractBuildTools
};
