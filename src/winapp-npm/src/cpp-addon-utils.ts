import * as fs from 'fs/promises';
import * as fsSync from 'fs';
import * as path from 'path';
import { execSync } from 'child_process';
import { checkAndInstallPython, checkAndInstallVisualStudioBuildTools } from './dependency-utils';

export interface GenerateCppAddonOptions {
  name?: string;
  projectRoot?: string;
  verbose?: boolean;
}

export interface GenerateCppAddonResult {
  success: boolean;
  addonName: string;
  addonPath: string;
  needsTerminalRestart: boolean;
  files: string[];
}

/**
 * Generates addon files for an Electron project
 * @param options - Configuration options
 */
export async function generateCppAddonFiles(options: GenerateCppAddonOptions = {}): Promise<GenerateCppAddonResult> {
  const { name = 'nativeWindowsAddon', projectRoot = process.cwd(), verbose = true } = options;

  let needsTerminalRestart = false;

  try {
    // Check for Python and offer to install if missing
    const pythonInstalled = await checkAndInstallPython(false); // Don't show verbose Python info
    if (pythonInstalled) {
      needsTerminalRestart = true;
    }

    // Check for Visual Studio Build Tools and offer to install if missing
    await checkAndInstallVisualStudioBuildTools(false); // Don't show verbose VS info
    // VS tools are typically found without PATH restart, so we don't set needsTerminalRestart for it

    if (verbose) {
      console.log(`🔧 Generating addon files for: ${name}`);
    }

    // Find a unique addon directory name
    const addonDirName = await findUniqueAddonName(name, projectRoot);
    const addonDir = path.join(projectRoot, addonDirName);

    if (verbose) {
      console.log(`📁 Creating addon directory: ${addonDirName}`);
    }

    // Create the addon directory
    await fs.mkdir(addonDir, { recursive: true });

    // Copy template files
    await copyTemplateFiles(addonDirName, addonDir, verbose);

    // Install required npm packages
    await installRequiredPackages(projectRoot, verbose);

    // Add build script to package.json
    await addBuildScript(addonDirName, projectRoot, verbose);

    const result: GenerateCppAddonResult = {
      success: true,
      addonName: addonDirName,
      addonPath: addonDir,
      needsTerminalRestart: needsTerminalRestart,
      files: [path.join(addonDir, 'binding.gyp'), path.join(addonDir, `${addonDirName}.cc`)],
    };

    if (verbose) {
      console.log(`✅ Addon files generated successfully!`);
      console.log(`📦 Addon name: ${result.addonName}`);
      console.log(`📁 Addon path: ${result.addonPath}`);
      console.log(`🔨 Build script: build-${result.addonName}`);
    }

    return result;
  } catch (error) {
    const err = error as Error;
    throw new Error(`Failed to generate addon files: ${err.message}`);
  }
}

/**
 * Finds a unique addon name by appending numbers if needed
 * @param baseName - Base name for the addon
 * @param projectRoot - Root directory of the project
 * @returns Unique addon name
 */
async function findUniqueAddonName(baseName: string, projectRoot: string): Promise<string> {
  let addonName = baseName;
  let counter = 1;

  while (fsSync.existsSync(path.join(projectRoot, addonName))) {
    addonName = `${baseName}${counter}`;
    counter++;
  }

  return addonName;
}

/**
 * Copies template files to the addon directory
 * @param addonName - Name of the addon
 * @param addonDir - Target addon directory
 * @param verbose - Enable verbose logging
 */
async function copyTemplateFiles(addonName: string, addonDir: string, verbose: boolean): Promise<void> {
  const templateDir = path.join(__dirname, '../addon-template');

  if (!fsSync.existsSync(templateDir)) {
    throw new Error(`Template directory not found: ${templateDir}`);
  }

  // Copy and process binding.gyp
  const bindingGypTemplate = path.join(templateDir, 'binding.gyp');
  const bindingGypTarget = path.join(addonDir, 'binding.gyp');

  let bindingGypContent = await fs.readFile(bindingGypTemplate, 'utf8');
  bindingGypContent = bindingGypContent.replace(/{addon-name}/g, addonName);

  await fs.writeFile(bindingGypTarget, bindingGypContent, 'utf8');

  if (verbose) {
    console.log(`📄 Created binding.gyp`);
  }

  // Copy and rename addon.cc
  const addonCcTemplate = path.join(templateDir, 'addon.cc');
  const addonCcTarget = path.join(addonDir, `${addonName}.cc`);

  await fs.copyFile(addonCcTemplate, addonCcTarget);

  if (verbose) {
    console.log(`📄 Created ${addonName}.cc`);
  }
}

/**
 * Installs required npm packages
 * @param projectRoot - Root directory of the project
 * @param verbose - Enable verbose logging
 */
async function installRequiredPackages(projectRoot: string, verbose: boolean): Promise<void> {
  const requiredPackages = ['nan', 'node-addon-api', 'node-gyp'];

  // Check if package.json exists
  const packageJsonPath = path.join(projectRoot, 'package.json');
  if (!fsSync.existsSync(packageJsonPath)) {
    throw new Error('package.json not found in project root');
  }

  // Read current package.json
  const packageJsonContent = await fs.readFile(packageJsonPath, 'utf8');
  const packageJson = JSON.parse(packageJsonContent);

  // Check which packages are missing
  const devDependencies = packageJson.devDependencies || {};
  const missingPackages = requiredPackages.filter((pkg) => !devDependencies[pkg]);

  if (missingPackages.length > 0) {
    if (verbose) {
      console.log(`📦 Installing missing packages: ${missingPackages.join(', ')}`);
    }

    const installCommand = `npm install --save-dev ${missingPackages.join(' ')}`;

    try {
      execSync(installCommand, { cwd: projectRoot, stdio: verbose ? 'inherit' : 'pipe' });

      if (verbose) {
        console.log(`✅ Packages installed successfully`);
      }
    } catch (error) {
      const err = error as Error;
      throw new Error(`Failed to install packages: ${err.message}`);
    }
  } else {
    if (verbose) {
      console.log(`✅ All required packages are already installed`);
    }
  }
}

/**
 * Adds build script to package.json
 * @param addonName - Name of the addon
 * @param projectRoot - Root directory of the project
 * @param verbose - Enable verbose logging
 */
async function addBuildScript(addonName: string, projectRoot: string, verbose: boolean): Promise<void> {
  const packageJsonPath = path.join(projectRoot, 'package.json');

  // Read current package.json
  const packageJsonContent = await fs.readFile(packageJsonPath, 'utf8');
  const packageJson = JSON.parse(packageJsonContent);

  // Initialize scripts if it doesn't exist
  if (!packageJson.scripts) {
    packageJson.scripts = {};
  }

  // Find a unique script name
  let scriptName = `build-${addonName}`;
  let counter = 1;

  while (packageJson.scripts[scriptName]) {
    scriptName = `build-${addonName}${counter}`;
    counter++;
  }

  // Add the build script
  packageJson.scripts[scriptName] = `node-gyp clean configure build --directory=${addonName}`;

  // Write back to package.json
  await fs.writeFile(packageJsonPath, JSON.stringify(packageJson, null, 2), 'utf8');

  if (verbose) {
    console.log(`📝 Added build script: ${scriptName}`);
  }
}
