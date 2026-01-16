const fs = require('fs').promises;
const fsSync = require('fs');
const path = require('path');
const { callWinappCli: callWinappCli } = require('./winapp-cli-utils');

/**
 * Adds MSIX identity information from an appxmanifest.xml file to an executable's embedded manifest
 * @param {string} exePath - Path to the executable file
 * @param {string} appxManifestPath - Path to the appxmanifest.xml file containing MSIX identity data
 * @param {Object} options - Optional configuration
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 */
async function addMsixIdentityToExe(exePath, appxManifestPath, options = {}) {
  const { verbose = false } = options;
  
  if (verbose) {
    console.log('Adding MSIX identity to executable using native CLI...');
  }

  // Build arguments for native CLI
  const args = ['create-debug-identity', exePath];
  
  // Add manifest argument if provided
  if (appxManifestPath) {
    args.push('--manifest', appxManifestPath);
  }

  args.push('--no-install');

  // Add optional arguments
  if (verbose) {
    args.push('--verbose');
  }
  
  // Call native CLI
  await callWinappCli(args, { verbose });
  
  return {
    success: true,
  };
}

/**
 * Adds MSIX identity to the Electron debug process
 * @param {Object} options - Configuration options
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 */
async function addElectronDebugIdentity(options = {}) {
  const { verbose = false } = options;
  
  if (verbose) {
    console.log('� Adding MSIX debug identity to Electron...');
  }
  
  
  try {
    // Step 1: Make a backup of electron.exe
    const electronExePath = path.join(process.cwd(), 'node_modules', 'electron', 'dist', 'electron.exe');
    const electronBackupPath = path.join(process.cwd(), 'node_modules', 'electron', 'dist', 'electron.backup.exe');

    if (!fsSync.existsSync(electronExePath)) {
      throw new Error(`Electron executable not found at: ${electronExePath}`);
    }
    
    if (verbose) {
      console.log('💾 Creating backup of electron.exe...');
    }
    
    // Create backup if it doesn't exist, or if the current exe is newer than the backup
    if (!fsSync.existsSync(electronBackupPath) || 
        fsSync.statSync(electronExePath).mtime > fsSync.statSync(electronBackupPath).mtime) {
      await fs.copyFile(electronExePath, electronBackupPath);
      
      if (verbose) {
        console.log(`✅ Backup created: ${electronBackupPath}`);
      }
    } else {
      if (verbose) {
        console.log('⏭️  Backup already exists and is up to date');
      }
    }
    
    // Step 2: Use the native CLI to create debug identity (handles manifest generation, identity addition, and package registration)
    if (verbose) {
      console.log('🔐 Creating debug identity using native CLI...');
    }
    
    // Build arguments for native CLI
    const args = ['create-debug-identity', electronExePath];
    if (verbose) {
      args.push('--verbose');
    }
    
    await callWinappCli(args, { verbose });
    
    if (verbose) {
      console.log('✅ Debug identity created and package registered successfully');
    }
    
    // Determine the manifest path after CLI execution
    const msixDebugDir = path.resolve('.winapp/debug');
    const manifestPath = path.join(msixDebugDir, 'appxmanifest.xml');
    
    const result = {
      success: true,
      electronExePath,
      backupPath: electronBackupPath,
      manifestPath,
      assetsDir: path.join(msixDebugDir, 'Assets'),
    };
    
    if (verbose) {
      console.log(`📁 Manifest: ${result.manifestPath}`);
    }
    
    return result;
    
  } catch (error) {
    throw new Error(`Failed to add Electron debug identity: ${error.message}`);
  }
}

module.exports = {
  addMsixIdentityToExe,
  addElectronDebugIdentity
};
