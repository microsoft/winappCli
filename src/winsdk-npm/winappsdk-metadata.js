const path = require('path');
const fs = require('fs');
const { getPackagePath } = require('./nuget-utils');

/**
 * Parse version string and return major.minor version
 * @param {string} version - Version string (e.g., "1.8.0.1234")
 * @returns {string} - Major.minor version (e.g., "1.8")
 */
function getMajorMinorVersion(version) {
  if (!version) return null;
  const parts = version.split('.');
  return parts.length >= 2 ? `${parts[0]}.${parts[1]}` : version;
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
    
    if (aPart < bPart) return -1;
    if (aPart > bPart) return 1;
  }
  
  return 0;
}

/**
 * Extract .nuspec content from .nupkg file using Node.js built-in modules
 * @param {string} nupkgPath - Path to the .nupkg file
 * @returns {Promise<string>} - Content of the .nuspec file
 */
async function extractNuspecFromNupkg(nupkgPath) {
  return new Promise((resolve, reject) => {
    try {
      const zlib = require('zlib');
      const fs = require('fs');
      
      // Read the .nupkg file (which is a ZIP file)
      const zipBuffer = fs.readFileSync(nupkgPath);
      
      // ZIP file structure parsing
      let offset = 0;
      const zipEntries = [];
      
      // Find the end of central directory record
      let eocdOffset = -1;
      for (let i = zipBuffer.length - 22; i >= 0; i--) {
        if (zipBuffer.readUInt32LE(i) === 0x06054b50) { // End of central directory signature
          eocdOffset = i;
          break;
        }
      }
      
      if (eocdOffset === -1) {
        throw new Error('Invalid ZIP file: End of central directory not found');
      }
      
      // Read central directory info
      const centralDirSize = zipBuffer.readUInt32LE(eocdOffset + 12);
      const centralDirOffset = zipBuffer.readUInt32LE(eocdOffset + 16);
      
      // Parse central directory entries
      let currentOffset = centralDirOffset;
      while (currentOffset < centralDirOffset + centralDirSize) {
        if (zipBuffer.readUInt32LE(currentOffset) !== 0x02014b50) { // Central directory file header signature
          break;
        }
        
        const filenameLength = zipBuffer.readUInt16LE(currentOffset + 28);
        const extraFieldLength = zipBuffer.readUInt16LE(currentOffset + 30);
        const commentLength = zipBuffer.readUInt16LE(currentOffset + 32);
        const compressionMethod = zipBuffer.readUInt16LE(currentOffset + 10);
        const compressedSize = zipBuffer.readUInt32LE(currentOffset + 20);
        const uncompressedSize = zipBuffer.readUInt32LE(currentOffset + 24);
        const localHeaderOffset = zipBuffer.readUInt32LE(currentOffset + 42);
        
        const filename = zipBuffer.subarray(
          currentOffset + 46, 
          currentOffset + 46 + filenameLength
        ).toString('utf8');
        
        if (filename.endsWith('.nuspec')) {
          zipEntries.push({
            filename,
            compressionMethod,
            compressedSize,
            uncompressedSize,
            localHeaderOffset
          });
        }
        
        currentOffset += 46 + filenameLength + extraFieldLength + commentLength;
      }
      
      if (zipEntries.length === 0) {
        throw new Error('No .nuspec file found in package');
      }
      
      // Extract the first .nuspec file found
      const entry = zipEntries[0];
      
      // Read local file header to get to the actual file data
      const localHeaderOffset = entry.localHeaderOffset;
      if (zipBuffer.readUInt32LE(localHeaderOffset) !== 0x04034b50) { // Local file header signature
        throw new Error('Invalid ZIP file: Local file header signature not found');
      }
      
      const localFilenameLength = zipBuffer.readUInt16LE(localHeaderOffset + 26);
      const localExtraFieldLength = zipBuffer.readUInt16LE(localHeaderOffset + 28);
      
      const fileDataOffset = localHeaderOffset + 30 + localFilenameLength + localExtraFieldLength;
      const compressedData = zipBuffer.subarray(fileDataOffset, fileDataOffset + entry.compressedSize);
      
      // Decompress the data if needed
      let fileContent;
      if (entry.compressionMethod === 0) {
        // No compression (stored)
        fileContent = compressedData.toString('utf8');
      } else if (entry.compressionMethod === 8) {
        // Deflate compression
        try {
          const decompressed = zlib.inflateRawSync(compressedData);
          fileContent = decompressed.toString('utf8');
        } catch (error) {
          throw new Error(`Failed to decompress .nuspec file: ${error.message}`);
        }
      } else {
        throw new Error(`Unsupported compression method: ${entry.compressionMethod}`);
      }
      
      if (!fileContent || !fileContent.trim()) {
        throw new Error('Extracted .nuspec file is empty');
      }
      
      resolve(fileContent.trim());
      
    } catch (error) {
      reject(new Error(`Failed to extract .nuspec: ${error.message}`));
    }
  });
}

/**
 * Extract actual version from NuGet version range (e.g., "[1.8.250509001-experimental]" -> "1.8.250509001-experimental")
 * @param {string} versionRange - Version range string from .nuspec
 * @returns {string} - Actual version string
 */
function extractVersionFromRange(versionRange) {
  if (!versionRange) return versionRange;
  
  // Handle NuGet version range formats:
  // [1.0] - exact version
  // [1.0,) - minimum version
  // (,1.0] - maximum version  
  // [1.0,2.0] - range
  // Just extract the first version number
  const match = versionRange.match(/[\[\(]([^,\]\)]+)/);
  if (match) {
    return match[1];
  }
  
  // If no brackets, assume it's already a clean version
  return versionRange;
}

/**
 * Parse .nuspec XML content to extract dependency information
 * @param {string} nuspecContent - XML content of .nuspec file
 * @param {boolean} includeAllDeps - Whether to include all dependencies, not just WindowsAppSDK ones
 * @returns {Object} - Parsed dependency information
 */
function parseNuspecDependencies(nuspecContent, includeAllDeps = false) {
  const dependencies = [];
  
  try {
    // Extract dependencies using multiple regex patterns to handle different XML formats
    const patterns = [
      // Self-closing dependency tags
      /<dependency[^>]+id="([^"]+)"[^>]+version="([^"]+)"[^>]*\/>/g,
      /<dependency[^>]+version="([^"]+)"[^>]+id="([^"]+)"[^>]*\/>/g,
      // Opening dependency tags  
      /<dependency[^>]+id="([^"]+)"[^>]+version="([^"]+)"[^>]*>/g,
      /<dependency[^>]+version="([^"]+)"[^>]+id="([^"]+)"[^>]*>/g
    ];
    
    for (const pattern of patterns) {
      let match;
      while ((match = pattern.exec(nuspecContent)) !== null) {
        let id, version;
        
        if (pattern.toString().includes('id="([^"]+)"[^>]+version="([^"]+)"')) {
          id = match[1];
          version = match[2];
        } else {
          version = match[1];
          id = match[2];
        }
        
        if (id && version) {
          // Clean up version ranges
          const cleanVersion = extractVersionFromRange(version);
          
          // Include all dependencies if requested, or just WindowsAppSDK ones
          if (includeAllDeps || id.startsWith('Microsoft.WindowsAppSDK.')) {
            // Avoid duplicates
            if (!dependencies.some(dep => dep.id === id)) {
              dependencies.push({ id, version: cleanVersion });
            }
          }
        }
      }
    }
    
    // If no dependencies found with regex, try a simpler approach
    if (dependencies.length === 0) {
      const lines = nuspecContent.split('\n');
      for (const line of lines) {
        const targetFilter = includeAllDeps ? 'Microsoft.' : 'Microsoft.WindowsAppSDK.';
        
        if (line.includes(targetFilter) && line.includes('id=') && line.includes('version=')) {
          const idMatch = line.match(/id="([^"]*Microsoft\.[^"]*)"/);
          const versionMatch = line.match(/version="([^"]*)"/);
          
          if (idMatch && versionMatch) {
            const id = idMatch[1];
            const version = extractVersionFromRange(versionMatch[1]);
            
            if (includeAllDeps || id.startsWith('Microsoft.WindowsAppSDK.')) {
              if (!dependencies.some(dep => dep.id === id)) {
                dependencies.push({ id, version });
              }
            }
          }
        }
      }
    }
    
  } catch (error) {
    console.warn(`Warning: Could not parse .nuspec dependencies: ${error.message}`);
  }
  
  return { dependencies };
}

/**
 * Find the best metadata path from a directory, preferring higher OS versions
 * @param {string} metadataDir - Directory to search for metadata
 * @param {boolean} verbose - Show verbose output
 * @returns {string|null} - Best metadata path or null if none found
 */
function findBestMetadataPath(metadataDir, verbose = false) {
  try {
    if (!fs.existsSync(metadataDir)) {
      return null;
    }

    const entries = fs.readdirSync(metadataDir);
    
    // First, check if there are .winmd files directly in this directory
    const hasDirectWinmdFiles = entries.some(file => file.endsWith('.winmd'));
    if (hasDirectWinmdFiles) {
      if (verbose) {
        console.log(`  📁 Found .winmd files directly in ${metadataDir}`);
      }
      return metadataDir;
    }

    // Look for subdirectories that might contain .winmd files
    const subDirs = entries.filter(entry => {
      const entryPath = path.join(metadataDir, entry);
      try {
        return fs.statSync(entryPath).isDirectory();
      } catch {
        return false;
      }
    });

    if (subDirs.length === 0) {
      return null;
    }

    // Filter subdirectories that actually contain .winmd files
    const validSubDirs = [];
    for (const subDir of subDirs) {
      const subDirPath = path.join(metadataDir, subDir);
      try {
        const subDirEntries = fs.readdirSync(subDirPath);
        const hasWinmdFiles = subDirEntries.some(file => file.endsWith('.winmd'));
        if (hasWinmdFiles) {
          validSubDirs.push({ name: subDir, path: subDirPath });
        }
      } catch {
        // Ignore errors reading subdirectory
      }
    }

    if (validSubDirs.length === 0) {
      return null;
    }

    // Sort by version number (if they look like version numbers)
    const versionPattern = /^(\d+)\.(\d+)\.(\d+)\.(\d+)$/;
    const versionDirs = validSubDirs.filter(dir => versionPattern.test(dir.name));
    
    if (versionDirs.length > 0) {
      // Sort version directories by version number (descending - highest first)
      versionDirs.sort((a, b) => {
        const aMatch = a.name.match(versionPattern);
        const bMatch = b.name.match(versionPattern);
        
        if (aMatch && bMatch) {
          for (let i = 1; i <= 4; i++) {
            const aPart = parseInt(aMatch[i]);
            const bPart = parseInt(bMatch[i]);
            if (aPart !== bPart) {
              return bPart - aPart; // Descending order
            }
          }
        }
        return 0;
      });
      
      const bestVersionDir = versionDirs[0];
      if (verbose) {
        console.log(`  📁 Selected highest OS version: ${bestVersionDir.name} from ${versionDirs.map(d => d.name).join(', ')}`);
      }
      return bestVersionDir.path;
    }

    // If no version-like directories, just return the first valid one
    const firstValidDir = validSubDirs[0];
    if (verbose) {
      console.log(`  📁 Selected metadata directory: ${firstValidDir.name}`);
    }
    return firstValidDir.path;

  } catch (error) {
    if (verbose) {
      console.warn(`⚠️  Error finding best metadata path in ${metadataDir}: ${error.message}`);
    }
    return null;
  }
}

/**
 * Find metadata files in a package directory
 * @param {string} packagePath - Path to the package directory  
 * @param {boolean} verbose - Show verbose output
 * @returns {string|null} - Path to metadata folder or null if not found
 */
function findPackageMetadata(packagePath, verbose = false) {
  const possibleMetadataPaths = [
    path.join(packagePath, 'lib'),
    path.join(packagePath, 'metadata'),
    path.join(packagePath, 'lib', 'uap10.0'),
    path.join(packagePath, 'lib', 'net6.0-windows10.0.17763.0'),
    path.join(packagePath, 'ref'),
    path.join(packagePath, 'ref', 'net6.0'),
    path.join(packagePath, 'winmd'),
    path.join(packagePath, 'lib', 'winrt45')
  ];
  
  for (const metadataPath of possibleMetadataPaths) {
    if (fs.existsSync(metadataPath)) {
      // Use the helper to find the best metadata path
      const bestMetadataPath = findBestMetadataPath(metadataPath, verbose);
      if (bestMetadataPath) {
        return bestMetadataPath;
      }
    }
  }
  
  return null;
}

/**
 * Recursively resolve all dependencies for a given package
 * @param {string} packageId - The package ID to resolve dependencies for
 * @param {string} packageVersion - The package version
 * @param {Set} visited - Set of already visited packages to avoid cycles
 * @param {boolean} verbose - Show verbose output
 * @returns {Set} - Set of all dependency objects with metadata paths
 */
async function resolveAllDependencies(packageId, packageVersion, visited = new Set(), verbose = false) {
  const allDeps = new Set();
  const packageKey = `${packageId}@${packageVersion}`;
  
  // Avoid cycles
  if (visited.has(packageKey)) {
    return allDeps;
  }
  visited.add(packageKey);
  
  if (verbose) {
    console.log(`🔍 Resolving dependencies for ${packageId} v${packageVersion}`);
  }
  
  try {
    // Get the package path
    const packagePath = getPackagePath(packageId, null, packageVersion);
    if (!packagePath) {
      if (verbose) {
        console.warn(`⚠️  Package not found: ${packageId} v${packageVersion}`);
      }
      return allDeps;
    }
    
    // Try to find .nupkg file to extract .nuspec
    const nupkgPath = path.join(packagePath, `${packageId}.${packageVersion}.nupkg`);
    
    if (fs.existsSync(nupkgPath)) {
      // Extract and parse .nuspec for ALL dependencies (not just WindowsAppSDK)
      const nuspecContent = await extractNuspecFromNupkg(nupkgPath);
      const { dependencies } = parseNuspecDependencies(nuspecContent, true); // Include all deps
      
      if (verbose && dependencies.length > 0) {
        console.log(`  📦 Found ${dependencies.length} dependencies:`);
        dependencies.forEach(dep => {
          console.log(`    • ${dep.id} v${dep.version}`);
        });
      }
      
      // Check if current package has metadata
      const metadataPath = findPackageMetadata(packagePath, verbose);
      if (metadataPath) {
        allDeps.add({
          id: packageId,
          version: packageVersion,
          metadataPath: metadataPath
        });
        if (verbose) {
          console.log(`  ✅ Found metadata for ${packageId}: ${metadataPath}`);
        }
      }
      
      // Recursively resolve dependencies
      for (const dep of dependencies) {
        if (dep.id.startsWith('Microsoft.')) { // Focus on Microsoft packages
          const subDeps = await resolveAllDependencies(dep.id, dep.version, visited, verbose);
          for (const subDep of subDeps) {
            allDeps.add(subDep);
          }
        }
      }
    } else {
      // No .nupkg file, but still check for metadata in the package
      const metadataPath = findPackageMetadata(packagePath, verbose);
      if (metadataPath) {
        allDeps.add({
          id: packageId,
          version: packageVersion,
          metadataPath: metadataPath
        });
        if (verbose) {
          console.log(`  ✅ Found metadata for ${packageId}: ${metadataPath}`);
        }
      }
    }
    
  } catch (error) {
    if (verbose) {
      console.warn(`⚠️  Error resolving dependencies for ${packageId}: ${error.message}`);
    }
  }
  
  return allDeps;
}

/**
 * Find WindowsAppSDK subpackages for version 1.8+
 * @param {string} winAppSdkPath - Path to the main WindowsAppSDK package
 * @param {string} version - Version of the WindowsAppSDK
 * @param {boolean} verbose - Show verbose output
 * @returns {Promise<string[]>} - Array of paths to subpackage metadata folders
 */
async function findWindowsAppSDKSubpackages(winAppSdkPath, version, verbose = false) {
  try {
    if (verbose) {
      console.log(`🚀 Starting recursive dependency resolution for WindowsAppSDK v${version}`);
    }
    
    // Use recursive dependency resolution starting from WindowsAppSDK
    const packageId = 'Microsoft.WindowsAppSDK';
    const allDeps = await resolveAllDependencies(packageId, version, new Set(), verbose);
    
    // Convert Set to array of metadata paths
    const metadataPaths = Array.from(allDeps).map(dep => dep.metadataPath);
    
    if (verbose) {
      console.log(`📊 Recursive resolution complete. Found ${allDeps.size} packages with metadata:`);
      for (const dep of allDeps) {
        console.log(`  ✅ ${dep.id} v${dep.version} -> ${dep.metadataPath}`);
      }
    }
    
    // If we didn't find any packages via recursive resolution, fall back to directory scan
    if (metadataPaths.length === 0) {
      if (verbose) {
        console.log(`⚠️  No packages found via recursive resolution, falling back to directory scan`);
      }
      const { getProjectRootDir } = require('./utils');
      const projectRoot = getProjectRootDir();
      const packagesDir = path.join(projectRoot, '.winsdk', 'packages');
      return findSubpackagesByDirectoryScan(packagesDir, verbose);
    }
    
    return metadataPaths;
    
  } catch (error) {
    if (verbose) {
      console.error(`❌ Error in recursive dependency resolution: ${error.message}`);
    }
    
    // Fall back to directory scan on any error
    const { getProjectRootDir } = require('./utils');
    const projectRoot = getProjectRootDir();
    const packagesDir = path.join(projectRoot, '.winsdk', 'packages');
    return findSubpackagesByDirectoryScan(packagesDir, verbose);
  }
}

/**
 * Fallback method: Find WindowsAppSDK subpackages by scanning the packages directory
 * @param {string} packagesDir - Path to the packages directory
 * @param {boolean} verbose - Show verbose output
 * @returns {string[]} - Array of paths to subpackage metadata folders
 */
function findSubpackagesByDirectoryScan(packagesDir, verbose = false) {
  const metadataPaths = [];
  
  try {
    if (!fs.existsSync(packagesDir)) {
      return metadataPaths;
    }
    
    const entries = fs.readdirSync(packagesDir);
    
    // Look for WindowsAppSDK subpackage directories (exclude the main package)
    const subpackagePattern = /^Microsoft\.WindowsAppSDK\./;
    const mainPackagePattern = /^Microsoft\.WindowsAppSDK\.\d+\.\d+/;
    
    for (const entry of entries) {
      if (subpackagePattern.test(entry) && !mainPackagePattern.test(entry)) {
        const entryPath = path.join(packagesDir, entry);
        const stat = fs.statSync(entryPath);
        
        if (stat.isDirectory()) {
          // Look for folders with metadata - try multiple possible paths
          const possibleMetadataPaths = [
            path.join(entryPath, 'lib'),
            path.join(entryPath, 'metadata'),
            path.join(entryPath, 'lib', 'uap10.0'),
            path.join(entryPath, 'lib', 'net6.0-windows10.0.17763.0'),
            path.join(entryPath, 'ref'),
            path.join(entryPath, 'ref', 'net6.0')
          ];
          
          for (const metadataPath of possibleMetadataPaths) {
            if (fs.existsSync(metadataPath)) {
              // Use the helper to find the best metadata path
              const bestMetadataPath = findBestMetadataPath(metadataPath, verbose);
              if (bestMetadataPath) {
                metadataPaths.push(bestMetadataPath);
                if (verbose) {
                  console.log(`✅ Found subpackage metadata: ${entry} at ${bestMetadataPath}`);
                }
                break; // Use the first valid path found
              }
            }
          }
        }
      }
    }
    
  } catch (error) {
    if (verbose) {
      console.warn(`⚠️  Error scanning for subpackages: ${error.message}`);
    }
  }
  
  return metadataPaths;
}

/**
 * Get WindowsAppSDK metadata paths based on version
 * 
 * This function handles the change in WindowsAppSDK packaging between versions:
 * - v1.7 and below (Legacy): Metadata is stored in the main package's lib folder
 * - v1.8+ (New): Metadata is distributed across multiple subpackages, and the main 
 *   package only contains dependency information in its .nuspec file
 * 
 * @param {string} winAppSdkPath - Path to the main WindowsAppSDK package
 * @param {string} version - Version of the WindowsAppSDK
 * @param {boolean} verbose - Show verbose output
 * @returns {Promise<string[]>} - Array of paths to use for CppWinRT input
 */
async function getWindowsAppSDKMetadataPaths(winAppSdkPath, version, verbose = false) {
  const majorMinor = getMajorMinorVersion(version);
  
  if (verbose) {
    console.log(`🔍 Detecting WindowsAppSDK packaging method for version ${version} (${majorMinor})`);
  }
  
  // Check if this is version 1.8+ (new packaging method)
  if (majorMinor && compareVersions(majorMinor, '1.8') >= 0) {
    if (verbose) {
      console.log(`📦 Using new packaging method (v1.8+) for WindowsAppSDK`);
    }
    
    return await findWindowsAppSDKSubpackages(winAppSdkPath, version, verbose);
    
  } else {
    if (verbose) {
      console.log(`📦 Using legacy packaging method (v1.7 and below) for WindowsAppSDK`);
    }
    
    // Legacy method - look for lib folder in main package
    const libDir = path.join(winAppSdkPath, 'lib');
    if (fs.existsSync(libDir)) {
      // Use the helper to find the best metadata path, which will handle version subfolders
      const bestLibPath = findBestMetadataPath(libDir, verbose);
      if (bestLibPath) {
        if (verbose) {
          console.log(`✅ Found legacy WindowsAppSDK metadata at: ${bestLibPath}`);
        }
        return [bestLibPath];
      }

      // Fallback to the old logic if the helper doesn't find anything
      const libVersions = fs.readdirSync(libDir)
        .filter(item => fs.statSync(path.join(libDir, item)).isDirectory())
        .filter(item => item.startsWith('uap'))
        .sort()
        .reverse(); // Get latest version first
      
      if (libVersions.length > 0) {
        const libPath = path.join(libDir, libVersions[0]);
        if (verbose) {
          console.log(`✅ Found legacy WindowsAppSDK lib version: ${libVersions[0]}`);
        }
        return [libPath];
      }
    }
    
    return [];
  }
}

module.exports = {
  getWindowsAppSDKMetadataPaths
};
