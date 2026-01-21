import * as fs from 'fs/promises';
import * as fsSync from 'fs';
import * as path from 'path';
import { execSync } from 'child_process';
import { checkAndInstallDotnetSdk, checkAndInstallVisualStudioBuildTools } from './dependency-utils';

export interface GenerateCsAddonOptions {
  name?: string;
  projectRoot?: string;
  verbose?: boolean;
}

export interface GenerateCsAddonResult {
  success: boolean;
  addonName: string;
  addonPath: string;
  needsTerminalRestart: boolean;
  files: string[];
}

// C# reserved keywords
const CSHARP_KEYWORDS = [
  'abstract',
  'as',
  'base',
  'bool',
  'break',
  'byte',
  'case',
  'catch',
  'char',
  'checked',
  'class',
  'const',
  'continue',
  'decimal',
  'default',
  'delegate',
  'do',
  'double',
  'else',
  'enum',
  'event',
  'explicit',
  'extern',
  'false',
  'finally',
  'fixed',
  'float',
  'for',
  'foreach',
  'goto',
  'if',
  'implicit',
  'in',
  'int',
  'interface',
  'internal',
  'is',
  'lock',
  'long',
  'namespace',
  'new',
  'null',
  'object',
  'operator',
  'out',
  'override',
  'params',
  'private',
  'protected',
  'public',
  'readonly',
  'ref',
  'return',
  'sbyte',
  'sealed',
  'short',
  'sizeof',
  'stackalloc',
  'static',
  'string',
  'struct',
  'switch',
  'this',
  'throw',
  'true',
  'try',
  'typeof',
  'uint',
  'ulong',
  'unchecked',
  'unsafe',
  'ushort',
  'using',
  'virtual',
  'void',
  'volatile',
  'while',
];

/**
 * Generates C# addon files for an Electron project
 * @param options - Configuration options
 */
export async function generateCsAddonFiles(options: GenerateCsAddonOptions = {}): Promise<GenerateCsAddonResult> {
  const { name = 'csAddon', projectRoot = process.cwd(), verbose = true } = options;

  let needsTerminalRestart = false;

  try {
    // Validate addon name (should be a valid C# namespace/class name)
    validateAddonName(name);

    await checkAndInstallVisualStudioBuildTools(false); // Don't show verbose build tools info
    // We don't set needsTerminalRestart for VS installation because so far the tools that need it know how to find it.

    // Check if dotnet SDK is available and offer to install if missing
    const dotnetInstalled = await checkAndInstallDotnetSdk('10', false); // Don't show verbose SDK info
    if (dotnetInstalled) {
      needsTerminalRestart = true;
    }

    // Check if addon already exists
    const addonDir = path.join(projectRoot, name);
    if (fsSync.existsSync(addonDir)) {
      throw new Error(
        `Addon directory already exists: ${name}. Please choose a different name or remove the existing directory.`
      );
    }

    if (verbose) {
      console.log(`📁 Creating addon directory: ${name}`);
    }

    // Create the addon directory
    await fs.mkdir(addonDir, { recursive: true });

    // Copy and process template files
    await copyTemplateFiles(name, addonDir, projectRoot, false); // Don't show individual file messages

    // Install required npm packages
    await installNodeApiDotnet(projectRoot, false); // Don't show package install details

    // Add build scripts to package.json
    await addCsBuildScripts(name, projectRoot, false); // Don't show script addition messages

    // Update .gitignore
    await updateGitignore(projectRoot, false); // Don't show gitignore update message

    const result: GenerateCsAddonResult = {
      success: true,
      addonName: name,
      addonPath: addonDir,
      needsTerminalRestart: needsTerminalRestart,
      files: [path.join(addonDir, `${name}.csproj`), path.join(addonDir, 'addon.cs'), path.join(addonDir, 'README.md')],
    };

    return result;
  } catch (error) {
    const err = error as Error;
    throw new Error(`Failed to generate C# addon files: ${err.message}`);
  }
}

/**
 * Validates that the addon name is suitable for C# namespace/class
 * @param name - Addon name to validate
 */
function validateAddonName(name: string): void {
  // Must start with a letter or underscore
  if (!/^[a-zA-Z_]/.test(name)) {
    throw new Error('Addon name must start with a letter or underscore');
  }

  // Must contain only letters, numbers, and underscores
  if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(name)) {
    throw new Error('Addon name must contain only letters, numbers, and underscores');
  }

  // Should not be a C# keyword
  if (CSHARP_KEYWORDS.includes(name.toLowerCase())) {
    throw new Error(`Addon name cannot be a C# keyword: ${name}`);
  }
}

/**
 * Copies and processes template files to the addon directory and project root
 * @param addonName - Name of the addon
 * @param addonDir - Target addon directory
 * @param projectRoot - Project root directory
 * @param verbose - Enable verbose logging
 */
async function copyTemplateFiles(
  addonName: string,
  addonDir: string,
  projectRoot: string,
  verbose: boolean
): Promise<void> {
  const templateDir = path.join(__dirname, '../cs-addon-template');

  if (!fsSync.existsSync(templateDir)) {
    throw new Error(`Template directory not found: ${templateDir}`);
  }

  // Process and copy .csproj file
  const csprojTemplate = path.join(templateDir, 'AddonTemplate.csproj.template');
  const csprojTarget = path.join(addonDir, `${addonName}.csproj`);

  let csprojContent = await fs.readFile(csprojTemplate, 'utf8');
  csprojContent = csprojContent.replace(/{AddonName}/g, addonName);

  await fs.writeFile(csprojTarget, csprojContent, 'utf8');

  if (verbose) {
    console.log(`📄 Created ${addonName}.csproj`);
  }

  // Process and copy addon.cs file
  const addonCsTemplate = path.join(templateDir, 'addon.cs.template');
  const addonCsTarget = path.join(addonDir, 'addon.cs');

  let addonCsContent = await fs.readFile(addonCsTemplate, 'utf8');
  addonCsContent = addonCsContent.replace(/{AddonName}/g, addonName);

  await fs.writeFile(addonCsTarget, addonCsContent, 'utf8');

  if (verbose) {
    console.log(`📄 Created addon.cs`);
  }

  // Process and copy README.md file
  const readmeTemplate = path.join(templateDir, 'README.md.template');
  const readmeTarget = path.join(addonDir, 'README.md');

  let readmeContent = await fs.readFile(readmeTemplate, 'utf8');
  readmeContent = readmeContent.replace(/{AddonName}/g, addonName);

  await fs.writeFile(readmeTarget, readmeContent, 'utf8');

  if (verbose) {
    console.log(`📄 Created README.md`);
  }

  // Copy Directory.packages.props to project root (if it doesn't exist)
  const packagePropsTarget = path.join(projectRoot, 'Directory.packages.props');
  if (!fsSync.existsSync(packagePropsTarget)) {
    const packagePropsTemplate = path.join(templateDir, 'Directory.packages.props.template');
    await fs.copyFile(packagePropsTemplate, packagePropsTarget);

    if (verbose) {
      console.log(`📄 Created Directory.packages.props`);
    }
  } else {
    if (verbose) {
      console.log(`📄 Directory.packages.props already exists, skipping`);
    }
  }
}

/**
 * Installs node-api-dotnet package
 * @param projectRoot - Root directory of the project
 * @param verbose - Enable verbose logging
 */
async function installNodeApiDotnet(projectRoot: string, verbose: boolean): Promise<void> {
  // Check if package.json exists
  const packageJsonPath = path.join(projectRoot, 'package.json');
  if (!fsSync.existsSync(packageJsonPath)) {
    throw new Error('package.json not found in project root');
  }

  // Read current package.json
  const packageJsonContent = await fs.readFile(packageJsonPath, 'utf8');
  const packageJson = JSON.parse(packageJsonContent);

  // Check if node-api-dotnet is already installed
  const dependencies = packageJson.dependencies || {};
  const devDependencies = packageJson.devDependencies || {};

  if (dependencies['node-api-dotnet'] || devDependencies['node-api-dotnet']) {
    if (verbose) {
      console.log(`✅ node-api-dotnet is already installed`);
    }
    return;
  }

  if (verbose) {
    console.log(`📦 Installing node-api-dotnet...`);
  }

  const installCommand = 'npm install node-api-dotnet';

  try {
    execSync(installCommand, {
      cwd: projectRoot,
      stdio: verbose ? 'inherit' : 'pipe',
      shell: process.env.ComSpec || 'cmd.exe',
    });

    if (verbose) {
      console.log(`✅ node-api-dotnet installed successfully`);
    }
  } catch (error) {
    const err = error as Error;
    throw new Error(`Failed to install node-api-dotnet: ${err.message}`);
  }
}

/**
 * Adds build scripts to package.json
 * @param addonName - Name of the addon
 * @param projectRoot - Root directory of the project
 * @param verbose - Enable verbose logging
 */
async function addCsBuildScripts(addonName: string, projectRoot: string, verbose: boolean): Promise<void> {
  const packageJsonPath = path.join(projectRoot, 'package.json');

  // Read current package.json
  const packageJsonContent = await fs.readFile(packageJsonPath, 'utf8');
  const packageJson = JSON.parse(packageJsonContent);

  // Initialize scripts if it doesn't exist
  if (!packageJson.scripts) {
    packageJson.scripts = {};
  }

  // Add build script - use publish to generate .node file
  const buildScriptName = `build-${addonName}`;
  // Use dotnet publish - RuntimeIdentifier is set in the .csproj with a default
  // Can be overridden with: npm run build-csAddon -- /p:RuntimeIdentifier=win-arm64
  const buildCommand = `dotnet publish ./${addonName}/${addonName}.csproj -c Release`;

  if (packageJson.scripts[buildScriptName]) {
    if (verbose) {
      console.log(`⚠️  Build script '${buildScriptName}' already exists, skipping`);
    }
  } else {
    packageJson.scripts[buildScriptName] = buildCommand;
    if (verbose) {
      console.log(`📝 Added build script: ${buildScriptName}`);
    }
  }

  // Add clean script (without cs- prefix)
  const cleanScriptName = `clean-${addonName}`;
  const cleanCommand = `dotnet clean ./${addonName}/${addonName}.csproj`;

  if (!packageJson.scripts[cleanScriptName]) {
    packageJson.scripts[cleanScriptName] = cleanCommand;
    if (verbose) {
      console.log(`📝 Added clean script: ${cleanScriptName}`);
    }
  }

  // Write back to package.json
  await fs.writeFile(packageJsonPath, JSON.stringify(packageJson, null, 2), 'utf8');
}

/**
 * Updates .gitignore to exclude C# build artifacts
 * @param projectRoot - Root directory of the project
 * @param verbose - Enable verbose logging
 */
async function updateGitignore(projectRoot: string, verbose: boolean): Promise<void> {
  const gitignorePath = path.join(projectRoot, '.gitignore');

  const entriesToAdd = ['', '# C# build artifacts', 'bin/', 'obj/', '*.user', '*.suo'];

  let gitignoreContent = '';
  let exists = false;

  try {
    if (fsSync.existsSync(gitignorePath)) {
      gitignoreContent = await fs.readFile(gitignorePath, 'utf8');
      exists = true;
    }
  } catch {
    // File doesn't exist, we'll create it
  }

  // Check if entries already exist
  const needsUpdate = entriesToAdd.some((entry) => entry && !gitignoreContent.includes(entry));

  if (!needsUpdate && exists) {
    if (verbose) {
      console.log(`✅ .gitignore already contains C# build artifact entries`);
    }
    return;
  }

  // Add entries
  const newContent = gitignoreContent.trim() + '\n' + entriesToAdd.join('\n') + '\n';
  await fs.writeFile(gitignorePath, newContent, 'utf8');

  if (verbose) {
    console.log(`📝 Updated .gitignore with C# build artifact entries`);
  }
}
