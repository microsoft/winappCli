#!/usr/bin/env node

const path = require('path');
const fs = require('fs');
const { downloadAndExtractNuGetPackage, getPackagePath } = require('./nuget-utils');
const { execSync } = require('child_process');
const { 
  loadConfig, 
  saveConfig, 
  getPackageVersion, 
  configExists, 
  updateConfigWithDownloadResults 
} = require('./config-utils');

// List of SDK packages to download
const SDK_PACKAGES = [
  'Microsoft.Windows.CppWinRT',
  'Microsoft.Windows.SDK.BuildTools',
  'Microsoft.WindowsAppSDK',
  'Microsoft.Windows.ImplementationLibrary',
  'Microsoft.Windows.SDK.CPP',
  'Microsoft.Windows.SDK.CPP.x64',
  'Microsoft.Windows.SDK.CPP.arm64'
];

/**
 * Download all SDK packages
 * @param {Object} options - Options for downloading
 * @param {string} [options.outputDir] - Directory to download packages to (defaults to .winsdk/packages in project root)
 * @param {boolean} [options.skipExisting=true] - Skip packages that are already downloaded
 * @param {boolean} [options.keepDownloads=false] - Keep the .nupkg files after extraction
 * @param {boolean} [options.verbose=true] - Show progress messages
 * @param {boolean} [options.useConfig=true] - Use configuration file for version management
 * @param {boolean} [options.cleanupOldVersions=true] - Remove old versions of packages after downloading new ones
 * @param {boolean} [options.includeExperimental=false] - Include experimental/prerelease packages from NuGet
 * @returns {Promise<Object>} - Object with package names as keys and downloaded versions as values
 */
async function downloadAllSDKPackages(options = {}) {
  const {
    outputDir = null, // Let nuget-utils handle the default path
    skipExisting = true,
    keepDownloads = false,
    verbose = true,
    useConfig = true,
    cleanupOldVersions = true,
    includeExperimental = false
  } = options;

  // Load or create configuration
  let config = null;
  let configPath = null;
  
  if (useConfig) {
    try {
      config = loadConfig();
      if (verbose) {
        const isNewConfig = !configExists();
        if (isNewConfig) {
          console.log('📄 Created new configuration file: winsdk.yaml');
        } else {
          console.log('📄 Loaded configuration from: winsdk.yaml');
        }
      }
    } catch (error) {
      if (verbose) {
        console.warn(`⚠️  Could not load configuration: ${error.message}`);
        console.log('📄 Proceeding without configuration...');
      }
    }
  }

  const results = {};
  const errors = [];

  if (verbose) {
    console.log(`Downloading ${SDK_PACKAGES.length} SDK packages...`);
    console.log('Packages:', SDK_PACKAGES.join(', '));
    if (includeExperimental) {
      console.log('🧪 Including experimental/prerelease versions');
    }
    console.log('');
  }

  for (const packageName of SDK_PACKAGES) {
    try {
      // Determine the version to download
      let targetVersion = null;
      if (config) {
        targetVersion = getPackageVersion(config, packageName);
        if (targetVersion && verbose) {
          console.log(`🔒 Using configured version for ${packageName}: v${targetVersion}`);
        }
      }

      // Check if package already exists by trying to get its path
      if (skipExisting) {
        try {
          const existingPath = getPackagePath(packageName, outputDir, targetVersion);
          if (existingPath) {
            // Extract version from existing path for config update
            const folderName = path.basename(existingPath);
            const existingVersion = folderName.substring(packageName.length + 1);
            
            if (verbose) {
              const versionMsg = targetVersion ? ` v${targetVersion}` : '';
              console.log(`⏭️  Skipping ${packageName}${versionMsg} (already exists)`);
            }
            
            // Store the existing version info for config update
            results[packageName] = {
              version: existingVersion,
              path: existingPath,
              skipped: true
            };
            continue;
          }
        } catch (error) {
          // If getPackagePath fails, it means we can't determine project root or the package doesn't exist
          // We'll proceed with downloading, and if it fails due to project root, it will be caught below
        }
      }

      if (verbose) {
        const versionMsg = targetVersion ? ` v${targetVersion}` : '';
        console.log(`📦 Downloading ${packageName}${versionMsg}...`);
      }

      const result = await downloadAndExtractNuGetPackage(
        packageName,
        outputDir,
        {
          version: targetVersion, // Use the configured version if available
          keepDownload: keepDownloads,
          verbose: false, // We're handling our own verbose output
          cleanupOldVersions,
          includeExperimental
        }
      );

      results[packageName] = result;
      
      if (verbose) {
        console.log(`✅ ${packageName} v${result.version} downloaded successfully`);
      }

    } catch (error) {
      const errorMsg = `Failed to download ${packageName}: ${error.message}`;
      errors.push(errorMsg);
      
      if (verbose) {
        console.error(`❌ ${errorMsg}`);
      }
      
      results[packageName] = 'error';
    }
  }

  // Update configuration with downloaded versions
  if (useConfig && config) {
    try {
      updateConfigWithDownloadResults(results);
      if (verbose) {
        console.log('📄 Updated configuration with download results');
      }
    } catch (error) {
      if (verbose) {
        console.warn(`⚠️  Could not update configuration: ${error.message}`);
      }
    }
  }

  if (verbose) {
    console.log('\n📋 Download Summary:');
    console.log('===================');
    
    const successful = Object.entries(results).filter(([, result]) => 
      result !== 'error' && 
      (typeof result === 'object' && result.version && !result.skipped)
    );
    const existing = Object.entries(results).filter(([, result]) => 
      typeof result === 'object' && result.skipped
    );
    const failed = Object.entries(results).filter(([, result]) => result === 'error');
    
    console.log(`✅ Successfully downloaded: ${successful.length}`);
    if (existing.length > 0) {
      console.log(`⏭️  Skipped (existing): ${existing.length}`);
    }
    if (failed.length > 0) {
      console.log(`❌ Failed: ${failed.length}`);
    }
    
    if (successful.length > 0) {
      console.log('\nSuccessfully downloaded packages:');
      successful.forEach(([pkg, result]) => {
        console.log(`  • ${pkg} v${result.version}`);
      });
    }
    
    if (existing.length > 0) {
      console.log('\nExisting packages:');
      existing.forEach(([pkg, result]) => {
        console.log(`  • ${pkg} v${result.version}`);
      });
    }
    
    if (failed.length > 0) {
      console.log('\nFailed packages:');
      failed.forEach(([pkg]) => {
        console.log(`  • ${pkg}`);
      });
    }
  }

  if (errors.length > 0) {
    throw new Error(`Some packages failed to download:\n${errors.join('\n')}`);
  }

  return results;
}

/**
 * Find all .winmd files recursively in a directory
 * @param {string} dir - Directory to search
 * @param {Array<string>} [results=[]] - Array to accumulate results
 * @returns {Array<string>} - Array of .winmd file paths
 */
function findWinmdFiles(dir, results = []) {
  if (!fs.existsSync(dir)) {
    return results;
  }

  try {
    const items = fs.readdirSync(dir, { withFileTypes: true });
    
    for (const item of items) {
      const fullPath = path.join(dir, item.name);
      
      if (item.isDirectory()) {
        findWinmdFiles(fullPath, results);
      } else if (item.isFile() && item.name.endsWith('.winmd')) {
        results.push(fullPath);
      }
    }
  } catch (error) {
    // Skip directories that can't be read
  }
  
  return results;
}

/**
 * Collect all component metadata files from all SDK packages
 * @param {string} packagesDir - Path to the packages directory
 * @param {boolean} [verbose=true] - Show progress messages
 * @returns {Array<string>} - Array of .winmd file paths
 */
function collectWinmdFiles(packagesDir, verbose = true) {
  const winmdFiles = [];
  
  if (!fs.existsSync(packagesDir)) {
    if (verbose) {
      console.log(`⚠️  Packages directory not found: ${packagesDir}`);
    }
    return winmdFiles;
  }

  // Search patterns based on the CMake example
  const searchPatterns = [
    '**/metadata/*.winmd',              // Standard metadata location
    '**/metadata/10.0.18362.0/*.winmd', // Non-standard paths
    '**/lib/*.winmd',                   // WebView2 metadata and others
    '**/lib/uap10.0/*.winmd',          // Older WindowsAppSDK metadata
    '**/lib/uap10.0.18362/*.winmd',    // Older WindowsAppSDK metadata
    '**/c/References/**/*.winmd'        // Windows Platform SDK references
  ];

  if (verbose) {
    console.log('🔍 Searching for .winmd files using patterns:');
    searchPatterns.forEach(pattern => {
      console.log(`  • ${pattern}`);
    });
  }

  // Search all subdirectories in the packages folder
  try {
    const packageFolders = fs.readdirSync(packagesDir, { withFileTypes: true })
      .filter(item => item.isDirectory())
      .map(item => path.join(packagesDir, item.name));

    for (const packageDir of packageFolders) {
      const packageName = path.basename(packageDir);
      
      if (verbose) {
        console.log(`\n� Searching package: ${packageName}`);
      }

      // Apply each search pattern to this package directory
      for (const pattern of searchPatterns) {
        const searchPath = path.join(packageDir, pattern.replace('**/', ''));
        const baseDir = path.dirname(searchPath);
        const fileName = path.basename(searchPath);
        
        // Find files matching the pattern
        const foundFiles = findWinmdFilesWithPattern(baseDir, fileName);
        
        if (foundFiles.length > 0) {
          winmdFiles.push(...foundFiles);
          if (verbose) {
            console.log(`  ✓ Found ${foundFiles.length} .winmd files in ${path.relative(packageDir, baseDir)}`);
          }
        }
      }
    }
  } catch (error) {
    if (verbose) {
      console.warn(`⚠️  Error reading packages directory: ${error.message}`);
    }
  }

  // Remove duplicates and sort
  const uniqueFiles = [...new Set(winmdFiles)].sort();
  
  if (verbose) {
    console.log(`\n📋 Total unique .winmd files found: ${uniqueFiles.length}`);
    if (uniqueFiles.length > 0) {
      console.log('Found files:');
      uniqueFiles.forEach((file, index) => {
        const relativePath = path.relative(packagesDir, file);
        console.log(`  ${index + 1}. ${relativePath}`);
      });
    }
  }

  return uniqueFiles;
}

/**
 * Find .winmd files matching a pattern in a directory
 * @param {string} baseDir - Base directory to search
 * @param {string} pattern - File pattern (e.g., '*.winmd')
 * @param {Array<string>} [results=[]] - Array to accumulate results
 * @returns {Array<string>} - Array of matching .winmd file paths
 */
function findWinmdFilesWithPattern(baseDir, pattern, results = []) {
  if (!fs.existsSync(baseDir)) {
    return results;
  }

  try {
    const items = fs.readdirSync(baseDir, { withFileTypes: true });
    
    for (const item of items) {
      const fullPath = path.join(baseDir, item.name);
      
      if (item.isDirectory()) {
        // Recursively search subdirectories
        findWinmdFilesWithPattern(fullPath, pattern, results);
      } else if (item.isFile() && item.name.endsWith('.winmd')) {
        // Check if file matches pattern
        if (pattern === '*.winmd' || item.name === pattern || pattern.includes('*')) {
          results.push(fullPath);
        }
      }
    }
  } catch (error) {
    // Skip directories that can't be read
  }
  
  return results;
}

/**
 * Run CppWinRT to generate projection headers
 * @param {Object} options - Options for CppWinRT generation
 * @param {string} [options.outputDir] - Output directory for generated headers (defaults to .winsdk/generated/include in project root)
 * @param {boolean} [options.verbose=true] - Show progress messages
 * @returns {Promise<void>}
 */
async function runCppWinRT(options = {}) {
  const {
    outputDir = null, // Will be determined based on project root
    verbose = true
  } = options;

  try {
    if (verbose) {
      console.log('\n🔧 Running CppWinRT to generate projection headers...');
    }

    // Determine output directory if not provided
    let finalOutputDir = outputDir;
    if (!finalOutputDir) {
      const { getProjectRootDir } = require('./utils');
      try {
        const projectRoot = getProjectRootDir();
        finalOutputDir = path.join(projectRoot, '.winsdk', 'generated', 'include');
      } catch (error) {
        throw new Error(`Could not determine project root directory for CppWinRT output. Please specify an outputDir explicitly. Original error: ${error.message}`);
      }
    }

    // Ensure packages are downloaded by checking if any core package exists
    const corePackagePath = getPackagePath('Microsoft.Windows.CppWinRT');
    if (!corePackagePath) {
      if (verbose) {
        console.log('📦 Packages not found, downloading first...');
      }
      await downloadAllSDKPackages({ verbose });
    }

    // Get the packages directory by getting any package path and going up one level
    const cppWinrtPackagePath = getPackagePath('Microsoft.Windows.CppWinRT');
    if (!cppWinrtPackagePath) {
      throw new Error('Required SDK packages not found. Please run setup first.');
    }
    
    const packagesDir = path.dirname(cppWinrtPackagePath);
    
    // Find cppwinrt.exe
    const cppwinrtExePath = path.join(cppWinrtPackagePath, 'bin', 'cppwinrt.exe');
    if (!fs.existsSync(cppwinrtExePath)) {
      throw new Error(`cppwinrt.exe not found at ${cppwinrtExePath}`);
    }

    // Collect all .winmd files from all SDK packages
    const winmdFiles = collectWinmdFiles(packagesDir, verbose);
    
    if (winmdFiles.length === 0) {
      throw new Error('No .winmd files found in SDK packages');
    }

    // Ensure output directory exists
    if (!fs.existsSync(finalOutputDir)) {
      fs.mkdirSync(finalOutputDir, { recursive: true });
    }

    if (verbose) {
      console.log(`\n🔧 CppWinRT Configuration:`);
      console.log(`  Executable: ${cppwinrtExePath}`);
      console.log(`  Packages directory: ${packagesDir}`);
      console.log(`  Output directory: ${finalOutputDir}`);
      console.log(`  Input .winmd files: ${winmdFiles.length}`);
    }

    // Build the cppwinrt command arguments
    const args = [
      '-input', 'sdk+', // Start with SDK
      ...winmdFiles.flatMap(winmd => ['-input', `"${winmd}"`]), // Add each .winmd file
      '-optimize',
      '-output', `"${finalOutputDir}"`,
      '-verbose'
    ];

    // Build the full command
    const command = `"${cppwinrtExePath}" ${args.join(' ')}`;
    
    if (verbose) {
      console.log(`\n🚀 Executing CppWinRT command:`);
      console.log(`${command}`);
      console.log('');
    }

    // Execute cppwinrt
    const result = execSync(command, { 
      cwd: __dirname,
      stdio: verbose ? 'inherit' : 'pipe',
      encoding: 'utf8',
      shell: true // Use shell to handle quotes properly
    });

    if (verbose) {
      console.log('✅ CppWinRT generation completed successfully!');
      console.log(`📁 Generated headers available at: ${finalOutputDir}`);
    }

  } catch (error) {
    throw new Error(`CppWinRT generation failed: ${error.message}`);
  }
}

/**
 * Complete SDK setup: download packages and generate CppWinRT headers
 * @param {Object} options - Options for setup
 * @param {string} [options.outputDir] - Directory for packages (defaults to .winsdk/packages in project root)
 * @param {string} [options.cppWinRTOutputDir] - Output directory for generated headers (defaults to .winsdk/generated/include in project root)
 * @param {boolean} [options.skipExisting=true] - Skip packages that are already downloaded
 * @param {boolean} [options.verbose=true] - Show progress messages
 * @param {boolean} [options.updateGitignore=true] - Add .winsdk folder to .gitignore
 * @param {boolean} [options.useConfig=true] - Use configuration file for version management
 * @param {boolean} [options.cleanupOldVersions=true] - Remove old versions of packages after downloading new ones
 * @param {boolean} [options.includeExperimental=false] - Include experimental/prerelease packages from NuGet
 * @returns {Promise<Object>} - Results from package downloads
 */
async function setupSDKs(options = {}) {
  const {
    outputDir = null, // Let nuget-utils handle the default path
    cppWinRTOutputDir = null, // Will be determined based on project root
    skipExisting = true,
    verbose = true,
    updateGitignore = true,
    useConfig = true,
    cleanupOldVersions = true,
    includeExperimental = false
  } = options;

  try {
    if (verbose) {
      console.log('🚀 Starting complete SDK setup...');
    }

    // Download all SDK packages
    const downloadResults = await downloadAllSDKPackages({
      outputDir,
      skipExisting,
      verbose,
      useConfig,
      cleanupOldVersions,
      includeExperimental
    });

    // Clean up and run CppWinRT generation
    if (verbose) {
      console.log('\n🧹 Cleaning CppWinRT output directory...');
    }
    
    // Determine CppWinRT output directory
    let finalCppWinRTOutputDir = cppWinRTOutputDir;
    if (!finalCppWinRTOutputDir) {
      const { getProjectRootDir } = require('./utils');
      try {
        const projectRoot = getProjectRootDir();
        finalCppWinRTOutputDir = path.join(projectRoot, '.winsdk', 'generated', 'include');
      } catch (error) {
        if (verbose) {
          console.warn(`⚠️  Could not determine CppWinRT output directory: ${error.message}`);
        }
      }
    }
    
    // Clean the output directory if it exists
    if (finalCppWinRTOutputDir && fs.existsSync(finalCppWinRTOutputDir)) {
      try {
        fs.rmSync(finalCppWinRTOutputDir, { recursive: true, force: true });
        if (verbose) {
          console.log(`🗑️  Cleaned directory: ${finalCppWinRTOutputDir}`);
        }
      } catch (error) {
        if (verbose) {
          console.warn(`⚠️  Could not clean output directory: ${error.message}`);
        }
      }
    }

    await runCppWinRT({
      outputDir: cppWinRTOutputDir,
      verbose
    });

    // Update .gitignore to exclude .winsdk folder
    if (updateGitignore) {
      try {
        const { getProjectRootDir } = require('./utils');
        const projectRoot = getProjectRootDir();
        const gitignorePath = path.join(projectRoot, '.gitignore');
        
        let gitignoreContent = '';
        let gitignoreExists = false;
        
        // Read existing .gitignore if it exists
        if (fs.existsSync(gitignorePath)) {
          gitignoreContent = fs.readFileSync(gitignorePath, 'utf8');
          gitignoreExists = true;
        }
        
        // Check if .winsdk is already in .gitignore
        const winsdkEntry = '.winsdk/';
        const lines = gitignoreContent.split('\n');
        const hasWinsdkEntry = lines.some(line => line.trim() === winsdkEntry.trim());
        
        if (!hasWinsdkEntry) {
          // Add entries to .gitignore
          let newContent = gitignoreContent + 
                           (gitignoreExists ? '\n' : '') + 
                           '# Windows SDK packages and generated files\n' + 
                           winsdkEntry + '\n';
          
          fs.writeFileSync(gitignorePath, newContent, 'utf8');
          
          if (verbose) {
            console.log(`✅ Added .winsdk/ to .gitignore`);
            console.log(`ℹ️  Note: winsdk.yaml should be committed to track SDK versions`);
          }
        } else if (verbose) {
          console.log(`⏭️  .winsdk/ already exists in .gitignore`);
        }

        if (verbose) {
          console.log(`ℹ️  Note: winsdk.yaml should be committed to track SDK versions`);
        }
      } catch (error) {
        if (verbose) {
          console.warn(`⚠️  Could not update .gitignore: ${error.message}`);
        }
      }
    }

    if (verbose) {
      console.log('\n🎉 Complete SDK setup finished successfully!');
    }

    return downloadResults;

  } catch (error) {
    throw new Error(`SDK setup failed: ${error.message}`);
  }
}

/**
 * Initialize or update the configuration file
 * @param {Object} options - Options for config initialization
 * @param {boolean} [options.force=false] - Overwrite existing config file
 * @param {boolean} [options.verbose=true] - Show progress messages
 * @returns {Promise<Object>} - The configuration object
 */
async function initializeConfig(options = {}) {
  const {
    force = false,
    verbose = true
  } = options;

  try {
    const { getProjectRootDir } = require('./utils');
    const { configExists, loadConfig, saveConfig } = require('./config-utils');
    
    const projectRoot = getProjectRootDir();
    const configPath = path.join(projectRoot, 'winsdk.yaml');
    
    if (configExists() && !force) {
      if (verbose) {
        console.log(`⏭️  Configuration file already exists: ${configPath}`);
        console.log('ℹ️  Use --force to overwrite existing configuration');
      }
      return loadConfig();
    }

    if (verbose) {
      console.log('📄 Initializing configuration file...');
    }

    // Create a fresh config with current package versions if they exist
    const config = loadConfig(); // This creates default if not exists
    const packagesWithVersions = [];

    // Check which packages are already downloaded and get their versions
    for (const packageName of SDK_PACKAGES) {
      try {
        const existingPath = getPackagePath(packageName);
        if (existingPath) {
          // Extract version from the path (format: PackageName.Version)
          const folderName = path.basename(existingPath);
          const version = folderName.substring(packageName.length + 1);
          packagesWithVersions.push({ name: packageName, version });
          
          if (verbose) {
            console.log(`🔍 Found existing package: ${packageName} v${version}`);
          }
        }
      } catch (error) {
        // Package not found, skip
      }
    }

    // Update config with found packages
    config.packages = packagesWithVersions;
    
    const savedPath = saveConfig(config);
    
    if (verbose) {
      console.log(`✅ Configuration file created: ${savedPath}`);
      if (packagesWithVersions.length > 0) {
        console.log(`📦 Detected ${packagesWithVersions.length} existing packages`);
      } else {
        console.log('ℹ️  No existing packages found. Run setup to download packages.');
      }
    }

    return config;
    
  } catch (error) {
    throw new Error(`Failed to initialize configuration: ${error.message}`);
  }
}

/**
 * List package versions from configuration
 * @param {Object} options - Options for listing
 * @param {boolean} [options.verbose=true] - Show detailed output
 * @returns {Promise<Object>} - Configuration object
 */
async function listPackageVersions(options = {}) {
  const {
    verbose = true
  } = options;

  try {
    const { loadConfig } = require('./config-utils');
    const config = loadConfig();
    
    if (verbose) {
      console.log('📋 Current SDK Package Configuration:');
      console.log('====================================');
      
      if (config.packages.length === 0) {
        console.log('No packages configured. Run setup to download packages.');
      } else {
        config.packages.forEach(pkg => {
          console.log(`  • ${pkg.name}: v${pkg.version}`);
        });
      }
    }

    return config;
    
  } catch (error) {
    if (verbose) {
      console.error('❌ No configuration file found. Run setup to create one.');
    }
    throw new Error(`Failed to list package versions: ${error.message}`);
  }
}

/**
 * Update a specific package version in the configuration
 * @param {string} packageName - Name of the package to update
 * @param {string} version - Version to set
 * @param {Object} options - Options for updating
 * @param {boolean} [options.verbose=true] - Show progress messages
 * @returns {Promise<Object>} - Updated configuration object
 */
async function updatePackageVersion(packageName, version, options = {}) {
  const {
    verbose = true
  } = options;

  try {
    const { loadConfig, saveConfig, setPackageVersion } = require('./config-utils');
    
    // Validate package name
    if (!SDK_PACKAGES.includes(packageName)) {
      throw new Error(`Unknown package: ${packageName}. Valid packages: ${SDK_PACKAGES.join(', ')}`);
    }

    const config = loadConfig();
    setPackageVersion(config, packageName, version);
    saveConfig(config);
    
    if (verbose) {
      console.log(`✅ Updated ${packageName} to version ${version}`);
      console.log('ℹ️  Run setup to download the updated package');
    }

    return config;
    
  } catch (error) {
    throw new Error(`Failed to update package version: ${error.message}`);
  }
}

module.exports = {
  downloadAllSDKPackages,
  SDK_PACKAGES,
  runCppWinRT,
  setupSDKs,
  initializeConfig,
  listPackageVersions,
  updatePackageVersion,
  // Re-export cleanup function from nuget-utils
  cleanupOldPackageVersions: require('./nuget-utils').cleanupOldPackageVersions
};

// If this script is run directly, run complete SDK setup
if (require.main === module) {
  // Check if this is being run from postinstall
  const isPostinstall = process.argv.includes('--postinstall');
  
  // Force output to be shown during postinstall
  if (isPostinstall) {
    console.log('\n📦 Setting up Windows SDK packages...\n');
  }
  
  setupSDKs({
    verbose: true,
  })
    .then((results) => {
      if (isPostinstall) {
        console.log('\n🎉 Windows SDK setup completed successfully!');
        console.log('ℹ️  You can run "npx winsdk setup" to update packages later.\n');
      } else {
        console.log('\n🎉 All SDK packages and CppWinRT generation completed!');
        runCppWinRT()
          .then(() => {
            console.log('✅ CppWinRT generation completed successfully!');
          })
          .catch((error) => {
            console.error('💥 CppWinRT generation failed:', error.message);
          });
      }
    })
    .catch((error) => {
      console.error('\n💥 Setup failed:', error.message);
      if (isPostinstall) {
        console.error('ℹ️  You can try running "npx winsdk setup" manually after installation.\n');
      }
      process.exit(1);
    });
}