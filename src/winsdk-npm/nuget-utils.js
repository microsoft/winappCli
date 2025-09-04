const fs = require("fs");
const https = require("https");
const path = require("path");
const { spawn, execSync } = require("child_process");
const { getProjectRootDir } = require("./utils");

const NUGET_API_BASE = 'https://api.nuget.org/v3-flatcontainer';
const NUGET_EXE_URL = 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe';

/**
 * Get all available versions of a NuGet package
 * @param {string} packageName - The name of the NuGet package
 * @returns {Promise<string[]>} - Array of version strings
 */
async function getNuGetPackageVersions(packageName) {
  return new Promise((resolve, reject) => {
    const url = `${NUGET_API_BASE}/${packageName.toLowerCase()}/index.json`;
    
    https.get(url, (res) => {
      let data = '';
      
      res.on('data', (chunk) => {
        data += chunk;
      });
      
      res.on('end', () => {
        try {
          const response = JSON.parse(data);
          resolve(response.versions);
        } catch (error) {
          reject(error);
        }
      });
    }).on('error', reject);
  });
}

/**
 * Remove old versions of a package, keeping only the specified version
 * @param {string} packageName - Name of the NuGet package
 * @param {string} keepVersion - Version to keep
 * @param {string} [packagesDir] - Directory containing packages (defaults to ".winsdk/packages" in project root)
 * @param {boolean} [verbose=true] - Whether to log progress messages
 * @returns {Promise<string[]>} - Array of removed package paths
 */
async function cleanupOldPackageVersions(packageName, keepVersion, packagesDir, verbose = true) {
  // Default packagesDir to ".winsdk/packages" folder in project root
  if (!packagesDir) {
    try {
      const projectRoot = getProjectRootDir();
      packagesDir = path.join(projectRoot, ".winsdk", "packages");
    } catch (error) {
      if (verbose) {
        console.warn(`Could not determine project root directory: ${error.message}`);
      }
      return [];
    }
  }

  const removedPaths = [];

  try {
    if (!fs.existsSync(packagesDir)) {
      return removedPaths;
    }

    const entries = fs.readdirSync(packagesDir);
    const packagePattern = new RegExp(`^${packageName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\.(.+)$`);
    
    for (const entry of entries) {
      const match = entry.match(packagePattern);
      if (match) {
        const foundVersion = match[1];
        const entryPath = path.join(packagesDir, entry);
        
        // Skip if this is the version we want to keep
        if (foundVersion === keepVersion) {
          continue;
        }
        
        // Check if this is actually a directory for this package
        const stat = fs.statSync(entryPath);
        if (stat.isDirectory()) {
          if (verbose) {
            console.log(`🗑️  Removing old version: ${packageName} v${foundVersion}`);
          }
          
          try {
            fs.rmSync(entryPath, { recursive: true, force: true });
            removedPaths.push(entryPath);
            
            if (verbose) {
              console.log(`✅ Removed: ${entryPath}`);
            }
          } catch (error) {
            if (verbose) {
              console.warn(`⚠️  Could not remove ${entryPath}: ${error.message}`);
            }
          }
        }
      }
    }

    // Also clean up any .nupkg files for old versions
    for (const entry of entries) {
      if (entry.startsWith(packageName + '.') && entry.endsWith('.nupkg')) {
        const versionMatch = entry.match(new RegExp(`^${packageName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\.(.*)\\.nupkg$`));
        if (versionMatch) {
          const foundVersion = versionMatch[1];
          if (foundVersion !== keepVersion) {
            const nupkgPath = path.join(packagesDir, entry);
            try {
              fs.unlinkSync(nupkgPath);
              if (verbose) {
                console.log(`🗑️  Removed old .nupkg: ${entry}`);
              }
            } catch (error) {
              if (verbose) {
                console.warn(`⚠️  Could not remove ${nupkgPath}: ${error.message}`);
              }
            }
          }
        }
      }
    }

  } catch (error) {
    if (verbose) {
      console.warn(`⚠️  Error during cleanup of ${packageName}: ${error.message}`);
    }
  }

  return removedPaths;
}

/**
 * Compare two version strings
 * @param {string} a - First version
 * @param {string} b - Second version
 * @returns {number} - -1 if a < b, 0 if a == b, 1 if a > b
 */
function compareVersions(a, b) {
  const aParts = a.split('.').map(Number);
  const bParts = b.split('.').map(Number);
  
  for (let i = 0; i < Math.max(aParts.length, bParts.length); i++) {
    const aPart = aParts[i] || 0;
    const bPart = bParts[i] || 0;
    
    if (aPart > bPart) return 1;
    if (aPart < bPart) return -1;
  }
  
  return 0;
}

/**
 * Get the latest stable version from an array of versions
 * @param {string[]} versions - Array of version strings
 * @param {boolean} [includeExperimental=false] - Whether to include experimental versions (containing '-')
 * @returns {string} - Latest stable or experimental version
 */
function getLatestVersion(versions, includeExperimental = false) {
  if (includeExperimental) {
    // Include all versions (stable and experimental)
    return versions.sort(compareVersions).pop();
  } else {
    // Filter out prerelease/experimental versions (those containing '-')
    const stableVersions = versions.filter(v => !v.includes('-'));
    
    if (stableVersions.length === 0) {
      // If no stable versions, use all versions
      return versions.sort(compareVersions).pop();
    }
    
    return stableVersions.sort(compareVersions).pop();
  }
}

/**
 * Download a file from a URL
 * @param {string} url - The URL to download from
 * @param {string} outputPath - The local path to save the file
 * @returns {Promise<void>}
 */
async function downloadFile(url, outputPath) {
  return new Promise((resolve, reject) => {
    const file = fs.createWriteStream(outputPath);
    
    https.get(url, (response) => {
      if (response.statusCode === 200) {
        response.pipe(file);
        
        file.on('finish', () => {
          file.close();
          resolve();
        });
        
        file.on('error', (error) => {
          fs.unlink(outputPath, () => {}); // Delete the file on error
          reject(error);
        });
      } else {
        file.close();
        fs.unlink(outputPath, () => {}); // Delete the file on error
        reject(new Error(`HTTP ${response.statusCode}: ${response.statusMessage}`));
      }
    }).on('error', (error) => {
      file.close();
      fs.unlink(outputPath, () => {}); // Delete the file on error
      reject(error);
    });
  });
}

/**
 * Extract a ZIP file (including .nupkg files) using PowerShell
 * @param {string} zipPath - Path to the ZIP file
 * @param {string} extractPath - Path to extract to
 * @returns {Promise<void>}
 */
function extractZip(zipPath, extractPath) {
  return new Promise((resolve, reject) => {
    try {
      // Create extract directory
      if (!fs.existsSync(extractPath)) {
        fs.mkdirSync(extractPath, { recursive: true });
      }

      // NuGet packages are ZIP files, but PowerShell doesn't recognize .nupkg extension
      // So we'll rename it to .zip temporarily
      const tempZipPath = zipPath.replace('.nupkg', '.zip');
      fs.renameSync(zipPath, tempZipPath);

      // Use PowerShell to extract the ZIP file on Windows
      const powershellCommand = `Expand-Archive -Path "${tempZipPath}" -DestinationPath "${extractPath}" -Force`;
      
      const powershell = spawn('powershell.exe', ['-NoProfile', '-Command', powershellCommand], {
        stdio: 'pipe'
      });
      
      let output = '';
      let errorOutput = '';
      
      powershell.stdout.on('data', (data) => {
        output += data.toString();
      });
      
      powershell.stderr.on('data', (data) => {
        errorOutput += data.toString();
      });
      
      powershell.on('close', (code) => {
        // Clean up the temporary zip file
        try {
          if (fs.existsSync(tempZipPath)) {
            fs.unlinkSync(tempZipPath);
          }
        } catch (e) {
          // Ignore cleanup errors
        }
        
        if (code === 0) {
          resolve();
        } else {
          reject(new Error(`PowerShell extraction failed with code ${code}: ${errorOutput}`));
        }
      });
      
      powershell.on('error', (error) => {
        // Clean up the temporary zip file
        try {
          if (fs.existsSync(tempZipPath)) {
            fs.unlinkSync(tempZipPath);
          }
        } catch (e) {
          // Ignore cleanup errors
        }
        reject(error);
      });
      
    } catch (error) {
      reject(error);
    }
  });
}

/**
 * Download and ensure nuget.exe is available
 * @param {boolean} [verbose=true] - Whether to log progress messages
 * @returns {Promise<string>} - Returns the path to nuget.exe
 */
async function ensureNuGetExe(verbose = true) {
  try {
    const projectRoot = getProjectRootDir();
    const nugetDir = path.join(projectRoot, ".winsdk", "tools");
    const nugetExePath = path.join(nugetDir, "nuget.exe");
    
    // Check if nuget.exe already exists
    if (fs.existsSync(nugetExePath)) {
      return nugetExePath;
    }
    
    // Create tools directory if it doesn't exist
    if (!fs.existsSync(nugetDir)) {
      fs.mkdirSync(nugetDir, { recursive: true });
    }
    
    if (verbose) {
      console.log('Downloading nuget.exe...');
    }
    
    await downloadFile(NUGET_EXE_URL, nugetExePath);
    
    if (verbose) {
      console.log(`nuget.exe downloaded to: ${nugetExePath}`);
    }
    
    return nugetExePath;
  } catch (error) {
    throw new Error(`Failed to download nuget.exe: ${error.message}`);
  }
}

/**
 * Download and extract a NuGet package using nuget.exe
 * @param {string} packageName - Name of the NuGet package
 * @param {string} [extractPath] - Directory to extract the package to (defaults to ".winsdk/packages" in project root)
 * @param {Object} options - Options object
 * @param {string} [options.version] - Specific version to download (defaults to latest stable)
 * @param {boolean} [options.verbose=true] - Whether to log progress messages
 * @param {boolean} [options.includeExperimental=false] - Whether to include experimental versions when finding latest version
 * @param {boolean} [options.cleanupOldVersions=true] - Whether to remove old versions of the package after downloading
 * @returns {Promise<Object>} - Returns an object with version and path of the extracted package
 */
async function downloadAndExtractNuGetPackageWithExe(packageName, extractPath, options = {}) {
  // Default extractPath to ".winsdk/packages" folder in project root
  if (!extractPath) {
    try {
      const projectRoot = getProjectRootDir();
      extractPath = path.join(projectRoot, ".winsdk", "packages");
    } catch (error) {
      throw new Error(`Could not determine project root directory (no package.json found). Please specify an extractPath explicitly. Original error: ${error.message}`);
    }
  }
  
  const {
    version = null,
    verbose = true,
    includeExperimental = false,
    cleanupOldVersions = true
  } = options;

  try {
    // Ensure nuget.exe is available
    const nugetExePath = await ensureNuGetExe(verbose);
    
    // Determine which version to download
    let targetVersion;
    if (version) {
      targetVersion = version;
    } else {
      if (verbose) console.log(`Fetching versions for ${packageName}...`);
      const versions = await getNuGetPackageVersions(packageName);
      
      if (!versions || versions.length === 0) {
        throw new Error(`No versions found for package ${packageName}`);
      }
      
      targetVersion = getLatestVersion(versions, includeExperimental);
    }
    
    if (verbose) console.log(`Target version: ${targetVersion}`);
    
    // Create extract path using nuget.exe format: PackageName.Version
    const versionedExtractPath = path.join(extractPath, `${packageName}.${targetVersion}`);
      // Check if package already exists
    if (fs.existsSync(versionedExtractPath)) {
      if (verbose) {
        console.log(`Package ${packageName} v${targetVersion} already exists at ${versionedExtractPath}`);
      }
      return {
        version: targetVersion,
        path: versionedExtractPath
      };
    }

    // Clean up old versions before downloading new one
    if (cleanupOldVersions) {
      if (verbose) {
        console.log(`🧹 Cleaning up old versions of ${packageName}...`);
      }
      const removedPaths = await cleanupOldPackageVersions(packageName, targetVersion, extractPath, verbose);
      if (removedPaths.length > 0 && verbose) {
        console.log(`🗑️  Cleaned up ${removedPaths.length} old version(s) of ${packageName}`);
      }
    }

    if (verbose) {
      console.log(`Downloading ${packageName} v${targetVersion} using nuget.exe...`);
    }
    
    // Build nuget.exe command - it will create PackageName.Version folder automatically
    const versionArg = targetVersion ? `-Version ${targetVersion}` : '';
    const command = `"${nugetExePath}" install ${packageName} ${versionArg} -OutputDirectory "${extractPath}" -NonInteractive -DirectDownload`;
    
    if (verbose) {
      console.log(`Command: ${command}`);
    }

    // Execute nuget.exe
    const result = execSync(command, { 
      stdio: verbose ? 'inherit' : 'pipe',
      encoding: 'utf8'
    });
    
    // Verify the package was downloaded
    if (!fs.existsSync(versionedExtractPath)) {
      throw new Error(`Downloaded package directory not found: ${versionedExtractPath}`);
    }
    
    if (verbose) {
      console.log(`Package ${packageName} v${targetVersion} downloaded successfully!`);
      console.log(`Package available at: ${versionedExtractPath}`);
    }
    
    return {
      version: targetVersion,
      path: versionedExtractPath
    };
    
  } catch (error) {
    throw new Error(`Error downloading/extracting NuGet package ${packageName} with nuget.exe: ${error.message}`);
  }
}

/**
 * Download and extract a NuGet package
 * @param {string} packageName - Name of the NuGet package
 * @param {string} [extractPath] - Directory to extract the package to (defaults to ".winsdk/packages" in project root)
 * @param {Object} options - Options object
 * @param {string} [options.version] - Specific version to download (defaults to latest stable)
 * @param {string} [options.downloadPath] - Directory to download the .nupkg file to (defaults to extractPath)
 * @param {boolean} [options.keepDownload=false] - Whether to keep the downloaded .nupkg file after extraction
 * @param {boolean} [options.verbose=true] - Whether to log progress messages
 * @param {boolean} [options.includeExperimental=false] - Whether to include experimental versions when finding latest version
 * @param {boolean} [options.cleanupOldVersions=true] - Whether to remove old versions of the package after downloading
 * @returns {Promise<Object>} - Returns an object with version and path of the extracted package
 */
async function downloadAndExtractNuGetPackage(packageName, extractPath, options = {}) {
  const {
    ...restOptions
  } = options;

  // Use nuget.exe if requested and available
  try {
    return await downloadAndExtractNuGetPackageWithExe(packageName, extractPath, restOptions);
  } catch (error) {
    if (restOptions.verbose !== false) {
      console.warn(`nuget.exe failed: ${error.message}`);
    }
  }
}

/**
 * Get the path of a downloaded NuGet package
 * @param {string} packageName - Name of the NuGet package
 * @param {string} [extractPath] - Base directory where packages are extracted (defaults to ".winsdk/packages" in project root)
 * @param {string} [version] - Specific version to find (if not provided, returns latest downloaded version)
 * @returns {string|null} - Returns the full path to the package or null if not found
 */
function getPackagePath(packageName, extractPath, version = null) {
  // Default extractPath to ".winsdk/packages" folder in project root
  if (!extractPath) {
    try {
      const projectRoot = getProjectRootDir();
      extractPath = path.join(projectRoot, ".winsdk", "packages");
    } catch (error) {
      throw new Error(`Could not determine project root directory (no package.json found). Please specify an extractPath explicitly. Original error: ${error.message}`);
    }
  }
  
  if (version) {
    // Check for specific version using nuget.exe format: PackageName.Version
    const versionPath = path.join(extractPath, `${packageName}.${version}`);
    return fs.existsSync(versionPath) ? versionPath : null;
  } else {
    // Find latest version by scanning all PackageName.* folders
    try {
      const items = fs.readdirSync(extractPath, { withFileTypes: true });
      const packageDirs = items
        .filter(item => item.isDirectory())
        .map(item => item.name)
        .filter(name => name.startsWith(`${packageName}.`))
        .map(name => {
          const version = name.substring(packageName.length + 1);
          return { name, version };
        });
      
      if (packageDirs.length === 0) {
        return null;
      }
      
      // Sort by version and get the latest
      const latestPackage = packageDirs.sort((a, b) => compareVersions(a.version, b.version)).pop();
      return path.join(extractPath, latestPackage.name);
    } catch (error) {
      return null;
    }
  }
}

module.exports = {
  getNuGetPackageVersions,
  compareVersions,
  getLatestVersion,
  downloadFile,
  extractZip,
  downloadAndExtractNuGetPackage,
  downloadAndExtractNuGetPackageWithExe,
  ensureNuGetExe,
  getPackagePath,
  cleanupOldPackageVersions
};
