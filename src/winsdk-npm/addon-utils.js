const fs = require('fs').promises;
const fsSync = require('fs');
const path = require('path');
const { execSync } = require('child_process');

/**
 * Generates addon files for an Electron project
 * @param {Object} options - Configuration options
 * @param {string} options.name - Name of the addon (default: 'nativeWindowsAddon')
 * @param {string} options.projectRoot - Root directory of the project (default: current working directory)
 * @param {boolean} options.verbose - Enable verbose logging (default: true)
 */
async function generateAddonFiles(options = {}) {
  const { 
    name = 'nativeWindowsAddon', 
    projectRoot = process.cwd(), 
    verbose = true 
  } = options;

  if (verbose) {
    console.log(`🔧 Generating addon files for: ${name}`);
  }

  try {
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

    const result = {
      success: true,
      addonName: addonDirName,
      addonPath: addonDir,
      files: [
        path.join(addonDir, 'binding.gyp'),
        path.join(addonDir, `${addonDirName}.cc`)
      ]
    };

    if (verbose) {
      console.log(`✅ Addon files generated successfully!`);
      console.log(`📦 Addon name: ${result.addonName}`);
      console.log(`📁 Addon path: ${result.addonPath}`);
      console.log(`🔨 Build script: build-${result.addonName}`);
    }

    return result;

  } catch (error) {
    throw new Error(`Failed to generate addon files: ${error.message}`);
  }
}

/**
 * Finds a unique addon name by appending numbers if needed
 * @param {string} baseName - Base name for the addon
 * @param {string} projectRoot - Root directory of the project
 * @returns {Promise<string>} - Unique addon name
 */
async function findUniqueAddonName(baseName, projectRoot) {
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
 * @param {string} addonName - Name of the addon
 * @param {string} addonDir - Target addon directory
 * @param {boolean} verbose - Enable verbose logging
 */
async function copyTemplateFiles(addonName, addonDir, verbose) {
  const templateDir = path.join(__dirname, 'addon-template');
  
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
 * @param {string} projectRoot - Root directory of the project
 * @param {boolean} verbose - Enable verbose logging
 */
async function installRequiredPackages(projectRoot, verbose) {
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
  const missingPackages = requiredPackages.filter(pkg => !devDependencies[pkg]);

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
      throw new Error(`Failed to install packages: ${error.message}`);
    }
  } else {
    if (verbose) {
      console.log(`✅ All required packages are already installed`);
    }
  }
}

/**
 * Adds build script to package.json
 * @param {string} addonName - Name of the addon
 * @param {string} projectRoot - Root directory of the project
 * @param {boolean} verbose - Enable verbose logging
 */
async function addBuildScript(addonName, projectRoot, verbose) {
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

module.exports = {
  generateAddonFiles
};