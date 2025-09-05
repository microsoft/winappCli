const fs = require('fs').promises;
const fsSync = require('fs');
const path = require('path');
const { execSync } = require('child_process');
const { callWinsdkCli } = require('./winsdk-cli-utils');

/**
 * Adds MSIX identity information from an appxmanifest.xml file to an executable's embedded manifest
 * @param {string} exePath - Path to the executable file
 * @param {string} appxManifestPath - Path to the appxmanifest.xml file containing MSIX identity data
 * @param {Object} options - Optional configuration
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 * @param {string} options.tempDir - Directory for temporary files (default: same as exe directory)
 */
async function addMsixIdentityToExe(exePath, appxManifestPath, options = {}) {
  const { verbose = true, tempDir } = options;
  
  if (verbose) {
    console.log('Adding MSIX identity to executable using native CLI...');
  }

  // Build arguments for native CLI
  const args = ['msix', 'add-identity-to-exe', exePath, appxManifestPath];
  
  // Add optional arguments
  if (tempDir) {
    args.push('--temp-dir', tempDir);
  }
  
  if (verbose) {
    args.push('--verbose');
  }
  
  // Call native CLI
  await callWinsdkCli(args, { verbose });
  
  // Extract identity information for return value (maintains API compatibility)
  try {
    const appxManifestContent = await fs.readFile(appxManifestPath, 'utf8');
    
    const nameMatch = appxManifestContent.match(/<Identity[^>]*Name\s*=\s*["']([^"']*)["']/i);
    const publisherMatch = appxManifestContent.match(/<Identity[^>]*Publisher\s*=\s*["']([^"']*)["']/i);
    const applicationMatch = appxManifestContent.match(/<Application[^>]*Id\s*=\s*["']([^"']*)["'][^>]*>/i);
    
    return {
      success: true,
      packageName: nameMatch ? nameMatch[1] : null,
      publisher: publisherMatch ? publisherMatch[1] : null,
      applicationId: applicationMatch ? applicationMatch[1] : null
    };
  } catch (error) {
    // If we can't parse the manifest for return values, still return success since CLI succeeded
    return {
      success: true,
      packageName: null,
      publisher: null,
      applicationId: null
    };
  }
}

/**
 * Adds MSIX identity to the Electron debug process
 * @param {Object} options - Configuration options
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 */
async function addElectronDebugIdentity(options = {}) {
  const { verbose = true } = options;
  
  if (verbose) {
    console.log('🔧 Adding MSIX identity to Electron debug process...');
  }
  
  const electronExePath = path.join(process.cwd(), 'node_modules', 'electron', 'dist', 'electron.exe');
  const electronBackupPath = path.join(process.cwd(), 'node_modules', 'electron', 'dist', 'electron.backup.exe');
  const msixDebugDir = path.join(process.cwd(), 'msix-debug');
  const manifestPath = path.join(msixDebugDir, 'appxmanifest.xml');
  
  try {
    // Step 1: Make a backup of electron.exe
    if (!fsSync.existsSync(electronExePath)) {
      throw new Error(`Electron executable not found at: ${electronExePath}`);
    }
    
    if (fsSync.existsSync(electronBackupPath)) {
      if (verbose) {
        console.log('⏭️  Backup already exists, skipping backup step');
      }
    } else {
      if (verbose) {
        console.log('💾 Creating backup of electron.exe...');
      }
      await fs.copyFile(electronExePath, electronBackupPath);
      if (verbose) {
        console.log(`✅ Backup created: ${electronBackupPath}`);
      }
    }
    
    // Step 2: Generate sparse appxmanifest and assets if they don't exist
    if (fsSync.existsSync(manifestPath)) {
      if (verbose) {
        console.log('⏭️  Manifest already exists, skipping generation step');
      }
    } else {
      if (verbose) {
        console.log('📄 Generating sparse MSIX manifest and assets using native CLI...');
      }
      
      // Use the native CLI to generate the manifest and assets
      await callWinsdkCli([
        'msix', 'init',
        '--sparse',
        '--output', msixDebugDir,
        '--executable', 'node_modules/electron/dist/electron.exe'
      ], { verbose });
      
      if (verbose) {
        console.log(`✅ Sparse manifest generated: ${manifestPath}`);
      }
    }
    
    // Step 3: Add identity to electron.exe
    if (verbose) {
      console.log('🔐 Adding MSIX identity to electron.exe...');
    }
    
    const identityResult = await addMsixIdentityToExe(electronExePath, manifestPath, {
      verbose: verbose
    });
    
    if (verbose) {
      console.log('✅ MSIX identity added to electron.exe');
    }
    
    // Step 4: Unregister any existing package first
    if (verbose) {
      console.log('🗑️  Checking for existing package...');
    }
    
    try {
      // Get package name from manifest to check and unregister it
      const manifestContent = await fs.readFile(manifestPath, 'utf8');
      const nameMatch = manifestContent.match(/<Identity[^>]*Name\s*=\s*["']([^"']*)["']/i);
      
      if (nameMatch) {
        const packageName = nameMatch[1];
        
        // First check if package exists
        const checkCommand = `powershell -Command "Get-AppxPackage -Name '${packageName}'"`;
        
        try {
          const checkResult = execSync(checkCommand, { stdio: 'pipe' });
          const checkOutput = checkResult.toString().trim();
          
          if (checkOutput && checkOutput.length > 0) {
            // Package exists, remove it
            if (verbose) {
              console.log(`📦 Found existing package '${packageName}', removing it...`);
            }
            
            const unregisterCommand = `powershell -Command "Get-AppxPackage -Name '${packageName}' | Remove-AppxPackage"`;
            execSync(unregisterCommand, { stdio: verbose ? 'inherit' : 'pipe' });
            
            if (verbose) {
              console.log('✅ Existing package unregistered successfully');
            }
          } else {
            // No package found, proceed silently
            if (verbose) {
              console.log('ℹ️  No existing package found');
            }
          }
        } catch (checkError) {
          // If check fails, package likely doesn't exist
          if (verbose) {
            console.log('ℹ️  No existing package found');
          }
        }
      }
    } catch (error) {
      if (verbose) {
        console.log('⚠️  Note: Could not check for existing package');
      }
    }
    
    // Step 5: Register the manifest with external location
    if (verbose) {
      console.log('📋 Registering sparse package with external location...');
    }
    
    const currentDir = process.cwd();
    const registerCommand = `powershell -Command "Add-AppxPackage -Path '${manifestPath}' -ExternalLocation '${currentDir}' -Register -ForceUpdateFromAnyVersion"`;
    
    execSync(registerCommand, { stdio: verbose ? 'inherit' : 'pipe' });
    
    if (verbose) {
      console.log('✅ Sparse package registered successfully');
    }
    
    const result = {
      success: true,
      electronExePath,
      backupPath: electronBackupPath,
      manifestPath,
      assetsDir: path.join(msixDebugDir, 'Assets'),
      packageName: identityResult.packageName,
      publisher: identityResult.publisher,
      applicationId: identityResult.applicationId
    };
    
    if (verbose) {
      console.log('🎉 Electron debug identity setup completed successfully!');
      console.log(`📦 Package: ${result.packageName}`);
      console.log(`👤 Publisher: ${result.publisher}`);
      console.log(`🆔 App ID: ${result.applicationId}`);
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
