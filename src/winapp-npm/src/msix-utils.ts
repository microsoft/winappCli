import * as fs from 'fs/promises';
import * as fsSync from 'fs';
import * as path from 'path';
import { callWinappCli } from './winapp-cli-utils';

export interface MsixIdentityOptions {
  verbose?: boolean;
}

export interface MsixIdentityResult {
  success: boolean;
}

export interface ElectronDebugIdentityResult {
  success: boolean;
  electronExePath: string;
  backupPath: string;
  manifestPath: string;
  assetsDir: string;
}

export interface ClearElectronDebugIdentityResult {
  success: boolean;
  electronExePath: string;
  restoredFromBackup: boolean;
}

/**
 * Adds package identity information from an appxmanifest.xml file to an executable's embedded manifest
 * @param exePath - Path to the executable file
 * @param appxManifestPath - Path to the appxmanifest.xml file containing package identity data
 * @param options - Optional configuration
 */
export async function addMsixIdentityToExe(
  exePath: string,
  appxManifestPath?: string,
  options: MsixIdentityOptions = {}
): Promise<MsixIdentityResult> {
  const { verbose = false } = options;

  if (verbose) {
    console.log('Adding package identity to executable using native CLI...');
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
  await callWinappCli(args);

  return {
    success: true,
  };
}

/**
 * Adds package identity to the Electron debug process
 * @param options - Configuration options
 */
export async function addElectronDebugIdentity(
  options: MsixIdentityOptions = {}
): Promise<ElectronDebugIdentityResult> {
  const { verbose = false } = options;

  if (verbose) {
    console.log('🔐 Adding MSIX debug identity to Electron...');
  }

  try {
    // Step 1: Handle backup of electron.exe
    const electronDistPath = path.join(process.cwd(), 'node_modules', 'electron', 'dist');
    const electronExePath = path.join(electronDistPath, 'electron.exe');
    const electronBackupPath = path.join(electronDistPath, 'electron.backup.exe');
    const electronBackupVersionPath = path.join(electronDistPath, 'electron.backup.version');
    const electronPackageJsonPath = path.join(process.cwd(), 'node_modules', 'electron', 'package.json');

    if (!fsSync.existsSync(electronExePath)) {
      throw new Error(`Electron executable not found at: ${electronExePath}`);
    }

    // Get current Electron version from package.json
    let currentElectronVersion: string | undefined;
    if (fsSync.existsSync(electronPackageJsonPath)) {
      try {
        const packageJson = JSON.parse(fsSync.readFileSync(electronPackageJsonPath, 'utf-8'));
        currentElectronVersion = packageJson.version;
      } catch {
        // Ignore errors reading package.json
      }
    }

    // Get backup version if it exists
    let backupVersion: string | undefined;
    if (fsSync.existsSync(electronBackupVersionPath)) {
      try {
        backupVersion = fsSync.readFileSync(electronBackupVersionPath, 'utf-8').trim();
      } catch {
        // Ignore errors reading version file
      }
    }

    const backupExists = fsSync.existsSync(electronBackupPath);
    let versionMismatch = false;
    if (backupExists) {
      if (currentElectronVersion && backupVersion) {
        versionMismatch = currentElectronVersion !== backupVersion;
      } else if (currentElectronVersion && !backupVersion) {
        // Treat missing backup version as a mismatch when current version is known
        versionMismatch = true;
      }
    }

    if (backupExists && !versionMismatch) {
      // Backup exists and version matches (or we couldn't determine versions)
      // Restore from backup to ensure we're working with a clean electron.exe
      if (verbose) {
        console.log('💾 Restoring electron.exe from backup...');
      }
      await fs.copyFile(electronBackupPath, electronExePath);

      if (verbose) {
        console.log('✅ Restored clean electron.exe from backup');
      }
    } else {
      // No backup exists, or Electron was updated - create a fresh backup
      if (verbose) {
        if (versionMismatch) {
          console.log(
            `💾 Electron version changed (${backupVersion} → ${currentElectronVersion}), creating new backup...`
          );
        } else {
          console.log('💾 Creating backup of electron.exe...');
        }
      }
      await fs.copyFile(electronExePath, electronBackupPath);

      // Store the version alongside the backup
      if (currentElectronVersion) {
        await fs.writeFile(electronBackupVersionPath, currentElectronVersion, 'utf-8');
      }

      if (verbose) {
        console.log(`✅ Backup created: ${electronBackupPath}`);
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

    await callWinappCli(args);

    if (verbose) {
      console.log('✅ Debug identity created and package registered successfully');
    }

    // Determine the manifest path after CLI execution
    const msixDebugDir = path.resolve('.winapp/debug');
    const manifestPath = path.join(msixDebugDir, 'appxmanifest.xml');

    const result: ElectronDebugIdentityResult = {
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
    const err = error as Error;
    throw new Error(`Failed to add Electron debug identity: ${err.message}`);
  }
}

/**
 * Clears/removes package identity from the Electron debug process by restoring from backup
 * @param options - Configuration options
 */
export async function clearElectronDebugIdentity(
  options: MsixIdentityOptions = {}
): Promise<ClearElectronDebugIdentityResult> {
  const { verbose = false } = options;

  if (verbose) {
    console.log('🧹 Clearing Electron debug identity...');
  }

  try {
    const electronDistPath = path.join(process.cwd(), 'node_modules', 'electron', 'dist');
    const electronExePath = path.join(electronDistPath, 'electron.exe');
    const electronBackupPath = path.join(electronDistPath, 'electron.backup.exe');
    const electronBackupVersionPath = path.join(electronDistPath, 'electron.backup.version');

    if (!fsSync.existsSync(electronExePath)) {
      throw new Error(`Electron executable not found at: ${electronExePath}`);
    }

    let restoredFromBackup = false;

    if (fsSync.existsSync(electronBackupPath)) {
      // Restore from backup
      if (verbose) {
        console.log('💾 Restoring electron.exe from backup...');
      }
      await fs.copyFile(electronBackupPath, electronExePath);

      // Remove the backup files
      await fs.unlink(electronBackupPath);
      if (fsSync.existsSync(electronBackupVersionPath)) {
        await fs.unlink(electronBackupVersionPath);
      }

      restoredFromBackup = true;

      if (verbose) {
        console.log('✅ Restored clean electron.exe from backup');
        console.log('🗑️  Removed backup files');
      }
    } else {
      if (verbose) {
        console.log('ℹ️  No backup found - electron.exe may already be clean');
      }
    }

    return {
      success: true,
      electronExePath,
      restoredFromBackup,
    };
  } catch (error) {
    const err = error as Error;
    throw new Error(`Failed to clear Electron debug identity: ${err.message}`);
  }
}
